using Daftar.Domain.Shared;

namespace Daftar.Domain.Catalog;

/// <summary>
/// A product. Identity is the Id; Sku is unique but that is enforced by a database index, not here —
/// plain C# cannot see other rows.
///
/// The base unit is frozen by <see cref="Freeze"/> once any stock movement exists, because existing
/// balances are counted in base units and restating the factor would silently restate history.
/// </summary>
public sealed class Product : IAuditable, IDomainValidatable
{
    private string _sku = string.Empty;
    private bool _frozen;

    public int Id { get; set; }

    /// <summary>Trimmed and upper-cased on assignment.</summary>
    public string Sku
    {
        get => _sku;
        set => _sku = NormalizeSku(value);
    }

    public string Name { get; set; } = string.Empty;

    /// <summary>Low-stock alert threshold, in base units.</summary>
    public decimal? ReorderLevel { get; set; }

    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public List<ProductUnit> Units { get; set; } = [];

    public Product()
    {
    }

    public Product(string sku, string name)
    {
        Sku = sku;
        Name = name;
    }

    public bool IsFrozen => _frozen;

    public void Freeze() => _frozen = true;

    public void Unfreeze() => _frozen = false;

    public ProductUnit? BaseUnit => Units.FirstOrDefault(u => u.IsBase);

    /// <summary>Promotes <paramref name="unit"/> to be the base unit, demoting the previous one.</summary>
    public void SetBaseUnit(ProductUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!Units.Contains(unit))
            throw new DomainException("The unit to promote must already belong to this product.");

        foreach (var other in Units)
            other.IsBase = ReferenceEquals(other, unit);

        unit.Factor = 1m;
    }

    public ProductUnit DefaultSaleUnit() => Units.FirstOrDefault(u => u.IsDefaultSale && u.IsActive)
        ?? BaseUnit
        ?? throw new DomainException($"Product '{Sku}' has no default sale unit.");

    public ProductUnit DefaultPurchaseUnit() => Units.FirstOrDefault(u => u.IsDefaultPurchase && u.IsActive)
        ?? BaseUnit
        ?? throw new DomainException($"Product '{Sku}' has no default purchase unit.");

    public static string NormalizeSku(string value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Sku))
            throw new DomainException("Sku is required.");
        if (string.IsNullOrWhiteSpace(Name))
            throw new DomainException($"Product '{Sku}' needs a name.");
        if (ReorderLevel is < 0)
            throw new DomainException($"Product '{Sku}' has a negative ReorderLevel.");
    }
}