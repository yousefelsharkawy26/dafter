using System.ComponentModel.DataAnnotations.Schema;
using Daftar.Domain.Shared;

namespace Daftar.Domain.Inventory;

/// <summary>
/// A physical stock count. While it is a Draft the location is effectively locked: SystemQty is
/// frozen at the moment counting starts, so later sales cannot silently change what the counter
/// saw. Posting turns each variance into a CountAdjust movement.
/// </summary>
public sealed class StockCount : IAuditable, IDomainValidatable
{
    public int Id { get; set; }
    public int LocationId { get; set; }
    public StockLocation? Location { get; set; }

    /// <summary>Set when this counts a distributor's vehicle at settlement.</summary>
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

    public List<StockCountItem> Items { get; set; } = [];

    public bool IsPosted => Status == DocumentStatus.Posted;
    public bool IsDraft => Status == DocumentStatus.Draft;

    public StockCountItem AddItem(Lot lot, decimal systemQuantity, decimal? countedQuantity = null)
    {
        if (!IsDraft)
            throw new DomainException($"Count {DocNo ?? Id.ToString()} is {Status}; no lines can be added.");
        if (Items.Any(i => i.LotId == lot.Id))
            throw new DomainException($"Lot {lot.Id} is already on this count.");

        var item = new StockCountItem
        {
            StockCountId = Id,
            StockCount = this,
            LotId = lot.Id,
            Lot = lot,
            // Frozen at the moment counting starts.
            SystemQty = Rounding.Quantity(systemQuantity),
            CountedQty = countedQuantity,
        };

        item.RecalculateDifference();
        Items.Add(item);
        return item;
    }

    /// <summary>
    /// Variances only, in lot order. These are what posting will adjust. Computed, so it is not a
    /// navigation property — EF maps <see cref="Items"/> and ignores this.
    /// </summary>
    [NotMapped]
    public IEnumerable<StockCountItem> Variances =>
        Items.Where(i => i.CountedQty.HasValue && i.DiffQty != 0m);

    public decimal TotalVarianceValue(decimal lotCostOf) => Rounding.Amount(Variances.Sum(i => i.AbsDiff * lotCostOf));

    public void Post(string docNo, int userId, DateTime nowUtc)
    {
        if (!IsDraft)
            throw new DomainException($"Count {Id} is already {Status}.");
        if (string.IsNullOrWhiteSpace(docNo))
            throw new DomainException("A posted count needs a document number.");
        if (Items.Count == 0)
            throw new DomainException("Cannot post a count with no lines.");
        if (Items.Any(i => i.CountedQty is null))
            throw new DomainException("Every line must have a counted quantity before posting.");

        foreach (var item in Items)
            item.RecalculateDifference();

        DocNo = docNo;
        Status = DocumentStatus.Posted;
        PostedAt = nowUtc;
        PostedBy = userId;
        CancelReason = null;
    }

    public void Cancel(string reason, int userId, DateTime nowUtc)
    {
        if (IsPosted)
            throw new DomainException("A posted count cannot be cancelled; post a correcting count.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Cancelling requires a reason.");

        Status = DocumentStatus.Cancelled;
        CancelledAt = nowUtc;
        CancelledBy = userId;
        CancelReason = reason;
    }

    public void Validate()
    {
        if (LocationId <= 0)
            throw new DomainException("A count needs a location.");
        if (Status == DocumentStatus.Posted)
        {
            if (DocNo is null)
                throw new DomainException("A posted count must have a DocNo.");
            if (Items.Any(i => i.CountedQty is null))
                throw new DomainException("A posted count cannot have an uncounted line.");
        }
        if (Status == DocumentStatus.Cancelled && string.IsNullOrWhiteSpace(CancelReason))
            throw new DomainException("A cancelled count needs a CancelReason.");
    }
}