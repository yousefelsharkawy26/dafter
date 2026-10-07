using Daftar.Domain.Shared;

namespace Daftar.Domain.Purchasing;

public enum PurchaseType
{
    Invoice,
    Return,
}

/// <summary>
/// Supplier invoice or purchase return. Draft until posted; posting allocates the DocNo, creates
/// the lots and writes the movements, all in one transaction (Infrastructure).
/// </summary>
public sealed class Purchase : IAuditable, IDomainValidatable
{
    public int Id { get; set; }
    public PurchaseType Type { get; set; } = PurchaseType.Invoice;

    public int SupplierId { get; set; }
    public Parties.Party? Supplier { get; set; }

    /// <summary>Receiving warehouse. For a Return, where the goods leave from.</summary>
    public int LocationId { get; set; }
    public Inventory.StockLocation? Location { get; set; }

    public string? SupplierInvoiceNo { get; set; }
    public DateOnly? DueDate { get; set; }

    /// <summary>Set on a Return: the invoice being returned against.</summary>
    public int? OriginalPurchaseId { get; set; }
    public Purchase? OriginalPurchase { get; set; }

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

    public List<PurchaseItem> Items { get; set; } = [];

    public bool IsPosted => Status == DocumentStatus.Posted;
    public bool IsDraft => Status == DocumentStatus.Draft;

    /// <summary>Adds a line, renumbering LineNo from the end.</summary>
    public PurchaseItem AddItem(
        Catalog.Product product,
        Catalog.ProductUnit unit,
        decimal quantity,
        decimal unitPrice,
        decimal discountAmount = 0m)
    {
        if (!IsDraft)
            throw new DomainException($"Purchase {DocNo ?? Id.ToString()} is {Status}; no lines can be added.");

        var item = new PurchaseItem
        {
            PurchaseId = Id,
            Purchase = this,
            LineNo = Items.Count + 1,
            ProductId = product.Id,
            Product = product,
            ProductUnitId = unit.Id,
            ProductUnit = unit,
            DiscountAmount = discountAmount,
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
            throw new DomainException($"Purchase {Id} is {Status}; only a Draft can be posted.");
        if (string.IsNullOrWhiteSpace(docNo))
            throw new DomainException("A posted purchase needs a document number.");
        if (Items.Count == 0)
            throw new DomainException("Cannot post a purchase with no lines.");
        if (SupplierId <= 0)
            throw new DomainException("A purchase needs a supplier.");

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
                "A posted purchase cannot be cancelled; post a return or reverse the movements.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Cancelling requires a reason.");

        Status = DocumentStatus.Cancelled;
        CancelledAt = nowUtc;
        CancelledBy = userId;
        CancelReason = reason;
    }

    public void Validate()
    {
        if (SupplierId <= 0)
            throw new DomainException("A purchase needs a supplier.");
        if (DocNo is not null && string.IsNullOrWhiteSpace(DocNo))
            throw new DomainException("DocNo cannot be blank when set.");
        if (Status == DocumentStatus.Posted && DocNo is null)
            throw new DomainException("A posted purchase must have a DocNo.");
        if (Status == DocumentStatus.Cancelled && string.IsNullOrWhiteSpace(CancelReason))
            throw new DomainException("A cancelled purchase needs a CancelReason.");
        if (DiscountAmount < 0m)
            throw new DomainException("DiscountAmount cannot be negative.");
        if (Status == DocumentStatus.Draft && Items.Count > 0 && Subtotal != Rounding.Amount(Items.Sum(i => i.LineTotal)))
            throw new DomainException($"Purchase {Id} subtotal {Subtotal} does not match its lines.");
    }
}