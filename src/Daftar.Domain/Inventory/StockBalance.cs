using Daftar.Domain.Shared;

namespace Daftar.Domain.Inventory;

/// <summary>
/// Quantity of one batch at one location. A derived cache: it exists so allocation does not fold
/// every movement, and it must always equal the sum of that lot+location's movements. It changes
/// only in the same transaction as the movement that causes it.
///
/// There is no Id — the key is (LotId, LocationId).
/// </summary>
public sealed class StockBalance : IDomainValidatable
{
    private decimal _quantity;

    public int LotId { get; set; }
    public Lot? Lot { get; set; }

    public int LocationId { get; set; }
    public StockLocation? Location { get; set; }

    /// <summary>Base units. Never negative — enforced in <see cref="Apply"/>.</summary>
    public decimal Quantity
    {
        get => _quantity;
        private set => _quantity = value;
    }

    public DateTime UpdatedAt { get; set; }

    public static StockBalance Open(int lotId, int locationId, DateTime now)
        => new() { LotId = lotId, LocationId = locationId, Quantity = 0m, UpdatedAt = now };

    /// <summary>
    /// Applies a signed movement quantity and rejects going negative. This is the single place the
    /// "no negative stock" rule is enforced for balances.
    /// </summary>
    public void Apply(decimal signedQuantity, DateTime now)
    {
        var next = Rounding.Quantity(_quantity + signedQuantity);
        if (next < 0m)
            throw new InsufficientStockException(
                $"Lot {LotId} at location {LocationId} has {Quantity} in stock; {Math.Abs(signedQuantity)} cannot be taken out.");
        Quantity = next;
        UpdatedAt = now;
    }

    public void Validate()
    {
        if (Quantity < 0m)
            throw new DomainException($"StockBalances (Lot {LotId}, Location {LocationId}) is negative: {Quantity}.");
    }
}