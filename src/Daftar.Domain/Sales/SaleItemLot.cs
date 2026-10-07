using Daftar.Domain.Shared;

namespace Daftar.Domain.Sales;

/// <summary>
/// Which lot supplied which sale line. A 30 kg line can draw from two lots.
///
/// BaseQuantity is always positive — direction comes from Sales.Type — and UnitCost is snapshotted
/// so margin stays accurate even after lot costs move.
/// </summary>
public sealed class SaleItemLot : IDomainValidatable
{
    public int Id { get; set; }
    public int SaleItemId { get; set; }
    public SaleItem? SaleItem { get; set; }

    public int LotId { get; set; }
    public Inventory.Lot? Lot { get; set; }

    public decimal BaseQuantity { get; set; }
    public decimal UnitCost { get; set; }

    public decimal LineCost => Rounding.Amount(BaseQuantity * UnitCost);

    public void Validate()
    {
        if (BaseQuantity <= 0m)
            throw new DomainException($"SaleItemLot {Id} has BaseQuantity {BaseQuantity}; must be > 0.");
        if (UnitCost < 0m)
            throw new DomainException($"SaleItemLot {Id} has a negative UnitCost.");
    }
}