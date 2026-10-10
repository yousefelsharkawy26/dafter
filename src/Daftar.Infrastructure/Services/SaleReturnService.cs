using Daftar.Domain.Inventory;
using Daftar.Domain.Sales;
using Daftar.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Daftar.Infrastructure.Services;

public sealed record ReturnedLine(int SaleItemId, int LotId, decimal Quantity);

public sealed record ReturnItemRequest(int OriginalSaleItemId, decimal Quantity);

public sealed record SaleReturnDraftResult(int SaleId, IReadOnlyList<ReturnedLine> Lines);

/// <summary>
/// Customer returns.
///
/// A return mirrors the original sale's lines and restocks the SAME lots the goods left from,
/// capped at "sold minus already returned", so a lot cannot be credited twice. Anything that came
/// back unsellable is scrapped separately by <see cref="ScrapRestockedAsync"/>.
/// </summary>
public sealed class SaleReturnService(DaftarContext context)
{
    /// <summary>
    /// Builds a Draft return from an original sale. Only quantities still out are included, so a
    /// second return against the same sale can only take what the first one left.
    /// </summary>
    public async Task<SaleReturnDraftResult> BeginAsync(
        int originalSaleId,
        DateOnly businessDate,
        int userId,
        IReadOnlyList<ReturnItemRequest>? requestedItems = null,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        // A return undoes a sale, so it follows the same permission: a role that may not sell
        // may not create the credit that reverses one.
        context.RequirePermission(Daftar.Domain.Security.Role.Codes.SalesCreate);

        // Product and ProductUnit are needed to build the return lines. There is no lazy loading here,
        // so everything used below has to be loaded up front.
        var original = await context.Sales
            .Include(s => s.Items)
            .ThenInclude(i => i.Lots)
            .Include(s => s.Items)
            .ThenInclude(i => i.Product)
            .Include(s => s.Items)
            .ThenInclude(i => i.ProductUnit)
            .FirstOrDefaultAsync(s => s.Id == originalSaleId, cancellationToken)
            ?? throw new DomainException($"Sale {originalSaleId} does not exist.");

        if (original.Type != SaleType.Invoice)
            throw new DomainException(
                $"Sale {originalSaleId} is a return; only an invoice can be returned.");
        if (!original.IsPosted)
            throw new DomainException(
                $"Sale {originalSaleId} is {original.Status}; only a posted sale can be returned.");

        // How much of each original line has already come back.
        var alreadyReturned = await ReturnedQuantitiesByLineAsync(originalSaleId, cancellationToken);

        var returnSale = new Sale
        {
            Type = SaleType.Return,
            PartyId = original.PartyId,
            LocationId = original.LocationId,
            PriceListId = original.PriceListId,
            OriginalSaleId = original.Id,
            DistributorId = original.DistributorId,
            DistributorSessionId = original.DistributorSessionId,
            DocDate = businessDate,
            Notes = notes ?? $"Return against {original.DocNo}",
        };
        context.Sales.Add(returnSale);
        await context.SaveChangesAsync(cancellationToken);

        var mirrored = new List<ReturnedLine>();

        foreach (var source in original.Items)
        {
            var returnedSoFar = alreadyReturned.GetValueOrDefault(source.Id);
            var product = source.Product ?? throw new DomainException($"Product {source.ProductId} is missing.");
            var unit = source.ProductUnit ?? throw new DomainException($"Unit {source.ProductUnitId} is missing.");

            var factor = unit.Factor > 0m ? unit.Factor : 1m;
            var totalStillOut = Rounding.Quantity(source.Lots.Sum(d => d.BaseQuantity) - returnedSoFar);
            if (totalStillOut <= 0m)
                continue;

            decimal requestedQty;
            if (requestedItems != null)
            {
                var req = requestedItems.FirstOrDefault(r => r.OriginalSaleItemId == source.Id);
                if (req == null || req.Quantity <= 0m)
                    continue;

                var maxUnitQty = Rounding.Quantity(totalStillOut / factor);
                requestedQty = Math.Min(req.Quantity, maxUnitQty);
            }
            else
            {
                requestedQty = Rounding.Quantity(totalStillOut / factor);
            }

            if (requestedQty <= 0m)
                continue;

            var baseQtyNeeded = Rounding.Quantity(requestedQty * factor);

            // Credit at what was charged, not at today's list price.
            var line = returnSale.AddItem(
                product,
                unit,
                quantity: requestedQty,
                listPrice: source.UnitPrice,
                unitPrice: source.UnitPrice,
                originalSaleItemId: source.Id);

            var remainingToAllocate = baseQtyNeeded;
            var lotReturnedSoFar = returnedSoFar;

            foreach (var draw in source.Lots)
            {
                if (remainingToAllocate <= 0m)
                    break;

                var stillOutInLot = Rounding.Quantity(draw.BaseQuantity - lotReturnedSoFar);
                if (stillOutInLot <= 0m)
                {
                    lotReturnedSoFar = Rounding.Quantity(lotReturnedSoFar - draw.BaseQuantity);
                    continue;
                }
                lotReturnedSoFar = 0m;

                var takeFromLot = Math.Min(stillOutInLot, remainingToAllocate);
                if (takeFromLot <= 0m)
                    continue;

                line.AddLot(draw.LotId, takeFromLot, draw.UnitCost);
                mirrored.Add(new ReturnedLine(source.Id, draw.LotId, takeFromLot));
                remainingToAllocate = Rounding.Quantity(remainingToAllocate - takeFromLot);
            }
        }

        if (mirrored.Count == 0)
            throw new DomainException($"Sale {original.DocNo} has nothing left to return.");

        returnSale.Recalculate();
        await context.SaveChangesAsync(cancellationToken);

        return new SaleReturnDraftResult(returnSale.Id, mirrored);
    }

