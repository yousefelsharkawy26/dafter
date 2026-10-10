using Daftar.Domain.Inventory;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Sales;
using Daftar.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace Daftar.Infrastructure.Services;

public sealed record SalePostingResult(
    int SaleId,
    string DocNo,
    IReadOnlyList<(int LotId, decimal Quantity)> Allocations);

/// <summary>
/// Posting a sale invoice — drawing stock out of specific lots.
///
/// In one transaction: the header takes its DocNo, each line allocates across batches oldest-first,
/// the balances drop, inventory movements are written, and the party's receivable is recorded.
/// Any shortfall aborts the whole post, so a sale can never consume stock that was not there.
///
/// Returns post the mirror image, restocking the same lots.
/// </summary>
public sealed class SalePostingService(DaftarContext context)
{
    public async Task<SalePostingResult> PostAsync(
        int saleId,
        int userId,
        DateOnly? businessDate = null,
        CancellationToken cancellationToken = default)
    {
        context.RequirePermission(Daftar.Domain.Security.Role.Codes.SalesCreate);

        await using TransactionScope tx = await TransactionScope.BeginAsync(context, cancellationToken);
        try
        {
            var result = await PostCoreAsync(saleId, userId, businessDate, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<SalePostingResult> PostCoreAsync(
        int saleId,
        int userId,
        DateOnly? businessDate,
        CancellationToken cancellationToken)
    {
        var sale = await context.Sales
            .Include(s => s.Items)
            .ThenInclude(i => i.Lots)
            .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken)
            ?? throw new DomainException($"Sale {saleId} does not exist.");

        if (!sale.IsDraft)
            throw new DomainException($"Sale {saleId} is already {sale.Status}.");

        var now = DateTime.UtcNow;
        var date = businessDate ?? sale.DocDate;

        var docNo = await NextDocNoAsync(
            sale.Type == SaleType.Return ? DocumentType.SaleReturn : DocumentType.Sale, date.Year, cancellationToken);

        var allocations = new List<(int, decimal)>();

        foreach (var item in sale.Items)
        {
            var isReturn = sale.Type == SaleType.Return;

            // A return restocks the batches the original line drew from, in the amounts it drew.
            if (isReturn)
            {
                var returned = await RestockReturnedLineAsync(sale, item, date, now, userId, cancellationToken);
                allocations.AddRange(returned);
            }
            else
            {
                var balances = await LoadBalancesAsync(item.ProductId, sale.LocationId, cancellationToken);

                // Throws when the sale would go negative or only blocked stock exists.
                item.AllocateFrom(balances);

                foreach (var line in item.Lots)
                {
                    ApplyMovement(
                        lotId: line.LotId,
                        productId: item.ProductId,
                        locationId: sale.LocationId,
                        kind: InventoryMovementKind.Sale,
                        magnitude: line.BaseQuantity,
                        sourceType: MovementSourceType.Sale,
                        sourceId: sale.Id,
                        userId: userId,
                        businessDate: date,
                        nowUtc: now);

                    allocations.Add((line.LotId, line.BaseQuantity));
                }
            }
        }

        sale.Post(docNo, userId, now);

        // The receivable: a sale increases what the customer owes us.
        context.PartyAccountTransactions.Add(new Domain.Finance.PartyAccountTransaction
        {
            PartyId = sale.PartyId,
            OccurredAt = now,
            BusinessDate = date,
            Kind = sale.Type == SaleType.Return
                ? Domain.Finance.PartyLedgerKind.SaleReturn
                : Domain.Finance.PartyLedgerKind.Sale,
            Amount = sale.Type == SaleType.Return ? -sale.Total : sale.Total,
            DueDate = sale.DueDate,
            SourceType = "Sale",
            SourceId = sale.Id,
            CreatedBy = userId,
        });

        await context.SaveChangesAsync(cancellationToken);

        return new SalePostingResult(sale.Id, docNo, allocations);
    }

    /// <summary>
    /// A return line puts stock back into exactly the lots the original line drew from. Returned
    /// quantity is capped by what was sold and not already returned, so a lot cannot be credited twice.
    /// </summary>
    private async Task<List<(int LotId, decimal Quantity)>> RestockReturnedLineAsync(
        Sale sale,
        SaleItem item,
        DateOnly businessDate,
        DateTime now,
        int userId,
        CancellationToken cancellationToken)
    {
        if (item.OriginalSaleItemId is null)
            throw new DomainException($"Return line {item.LineNo} has no OriginalSaleItemId.");

        var result = new List<(int, decimal)>();

        foreach (var draw in item.Lots)
        {
            var lot = await context.Lots.FirstOrDefaultAsync(l => l.Id == draw.LotId, cancellationToken)
                ?? throw new DomainException($"Lot {draw.LotId} does not exist.");

            ApplyMovement(
                lotId: lot.Id,
                productId: item.ProductId,
                locationId: sale.LocationId,
                kind: InventoryMovementKind.SaleReturn,
                magnitude: draw.BaseQuantity,
                sourceType: MovementSourceType.Sale,
                sourceId: sale.Id,
                userId: userId,
                businessDate: businessDate,
                nowUtc: now);

            result.Add((lot.Id, draw.BaseQuantity));
        }

        return result;
    }

    private async Task<List<StockBalance>> LoadBalancesAsync(
        int productId,
        int locationId,
        CancellationToken cancellationToken)
        => await context.StockBalances
            .Include(b => b.Lot)
            .Where(b => b.LocationId == locationId && b.Lot!.ProductId == productId && b.Quantity > 0m)
            .ToListAsync(cancellationToken);

    private async Task<string> NextDocNoAsync(DocumentType docType, int year, CancellationToken cancellationToken)
    {
        var sequence = await context.DocumentSequences
            .FirstOrDefaultAsync(s => s.DocType == docType && s.Year == year, cancellationToken)
            ?? await context.DocumentSequences
                .FirstOrDefaultAsync(s => s.DocType == docType && s.Year == 0, cancellationToken);

        if (sequence is null)
            throw new DomainException($"No DocumentSequences row for {docType} in {year}.");

        return sequence.Take(year);
    }

    /// <summary>Stages a movement and moves the balance; the caller commits them together.</summary>
    public void ApplyMovement(
        int lotId,
        int productId,
        int locationId,
        InventoryMovementKind kind,
        decimal magnitude,
        MovementSourceType sourceType,
        int? sourceId,
        int userId,
        DateOnly businessDate,
        DateTime nowUtc)
    {
        var quantity = InventoryMovement.SignedQuantity(kind, magnitude);

        var balance = context.StockBalances
            .Include(b => b.Lot)
            .FirstOrDefault(b => b.LotId == lotId && b.LocationId == locationId);

        if (balance is null)
        {
            throw new InsufficientStockException(
                $"No stock balance for lot {lotId} at location {locationId}.");
        }

        balance.Apply(quantity, nowUtc);

        context.InventoryMovements.Add(new InventoryMovement
        {
            OccurredAt = nowUtc,
            BusinessDate = businessDate,
            ProductId = productId,
            LotId = lotId,
            LocationId = locationId,
            Kind = kind,
            Quantity = quantity,
            SourceType = sourceType,
            SourceId = sourceId,
            CreatedBy = userId,
        });
    }
}