using Daftar.Domain.Shared;

namespace Daftar.Domain.Sales;

public enum SaleType
{
    Invoice,

    /// <summary>Goods come back in and are restocked into the same lots they left from.</summary>
    Return,
}

/// <summary>
/// A customer invoice or sale return. Posting allocates the DocNo, draws stock from specific lots
/// oldest-first, writes the inventory movements, and records the party's receivable — one transaction.
/// </summary>
public sealed class Sale : IAuditable, IDomainValidatable
{
    public int Id { get; set; }
    public SaleType Type { get; set; } = SaleType.Invoice;

    /// <summary>The customer. Anonymous cash sales use the walk-in party.</summary>
    public int PartyId { get; set; }
    public Parties.Party? Party { get; set; }

    /// <summary>Stock leaves from, and returns to, this location.</summary>
    public int LocationId { get; set; }
    public Inventory.StockLocation? Location { get; set; }

    /// <summary>The list used, snapshotted.</summary>
    public int PriceListId { get; set; }
    public PriceList? PriceList { get; set; }

    /// <summary>Who sold it. Added with the P3 migration.</summary>
    public int? DistributorId { get; set; }
    public Distributors.Distributor? Distributor { get; set; }

    /// <summary>The work period it belongs to. Added with the P3 migration.</summary>
    public int? DistributorSessionId { get; set; }
    public Distributors.DistributorSession? DistributorSession { get; set; }

    /// <summary>Set on a Return: the invoice being returned against.</summary>
    public int? OriginalSaleId { get; set; }
    public Sale? OriginalSale { get; set; }

    public DateOnly? DueDate { get; set; }

    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal Total { get; set; }

    public string? DocNo { get; set; }
    public DateOnly DocDate { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;
    public DateTime? PostedAt { get; set; }
    public int? PostedBy { get; set; }
    public DateTime? CancelledAt { get; set; }
    public int? CancelledBy { get; set; }
    public string? CancelReason { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public List<SaleItem> Items { get; set; } = [];

    public bool IsPosted => Status == DocumentStatus.Posted;
    public bool IsDraft => Status == DocumentStatus.Draft;
    public bool IsReturn => Type == SaleType.Return;

    /// <summary>Gross margin, known only once costing has run at posting.</summary>
    public decimal TotalCost => Rounding.Amount(Items.Sum(i => i.CostTotal));

    public decimal Margin => Rounding.Amount(Total - TotalCost);

    public SaleItem AddItem(
        Catalog.Product product,
        Catalog.ProductUnit unit,
        decimal quantity,
        decimal listPrice,
        decimal unitPrice,
        decimal discountAmount = 0m,
        int? originalSaleItemId = null)
    {
        if (!IsDraft)
            throw new DomainException($"Sale {DocNo ?? Id.ToString()} is {Status}; no lines can be added.");
        if (listPrice <= 0m)
            throw new DomainException($"A line for '{product.Sku}' needs a positive list price.");

        var item = new SaleItem
        {
            SaleId = Id,
            Sale = this,
            LineNo = Items.Count + 1,
            ProductId = product.Id,
            Product = product,
            ProductUnitId = unit.Id,
            ProductUnit = unit,
            // ListPrice is kept so a later override is reportable.
            ListPrice = listPrice,
            UnitPrice = unitPrice,
            DiscountAmount = discountAmount,
            OriginalSaleItemId = originalSaleItemId,
        };

        item.SnapshotFrom(unit, quantity, unitPrice);
        Items.Add(item);
        Recalculate();
        return item;
    }

    /// <summary>Subtotal = sum of line totals; Total = Subtotal - DiscountAmount.</summary>
    public void Recalculate()
    {
        foreach (var item in Items)
            item.Recalculate();

        Subtotal = Rounding.Amount(Items.Sum(i => i.LineTotal));
        Total = Rounding.Amount(Subtotal - DiscountAmount);
    }

    public void Post(string docNo, int userId, DateTime nowUtc)
    {
        if (!IsDraft)
            throw new DomainException($"Sale {Id} is {Status}; only a Draft can be posted.");
        if (string.IsNullOrWhiteSpace(docNo))
            throw new DomainException("A posted sale needs a document number.");
        if (Items.Count == 0)
            throw new DomainException("Cannot post a sale with no lines.");
        if (PartyId <= 0)
            throw new DomainException("A sale needs a customer.");
        if (IsReturn && OriginalSaleId is null)
            throw new DomainException("A sale return must reference the original sale.");

        Recalculate();
        DocNo = docNo;
        Status = DocumentStatus.Posted;
        PostedAt = nowUtc;
        PostedBy = userId;
        CancelReason = null;
    }

    public void Cancel(string reason, int userId, DateTime nowUtc)
    {
        if (IsPosted)
            throw new DomainException(
                "A posted sale cannot be cancelled; post a return or reverse the movements.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Cancelling requires a reason.");

        Status = DocumentStatus.Cancelled;
        CancelledAt = nowUtc;
        CancelledBy = userId;
        CancelReason = reason;
    }

    /// <summary>
    /// True when this line needs Price.Override: the actual price differs from the list price, or the
    /// discount is deeper than the role allows.
    /// </summary>
    public bool RequiresOverridePermission(Security.Role role)
        => Items.Any(i => i.IsOverride || i.DiscountPercent > role.MaxDiscountPercent);

    public void Validate()
    {
        if (PartyId <= 0)
            throw new DomainException("A sale needs a customer.");
        if (IsReturn && OriginalSaleId is null)
            throw new DomainException($"Sale return {Id} has no OriginalSaleId.");
        if (Status == DocumentStatus.Posted && DocNo is null)
            throw new DomainException("A posted sale must have a DocNo.");
        if (Status == DocumentStatus.Cancelled && string.IsNullOrWhiteSpace(CancelReason))
            throw new DomainException("A cancelled sale needs a CancelReason.");
        if (DiscountAmount < 0m)
            throw new DomainException("DiscountAmount cannot be negative.");
        if (Status == DocumentStatus.Draft && Items.Count > 0
            && Subtotal != Rounding.Amount(Items.Sum(i => i.LineTotal)))
            throw new DomainException($"Sale {Id} subtotal {Subtotal} does not match its lines.");
    }
}