using Daftar.Domain.Shared;

namespace Daftar.Domain.Inventory;

/// <summary>
/// One counted lot. SystemQty is frozen when counting starts; DiffQty is filled in as the counter
/// works and finalised at posting.
/// </summary>
public sealed class StockCountItem : IDomainValidatable
{
    private decimal? _diffQty;

    public int Id { get; set; }
    public int StockCountId { get; set; }
    public StockCount? StockCount { get; set; }

    public int LotId { get; set; }
    public Lot? Lot { get; set; }

    /// <summary>Frozen when counting starts, so later movements cannot change what the counter saw.</summary>
    public decimal SystemQty { get; set; }

    public decimal? CountedQty { get; set; }

    /// <summary>CountedQty - SystemQty. Null until counted.</summary>
    public decimal? DiffQty
    {
        get => _diffQty;
        private set => _diffQty = value;
    }

    public bool IsShort => DiffQty is < 0;
    public decimal AbsDiff => DiffQty is { } d ? Math.Abs(d) : 0m;

    public void RecalculateDifference()
        => DiffQty = CountedQty is { } counted ? Rounding.Quantity(counted - SystemQty) : null;

    public void Validate()
    {
        if (CountedQty is < 0m)
            throw new DomainException($"Count line {Id} has a negative CountedQty.");
        if (SystemQty < 0m)
            throw new DomainException($"Count line {Id} has a negative SystemQty.");
        if (CountedQty.HasValue && DiffQty is null)
            throw new DomainException($"Count line {Id} was counted but has no DiffQty.");
    }
}