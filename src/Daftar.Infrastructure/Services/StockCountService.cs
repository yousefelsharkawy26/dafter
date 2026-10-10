using Daftar.Domain.Inventory;
using Daftar.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Daftar.Infrastructure.Services;

public sealed record CountPostingResult(
    int StockCountId,
    string DocNo,
    IReadOnlyList<(int LotId, decimal Difference)> Adjustments);

/// <summary>
/// Stock counting. Freezing a count snapshots SystemQty for every lot at the location so the
/// counter's numbers cannot drift underfoot; posting then writes one CountAdjust movement per
/// variance. A negative variance means stock disappeared and is refused, since count is how
/// theft and damage get recorded rather than stock simply going below zero.
/// </summary>
public sealed class StockCountService(DaftarContext context)
{
    /// <summary>
    /// Snapshots the current balances into a Draft count. Call once when counting starts; the
    /// location's balances are then what the counter compares against.
    /// </summary>
    public async Task<StockCount> BeginAsync(
        int locationId,
        DateOnly businessDate,
        int userId,
        int? distributorSessionId = null,
        CancellationToken cancellationToken = default)
    {
        var count = new StockCount
        {
            LocationId = locationId,
            DocDate = businessDate,
            DistributorSessionId = distributorSessionId,
        };
        context.StockCounts.Add(count);
        await context.SaveChangesAsync(cancellationToken);

        var balances = await context.StockBalances
            .Include(b => b.Lot)
            .Where(b => b.LocationId == locationId)
            .ToListAsync(cancellationToken);

        foreach (var balance in balances)
            count.AddItem(balance.Lot!, balance.Quantity);

        await context.SaveChangesAsync(cancellationToken);
        return count;
    }

    public async Task RecordCountAsync(
        int countId,
        int lotId,
        decimal countedQuantity,
        CancellationToken cancellationToken = default)
    {
        var item = await context.StockCountItems
            .FirstOrDefaultAsync(i => i.StockCountId == countId && i.LotId == lotId, cancellationToken)
            ?? throw new DomainException($"Lot {lotId} is not on count {countId}.");

        item.CountedQty = Rounding.Quantity(countedQuantity);
        item.RecalculateDifference();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<CountPostingResult> PostAsync(
        int countId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        context.RequirePermission(Daftar.Domain.Security.Role.Codes.StockAdjust);

        await using TransactionScope tx = await TransactionScope.BeginAsync(context, cancellationToken);
        try
        {
            var result = await PostCoreAsync(countId, userId, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<CountPostingResult> PostCoreAsync(
        int countId,
        int userId,
        CancellationToken cancellationToken)
    {
        var count = await context.StockCounts
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == countId, cancellationToken)
            ?? throw new DomainException($"Count {countId} does not exist.");

        if (!count.IsDraft)
            throw new DomainException($"Count {countId} is already {count.Status}.");

        var now = DateTime.UtcNow;
        var docNo = await NextDocNoAsync(count.DocDate.Year, cancellationToken);
        var adjustments = new List<(int, decimal)>();

        foreach (var item in count.Variances)
        {
            var lot = await context.Lots.FirstOrDefaultAsync(l => l.Id == item.LotId, cancellationToken)
                ?? throw new DomainException($"Lot {item.LotId} does not exist.");

            // CountAdjust may go either way, so the sign comes from the variance itself.
            var quantity = item.DiffQty!.Value;

            var balance = await context.StockBalances
                .FirstOrDefaultAsync(b => b.LotId == lot.Id && b.LocationId == count.LocationId, cancellationToken);

            if (balance is null)
            {
                balance = StockBalance.Open(lot.Id, count.LocationId, now);
                context.StockBalances.Add(balance);
            }

            balance.Apply(quantity, now);

            context.InventoryMovements.Add(new InventoryMovement
            {
                OccurredAt = now,
                BusinessDate = count.DocDate,
                ProductId = lot.ProductId,
                LotId = lot.Id,
                LocationId = count.LocationId,
                Kind = InventoryMovementKind.CountAdjust,
                Quantity = quantity,
                SourceType = MovementSourceType.Count,
                SourceId = count.Id,
                StockCountId = count.Id,
                ReasonCode = ReasonCode.CountError,
                CreatedBy = userId,
            });

            adjustments.Add((lot.Id, quantity));
        }

        count.Post(docNo, userId, now);
        await context.SaveChangesAsync(cancellationToken);

        return new CountPostingResult(count.Id, docNo, adjustments);
    }

    /// <summary>Shortage value at lot cost, for the settlement snapshot.</summary>
    public async Task<decimal> ShortageValueAsync(int countId, CancellationToken cancellationToken = default)
    {
        var items = await context.StockCountItems
            .Include(i => i.Lot)
            .Where(i => i.StockCountId == countId && i.DiffQty != null && i.DiffQty < 0)
            .ToListAsync(cancellationToken);

        return Rounding.Amount(items.Sum(i => i.AbsDiff * (i.Lot?.UnitCost ?? 0m)));
    }

    private async Task<string> NextDocNoAsync(int year, CancellationToken cancellationToken)
    {
        var sequence = await context.DocumentSequences
            .FirstOrDefaultAsync(s => s.DocType == DocumentType.Count && s.Year == year, cancellationToken)
            ?? await context.DocumentSequences
                .FirstOrDefaultAsync(s => s.DocType == DocumentType.Count && s.Year == 0, cancellationToken);

        if (sequence is null)
            throw new DomainException($"No DocumentSequences row for Count in {year}.");

        return sequence.Take(year);
    }
}