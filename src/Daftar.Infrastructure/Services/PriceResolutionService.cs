using Daftar.Domain.Parties;
using Daftar.Domain.Sales;
using Daftar.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace Daftar.Infrastructure.Services;

/// <summary>Which list a price came from, and whether the default list had to step in.</summary>
public sealed record ResolvedPrice(decimal Price, int PriceListId, bool FellBackToDefault);

/// <summary>
/// Resolves the price of a product-unit on a given date.
///
/// Rules: a party's own list wins; a unit missing from that list falls back to the default list;
/// within a list the current price is the latest EffectiveFrom on or before the document date.
///
/// Every comparison happens in C#, never in SQL. SQLite stores decimals as TEXT, so
/// <c>WHERE EffectiveFrom &lt;= @on</c> on a decimal column would compare strings.
/// </summary>
public sealed class PriceResolutionService(DaftarContext context)
{
    /// <summary>The default list, or null when none is marked.</summary>
    public Task<PriceList?> DefaultListAsync(CancellationToken ct = default)
        => context.PriceLists.FirstOrDefaultAsync(p => p.IsDefault && p.IsActive, ct);

    /// <summary>The list a party is priced off: their own if set, otherwise the default.</summary>
    public async Task<PriceList> ListForAsync(Party party, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(party);

        if (party.PriceListId is { } id)
        {
            var own = await context.PriceLists
                .FirstOrDefaultAsync(p => p.Id == id && p.IsActive, ct);
            if (own is not null)
                return own;
        }

        return await DefaultListAsync(ct)
            ?? throw new DomainException("No price list is marked as the default.");
    }

    public async Task<ResolvedPrice> ResolveAsync(
        int? priceListId,
        int productUnitId,
        DateOnly on,
        CancellationToken ct = default)
    {
        var @default = await DefaultListAsync(ct);
        var primary = priceListId is { } id
            ? await context.PriceLists.FirstOrDefaultAsync(p => p.Id == id && p.IsActive, ct)
            : @default;

        if (primary is not null)
        {
            var fromPrimary = await InForceAsync(primary.Id, productUnitId, on, ct);
            if (fromPrimary is { } price)
                return new ResolvedPrice(price, primary.Id, FellBackToDefault: false);
        }

        // The unit was absent from that list (or there was no list) — try the default one.
        if (@default is not null && @default.Id != primary?.Id)
        {
            var fromDefault = await InForceAsync(@default.Id, productUnitId, on, ct);
            if (fromDefault is { } price)
                return new ResolvedPrice(price, @default.Id, FellBackToDefault: true);
        }

        throw new DomainException(
            $"No price for unit {productUnitId} in force on {on:yyyy-MM-dd}" +
            (primary is null ? " and no default price list exists." : "."));
    }

    /// <summary>The price in force for one unit within one list, or null.</summary>
    private async Task<decimal?> InForceAsync(
        int priceListId,
        int productUnitId,
        DateOnly on,
        CancellationToken ct)
    {
        // Materialized, then compared in memory: a decimal column is TEXT in SQLite.
        var candidates = await context.ProductPrices
            .Where(p => p.PriceListId == priceListId && p.ProductUnitId == productUnitId)
            .ToListAsync(ct);

        return ProductPrice.InForceOn(candidates, on)?.Price;
    }

    /// <summary>
    /// Prices a whole sale up front, so the cashier sees the full amount before committing.
    /// Returns one entry per requested unit, in the order asked for.
    /// </summary>
    public async Task<IReadOnlyList<(int ProductUnitId, decimal Price, bool FellBack)>> ResolveManyAsync(
        int? priceListId,
        IReadOnlyCollection<int> productUnitIds,
        DateOnly on,
        CancellationToken ct = default)
    {
        var result = new List<(int, decimal, bool)>(productUnitIds.Count);
        foreach (var unitId in productUnitIds.Distinct())
        {
            var resolved = await ResolveAsync(priceListId, unitId, on, ct);
            result.Add((unitId, resolved.Price, resolved.FellBackToDefault));
        }

        return result;
    }
}