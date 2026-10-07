using Daftar.Domain.Shared;

namespace Daftar.Domain.Catalog;

/// <summary>
/// A unit a product can be counted or sold in — piece, kg, case, carton, tray. The base unit is
/// itself a row with Factor = 1, so documents never special-case it.
///
/// Factor is base units in ONE of this unit: a case of 24 pieces is 24, a 6 kg case is 6.
/// </summary>
public sealed class ProductUnit : IAuditable, IDomainValidatable
{
    private decimal _factor = 1m;
    private bool _isBase;
    private bool _frozen;

    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Base units in one of this unit. Always &gt; 0.</summary>
    public decimal Factor
    {
        get => _factor;
        set
        {
            if (value <= 0m)
                throw new DomainException($"Unit '{Name}' has Factor {value}; must be > 0.");
            _factor = value;
        }
    }

    public bool IsBase
    {
        get => _isBase;
        set
        {
            if (_frozen && _isBase != value)
                throw new DomainException(
                    "The base unit cannot change once the product has stock movements.");
            _isBase = value;
        }
    }

    /// <summary>Per unit: piece and case carry different barcodes.</summary>
    public string? Barcode { get; set; }

    public bool IsDefaultSale { get; set; }
    public bool IsDefaultPurchase { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public void Freeze() => _frozen = true;

    /// <summary>Converts a quantity expressed in this unit into base units.</summary>
    public decimal ToBase(decimal quantity) => Rounding.Quantity(quantity * Factor);

    /// <summary>Converts base units back into this unit.</summary>
    public decimal FromBase(decimal baseQuantity) => Rounding.Quantity(baseQuantity / Factor);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new DomainException("Unit name is required.");
        if (Factor <= 0m)
            throw new DomainException($"Unit '{Name}' has Factor {Factor}; must be > 0.");
    }
}