using Daftar.Domain.Shared;

namespace Daftar.Domain.Sales;

/// <summary>
/// The price of one product-unit in one price list from a given date.
///
/// Never updated: a price change is a new row. The current price is the latest EffectiveFrom that
/// is on or before the document date. A unit absent from a list falls back to the default list.
/// </summary>
public sealed class ProductPrice : IAuditable, IDomainValidatable
{
    public int Id { get; set; }
    public int PriceListId { get; set; }
    public PriceList? PriceList { get; set; }

    /// <summary>Price per unit — a case can be cheaper than 24 pieces.</summary>
    public int ProductUnitId { get; set; }
    public Catalog.ProductUnit? ProductUnit { get; set; }

    public decimal Price { get; set; }
    public DateOnly EffectiveFrom { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public void Validate()
    {
        if (Price < 0m)
            throw new DomainException($"Price cannot be negative, got {Price}.");
    }

    /// <summary>Chooses the price in force on <paramref name="on"/> from candidates for the same unit.</summary>
    public static ProductPrice? InForceOn(IEnumerable<ProductPrice> candidates, DateOnly on)
        => candidates
            .Where(p => p.EffectiveFrom <= on)
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefault();
}