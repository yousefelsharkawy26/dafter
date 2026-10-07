using Daftar.Domain.Shared;

namespace Daftar.Domain.Inventory;

public enum LotStatus
{
    Active,
    /// <summary>Quarantine or recall. Never drawn when selling.</summary>
    Blocked,
}

/// <summary>
/// One receipt batch per purchase line, created automatically at posting. It exists to carry the
/// unit cost of that specific delivery — stock costing needs to know what a unit cost when it was
/// bought, not today's price.
///
/// There is no lot number, production date or expiry: the manager does not track batches, so stock
/// of the same product is fungible. Identity is the Id; <see cref="ReceivedOn"/> then Id is the
/// drawing order (see <see cref="Fifo"/>).
/// </summary>
public sealed class Lot : IAuditable, IDomainValidatable
{
    private decimal _unitCost;
    private bool _frozen;

    public int Id { get; set; }
    public int ProductId { get; set; }
    public Catalog.Product? Product { get; set; }

    public DateOnly ReceivedOn { get; set; }

    /// <summary>null for opening stock.</summary>
    public int? SupplierId { get; set; }

    public Parties.Party? Supplier { get; set; }

    /// <summary>Per BASE unit, immutable once the batch has stock movements.</summary>
    public decimal UnitCost
    {
        get => _unitCost;
        private set
        {
            if (_frozen && _unitCost != value)
                throw new DomainException(
                    $"UnitCost of batch {Id} is immutable once it has stock movements.");
            _unitCost = value;
        }
    }

    public LotStatus Status { get; set; } = LotStatus.Active;
    public string? BlockReason { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public List<StockBalance> Balances { get; set; } = [];

    /// <summary>Fixes the cost so a later edit cannot restate history.</summary>
    public void Freeze() => _frozen = true;

    public void Block(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Blocking a batch requires a reason.");
        Status = LotStatus.Blocked;
        BlockReason = reason;
    }

    public void Unblock()
    {
        Status = LotStatus.Active;
        BlockReason = null;
    }

    /// <summary>
    /// Blocked batches are the only thing excluded. Nothing else makes stock unsellable, since
    /// there is no expiry to lapse.
    /// </summary>
    public bool IsAvailable() => Status == LotStatus.Active;

    public static Lot Create(int productId, DateOnly receivedOn, int? supplierId, decimal unitCost)
    {
        if (unitCost < 0m)
            throw new DomainException("A batch cannot have a negative UnitCost.");

        return new Lot
        {
            ProductId = productId,
            ReceivedOn = receivedOn,
            SupplierId = supplierId,
            _unitCost = Rounding.Unit(unitCost),
        };
    }

    public void Validate()
    {
        if (UnitCost < 0m)
            throw new DomainException($"Batch {Id} has a negative UnitCost.");
        if (Status == LotStatus.Blocked && string.IsNullOrWhiteSpace(BlockReason))
            throw new DomainException($"Blocked batch {Id} needs a BlockReason.");
    }
}