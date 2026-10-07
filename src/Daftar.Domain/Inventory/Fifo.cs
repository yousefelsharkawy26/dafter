using Daftar.Domain.Shared;

namespace Daftar.Domain.Inventory;

/// <summary>Why allocation found nothing, so the UI can say something more useful than "no stock".</summary>
public enum StockAvailability
{
    /// <summary>Enough sellable stock.</summary>
    Available,
    /// <summary>No balance rows at all for this product and location.</summary>
    NoStock,
    /// <summary>Stock exists but every batch is blocked.</summary>
    OnlyBlocked,
}

/// <summary>
/// First-In-First-Out allocation: oldest received batch first, then LotId as a tie-break so two
/// batches received on the same day always draw in the same order.
///
/// There is no expiry, so the old FEFO rule is gone — stock of the same product is fungible and
/// only the cost of each delivery is tracked.
///
/// Pure function over balances; it mutates nothing and hits no database.
/// </summary>
public static class Fifo
{
    public static IReadOnlyList<StockBalance> Pick(
        IEnumerable<StockBalance> balances,
        decimal requiredQuantity)
    {
        ArgumentNullException.ThrowIfNull(balances);

        if (requiredQuantity <= 0m)
            throw new DomainException($"Allocation requires a positive quantity, got {requiredQuantity}.");

        var ordered = Order(balances).ToList();

        if (ordered.Count == 0)
            throw new InsufficientStockException(
                Classify(balances) == StockAvailability.OnlyBlocked
                    ? "Only blocked stock is available."
                    : "No stock available.");

        var picked = new List<StockBalance>();
        var remaining = requiredQuantity;

        foreach (var balance in ordered)
        {
            if (remaining <= 0m)
                break;

            picked.Add(balance);
            remaining = Rounding.Quantity(remaining - balance.Quantity);
        }

        if (remaining > 0m)
            throw new InsufficientStockException(
                $"Insufficient stock: short by {remaining} base unit(s).");

        return picked;
    }

    /// <summary>Sellable balances in draw order: oldest delivery first.</summary>
    public static IReadOnlyList<StockBalance> Order(IEnumerable<StockBalance> balances)
        => balances
            .Where(b => b.Lot is not null && b.Quantity > 0m && b.Lot.IsAvailable())
            .OrderBy(b => b.Lot!.ReceivedOn)
            .ThenBy(b => b.LotId)
            .ToList();

    public static StockAvailability Classify(IEnumerable<StockBalance> balances)
    {
        ArgumentNullException.ThrowIfNull(balances);

        var any = false;
        foreach (var balance in balances)
        {
            if (balance.Lot is null || balance.Quantity <= 0m)
                continue;

            any = true;
            if (balance.Lot.IsAvailable())
                return StockAvailability.Available;
        }

        return any ? StockAvailability.OnlyBlocked : StockAvailability.NoStock;
    }

    /// <summary>How much to draw from each balance to cover the request, in draw order.</summary>
    public static IReadOnlyList<(StockBalance Balance, decimal Quantity)> Allocate(
        IEnumerable<StockBalance> balances,
        decimal requiredQuantity)
    {
        var result = new List<(StockBalance Balance, decimal Quantity)>();
        var remaining = requiredQuantity;

        foreach (var balance in Pick(balances, requiredQuantity))
        {
            var take = balance.Quantity <= remaining ? balance.Quantity : remaining;
            result.Add((balance, take));
            remaining = Rounding.Quantity(remaining - take);
        }

        return result;
    }
}