    /// <summary>
    /// Posts the draft, putting the stock back into the same lots and crediting the customer.
    /// Delegates to <see cref="SalePostingService"/>, which writes the SaleReturn movements and the
    /// negative receivable row.
    /// </summary>
    public async Task PostAsync(
        int returnSaleId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        var poster = new SalePostingService(context);
        await poster.PostAsync(returnSaleId, userId, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Writes off returned stock that cannot be sold again — expired, spoiled, or damaged in
    /// transit. Separate from the return so the reason is explicit and reaches the ledger.
    /// </summary>
    public async Task ScrapRestockedAsync(
        int lotId,
        int locationId,
        decimal quantity,
        string reasonCode,
        int userId,
        DateOnly businessDate,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        // Writing off goods is its own decision, separate from returning them, so it carries its
        // own permission rather than inheriting Sales.Create.
        context.RequirePermission(Daftar.Domain.Security.Role.Codes.StockScrap);

        if (quantity <= 0m)
            throw new DomainException("A scrap quantity must be positive.");
        if (string.IsNullOrWhiteSpace(reasonCode))
            throw new DomainException("Scrap needs a reason code.");

        await using TransactionScope tx = await TransactionScope.BeginAsync(context, cancellationToken);
        try
        {
            var lot = await context.Lots.FirstOrDefaultAsync(l => l.Id == lotId, cancellationToken)
                ?? throw new DomainException($"Lot {lotId} does not exist.");

            var balance = await context.StockBalances
                .FirstOrDefaultAsync(b => b.LotId == lotId && b.LocationId == locationId, cancellationToken)
                ?? throw new DomainException($"No stock of lot {lotId} at location {locationId}.");

            balance.Apply(-quantity, DateTime.UtcNow);

            context.InventoryMovements.Add(new InventoryMovement
            {
                OccurredAt = DateTime.UtcNow,
                BusinessDate = businessDate,
                ProductId = lot.ProductId,
                LotId = lotId,
                LocationId = locationId,
                Kind = InventoryMovementKind.Scrap,
                Quantity = -quantity,
                SourceType = MovementSourceType.Manual,
                ReasonCode = reasonCode,
                Note = note,
                CreatedBy = userId,
            });

            await context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Quantity already credited back per original sale line, summed from the return lines'
    /// SaleItemLots rather than the lines themselves: on a partial return the lots are what
    /// actually went back into stock.
    /// </summary>
    private async Task<Dictionary<int, decimal>> ReturnedQuantitiesByLineAsync(
        int originalSaleId,
        CancellationToken cancellationToken)
    {
        var returned = await context.SaleItemLots
            .Where(l => l.SaleItem!.Sale!.OriginalSaleId == originalSaleId
                     && l.SaleItem.Sale!.Status == DocumentStatus.Posted)
            .Select(l => new { OriginalSaleItemId = l.SaleItem!.OriginalSaleItemId, l.BaseQuantity })
            .ToListAsync(cancellationToken);

        return returned
            .Where(r => r.OriginalSaleItemId.HasValue)
            .GroupBy(r => r.OriginalSaleItemId.GetValueOrDefault())
            .ToDictionary(g => g.Key, g => Rounding.Quantity(g.Sum(x => x.BaseQuantity)));
    }
}