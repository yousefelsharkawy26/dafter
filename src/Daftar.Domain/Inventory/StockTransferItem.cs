using Daftar.Domain.Shared;

namespace Daftar.Domain.Inventory;

/// <summary>One lot on a transfer, moved in base units.</summary>
public sealed class StockTransferItem : IDomainValidatable
{
    public int Id { get; set; }
    public int TransferId { get; set; }
    public StockTransfer? Transfer { get; set; }

    public int LotId { get; set; }
    public Lot? Lot { get; set; }

    /// <summary>Base units, always positive. Direction comes from the transfer's locations.</summary>
    public decimal BaseQuantity { get; set; }

    public void Validate()
    {
        if (BaseQuantity <= 0m)
            throw new DomainException($"Transfer line {Id} has BaseQuantity {BaseQuantity}; must be > 0.");
    }
}