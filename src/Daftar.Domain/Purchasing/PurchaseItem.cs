using Daftar.Domain.Shared;

namespace Daftar.Domain.Purchasing;

/// <summary>
/// A purchase line. UnitName and UnitFactor are snapshots: renaming a unit or changing its factor
/// later must not rewrite history, so lines keep what was true when they were entered.
///
/// Quantity is in the ENTERED unit; BaseQuantity is what actually moves stock.
/// </summary>
public sealed class PurchaseItem : IDomainValidatable
{
    private decimal _lineTotal;

    public int Id { get; set; }
    public int PurchaseId { get; set; }
    public Purchase? Purchase { get; set; }

    public int LineNo { get; set; }

    public int ProductId { get; set; }
    public Catalog.Product? Product { get; set; }

    public int ProductUnitId { get; set; }
    public Catalog.ProductUnit? ProductUnit { get; set; }

    /// <summary>Snapshot of the unit name.</summary>
    public string UnitName { get; set; } = string.Empty;

    /// <summary>Snapshot of the unit factor: base units in one of the entered unit.</summary>
    public decimal UnitFactor { get; set; } = 1m;

    /// <summary>In the entered unit.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Quantity x UnitFactor. Stock always moves in these units.</summary>
    public decimal BaseQuantity { get; set; }

    /// <summary>Per entered unit.</summary>
    public decimal UnitPrice { get; set; }

    public decimal DiscountAmount { get; set; }
    public decimal LineTotal
    {
        get => _lineTotal;
        private set => _lineTotal = value;
    }

    /// <summary>On an Invoice, the batch created at posting. On a Return, the batch being returned.</summary>
    public int? LotId { get; set; }
    public Inventory.Lot? Lot { get; set; }

    /// <summary>Copies the unit name and factor, then recomputes base quantity and line total.</summary>
    public void SnapshotFrom(Catalog.ProductUnit unit, decimal quantity, decimal unitPrice)
    {
        ArgumentNullException.ThrowIfNull(unit);
        UnitName = unit.Name;
        UnitFactor = unit.Factor;
        Quantity = quantity;
        UnitPrice = unitPrice;
        Recalculate();
    }

    public void Recalculate()
    {
        BaseQuantity = Rounding.Quantity(Quantity * UnitFactor);
        _lineTotal = Rounding.LineTotal(Quantity, UnitPrice, DiscountAmount);
    }

    /// <summary>
    /// Lot cost = line total after the line discount, divided by base quantity. Invoice-level
    /// discount is deliberately NOT spread over lots (documented simplification).
    /// </summary>
    public decimal ComputeLotUnitCost()
        => BaseQuantity <= 0m ? 0m : Rounding.Unit(LineTotal / BaseQuantity);

    public void Validate()
    {
        if (Quantity <= 0m)
            throw new DomainException($"Line {LineNo} has Quantity {Quantity}; must be > 0.");
        if (BaseQuantity <= 0m)
            throw new DomainException($"Line {LineNo} has BaseQuantity {BaseQuantity}; must be > 0.");
        if (UnitPrice < 0m)
            throw new DomainException($"Line {LineNo} has a negative UnitPrice.");
        if (DiscountAmount < 0m)
            throw new DomainException($"Line {LineNo} has a negative DiscountAmount.");
        if (DiscountAmount > Rounding.Amount(Quantity * UnitPrice))
            throw new DomainException($"Line {LineNo} discount exceeds its gross amount.");
        if (UnitFactor <= 0m)
            throw new DomainException($"Line {LineNo} has UnitFactor {UnitFactor}; it has no unit snapshot.");
        if (_lineTotal != Rounding.LineTotal(Quantity, UnitPrice, DiscountAmount))
            throw new DomainException($"Line {LineNo} LineTotal is stale.");
    }
}