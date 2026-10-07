using Daftar.Domain.Shared;

namespace Daftar.Domain.Inventory;

/// <summary>
/// Moves stock between locations: warehouse to warehouse, or warehouse to a distributor's vehicle.
/// Posting writes two movement rows (TransferOut negative, TransferIn positive) sharing one
/// SourceId, so both sides always reconcile.
/// </summary>
public sealed class StockTransfer : IAuditable, IDomainValidatable
{
    public int Id { get; set; }

    public int FromLocationId { get; set; }
    public StockLocation? FromLocation { get; set; }

    public int ToLocationId { get; set; }
    public StockLocation? ToLocation { get; set; }

    /// <summary>Set when this transfer loads or unloads a distributor's vehicle.</summary>
    public int? DistributorSessionId { get; set; }
    public Distributors.DistributorSession? DistributorSession { get; set; }

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

    public List<StockTransferItem> Items { get; set; } = [];

    public bool IsPosted => Status == DocumentStatus.Posted;
    public bool IsDraft => Status == DocumentStatus.Draft;

    public StockTransferItem AddItem(Lot lot, decimal baseQuantity)
    {
        if (!IsDraft)
            throw new DomainException($"Transfer {DocNo ?? Id.ToString()} is {Status}; no lines can be added.");
        if (baseQuantity <= 0m)
            throw new DomainException("A transfer line must move a positive quantity.");

        var existing = Items.FirstOrDefault(i => i.LotId == lot.Id);
        if (existing is not null)
            throw new DomainException($"Lot {lot.Id} is already on this transfer.");

        var item = new StockTransferItem
        {
            TransferId = Id,
            Transfer = this,
            LotId = lot.Id,
            Lot = lot,
            BaseQuantity = Rounding.Quantity(baseQuantity),
        };

        Items.Add(item);
        return item;
    }

    public void Post(string docNo, int userId, DateTime nowUtc)
    {
        if (!IsDraft)
            throw new DomainException($"Transfer {Id} is already {Status}.");
        if (string.IsNullOrWhiteSpace(docNo))
            throw new DomainException("A posted transfer needs a document number.");
        if (Items.Count == 0)
            throw new DomainException("Cannot post a transfer with no lines.");
        if (FromLocationId == ToLocationId)
            throw new DomainException("A transfer needs two different locations.");

        DocNo = docNo;
        Status = DocumentStatus.Posted;
        PostedAt = nowUtc;
        PostedBy = userId;
        CancelReason = null;
    }

    public void Cancel(string reason, int userId, DateTime nowUtc)
    {
        if (IsPosted)
            throw new DomainException("A posted transfer cannot be cancelled; post the reverse transfer.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Cancelling requires a reason.");

        Status = DocumentStatus.Cancelled;
        CancelledAt = nowUtc;
        CancelledBy = userId;
        CancelReason = reason;
    }

    public void Validate()
    {
        if (FromLocationId <= 0 || ToLocationId <= 0)
            throw new DomainException("A transfer needs both a source and a destination.");
        if (FromLocationId == ToLocationId)
            throw new DomainException($"Transfer {Id} has the same location on both sides.");
        if (Status == DocumentStatus.Posted && DocNo is null)
            throw new DomainException("A posted transfer must have a DocNo.");
        if (Status == DocumentStatus.Cancelled && string.IsNullOrWhiteSpace(CancelReason))
            throw new DomainException("A cancelled transfer needs a CancelReason.");
    }
}