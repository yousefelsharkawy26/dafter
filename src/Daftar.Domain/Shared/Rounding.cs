using System.Globalization;
namespace Daftar.Domain.Shared;

/// <summary>
/// Every rounding in the system goes through here: AwayFromZero, per the schema convention.
/// SQLite stores decimals as TEXT, so these scales are documentation and C# enforcement,
/// not database constraints.
/// </summary>
public static class Rounding
{
    public const int AmountScale = 2;
    public const int UnitScale = 4;
    public const int QuantityScale = 3;

    /// <summary>dec(18,2) — document amounts and totals.</summary>
    public static decimal Amount(decimal value) => Round(value, AmountScale);

    /// <summary>dec(18,4) — unit prices and unit costs.</summary>
    public static decimal Unit(decimal value) => Round(value, UnitScale);

    /// <summary>dec(18,3) — quantities in base units.</summary>
    public static decimal Quantity(decimal value) => Round(value, QuantityScale);

    /// <summary>
    /// A quantity written for a screen: no forced decimals, none dropped either. A bag of 12
    /// reads "12", and 12.5kg reads "12.5", rather than one of them trailing ".0".
    /// </summary>
    public static string QuantityText(decimal value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>LineTotal = round(Quantity x UnitPrice - DiscountAmount, 2).</summary>
    public static decimal LineTotal(decimal quantity, decimal unitPrice, decimal discountAmount)
        => Amount(quantity * unitPrice - discountAmount);

    private static decimal Round(decimal value, int scale)
        => Math.Round(value, scale, MidpointRounding.AwayFromZero);
}