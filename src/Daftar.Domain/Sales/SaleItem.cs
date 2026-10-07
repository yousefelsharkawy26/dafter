using Daftar.Domain.Shared;

namespace Daftar.Domain.Sales;

/// <summary>
/// A sale line. UnitName/UnitFactor are snapshots, and both ListPrice and UnitPrice are stored so a
/// price override stays reportable after the fact.
///
/// CostTotal is filled at posting from the lots the line actually drew from.
/// </summary>
public sealed class SaleItem : IDomainValidatable
{
    private decimal _lineTotal;

    public int Id { get; set; }
    public int SaleId { get; set; }
    public Sale? Sale { get; set; }

    public int LineNo { get; set; }

    public int ProductId { get; set; }
    public Catalog.Product? Product { get; set; }

    public int ProductUnitId { get; set; }
    public Catalog.ProductUnit? ProductUnit { get; set; }

    public string UnitName { get; set; } = string.Empty;
    public decimal UnitFactor { get; set; } = 1m;

    /// <summary>In the entered unit.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Quantity x UnitFactor. The figure stock moves by.</summary>
    public decimal BaseQuantity { get; set; }

    /// <summary>Price-list price at the moment of the sale.</summary>
    public decimal ListPrice { get; set; }

    /// <summary>Actual price. Differs from ListPrice only with Price.Override.</summary>
    public decimal UnitPrice { get; set; }

    public decimal DiscountAmount { get; set; }
    public decimal LineTotal
    {
        get => _lineTotal;
        private set => _lineTotal = value;
    }

    /// <summary>Sum of SaleItemLots unit cost x quantity. Set at posting.</summary>
    public decimal CostTotal { get; set; }

    /// <summary>On a return line, the sale line being returned.</summary>
    public int? OriginalSaleItemId { get; set; }
    public SaleItem? OriginalSaleItem { get; set; }

    public List<SaleItemLot> Lots { get; set; } = [];

    /// <summary>The sold price differs from the list price.</summary>
    public bool IsOverride => UnitPrice != ListPrice;

    /// <summary>Line discount as a percentage of gross, for the role's MaxDiscountPercent check.</summary>
    public decimal DiscountPercent
    {
        get
        {
            var gross = Rounding.Amount(Quantity * UnitPrice);
            return gross <= 0m ? 0m : Rounding.Amount(DiscountAmount / gross * 100m);
        }
    }

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

    /// <summary>Records which lot supplied how much, and what that stock cost.</summary>
    public SaleItemLot AddLot(int lotId, decimal baseQuantity, decimal unitCost)
    {
        if (baseQuantity <= 0m)
            throw new DomainException($"Line {LineNo} cannot draw {baseQuantity} from a lot.");

        var existing = Lots.FirstOrDefault(l => l.LotId == lotId);
        if (existing is not null)
            throw new DomainException($"Line {LineNo} already draws from lot {lotId}.");

        var line = new SaleItemLot
        {
            SaleItemId = Id,
            LotId = lotId,
            BaseQuantity = baseQuantity,
            UnitCost = unitCost,
        };

        Lots.Add(line);
        CostTotal = Rounding.Amount(Lots.Sum(l => l.BaseQuantity * l.UnitCost));
        return line;
    }

    /// <summary>
    /// Allocates the line across batches, oldest delivery first. Throws if the total available is
    /// short, so a
    /// line can never post against stock that is not there.
    /// </summary>
    public void AllocateFrom(IEnumerable<Inventory.StockBalance> balances)
    {
        if (Lots.Count > 0)
            throw new DomainException($"Line {LineNo} has already been allocated.");

        var allocations = Inventory.Fifo.Allocate(balances, BaseQuantity);
        foreach (var (balance, quantity) in allocations)
            AddLot(balance.LotId, quantity, balance.Lot?.UnitCost ?? 0m);
    }

    public void Validate()
    {
        if (Quantity <= 0m)
            throw new DomainException($"Line {LineNo} has Quantity {Quantity}; must be > 0.");
        if (BaseQuantity <= 0m)
            throw new DomainException($"Line {LineNo} has BaseQuantity {BaseQuantity}; must be > 0.");
        if (UnitPrice < 0m)
            throw new DomainException($"Line {LineNo} has a negative UnitPrice.");
        if (ListPrice < 0m)
            throw new DomainException($"Line {LineNo} has a negative ListPrice.");
        if (DiscountAmount < 0m)
            throw new DomainException($"Line {LineNo} has a negative DiscountAmount.");
        if (DiscountAmount > Rounding.Amount(Quantity * UnitPrice))
            throw new DomainException($"Line {LineNo} discount exceeds its gross amount.");
        if (UnitFactor <= 0m)
            throw new DomainException($"Line {LineNo} has no unit snapshot.");
        if (_lineTotal != Rounding.LineTotal(Quantity, UnitPrice, DiscountAmount))
            throw new DomainException($"Line {LineNo} LineTotal is stale.");

        // Every base unit sold must be accounted for by a lot.
        var allocated = Rounding.Quantity(Lots.Sum(l => l.BaseQuantity));
        if (Sale is { IsPosted: true } && allocated != BaseQuantity)
            throw new DomainException(
                $"Line {LineNo} posts {BaseQuantity} base units but its lots cover {allocated}.");
    }
}