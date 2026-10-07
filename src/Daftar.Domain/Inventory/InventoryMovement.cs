using Daftar.Domain.Shared;

namespace Daftar.Domain.Inventory;

/// <summary>Why stock changed. Sign must agree with the kind — see <see cref="InventoryMovement"/>.</summary>
public enum InventoryMovementKind
{
    Opening,
    Receipt,
    PurchaseReturn,
    Sale,
    SaleReturn,
    TransferIn,
    TransferOut,
    CountAdjust,
    Adjust,
    Scrap,
    Reversal,
}

/// <summary>What triggered the movement, so the ledger can be traced back to a document.</summary>
public enum MovementSourceType
{
    Purchase,
    Sale,
    Transfer,
    Count,
    Manual,
    Opening,
}

public static class ReasonCode
{
    public const string Damaged = "Damaged";
    public const string Expired = "Expired";
    public const string Theft = "Theft";
    public const string CountError = "CountError";
    public const string Sample = "Sample";
    public const string Other = "Other";

    public static readonly string[] All = [Damaged, Expired, Theft, CountError, Sample, Other];
}

/// <summary>
/// One append-only row per balance change. A transfer writes two rows (TransferOut negative,
/// TransferIn positive) sharing SourceType/SourceId. ProductId is denormalized from the lot so
/// stock cards never need to join through the lot.
///
/// Quantity is SIGNED: positive is in, negative is out.
/// </summary>
public sealed class InventoryMovement : IAppendOnly, IDomainValidatable
{
    public int Id { get; set; }

    public DateTime OccurredAt { get; set; }
    public DateOnly BusinessDate { get; set; }

    public int ProductId { get; set; }
    public int LotId { get; set; }
    public int LocationId { get; set; }

    public InventoryMovementKind Kind { get; set; }

    /// <summary>SIGNED, base units, never 0.</summary>
    public decimal Quantity { get; set; }

    public MovementSourceType SourceType { get; set; }
    public int? SourceId { get; set; }

    /// <summary>Mandatory for Adjust and Scrap.</summary>
    public string? ReasonCode { get; set; }

    public string? Note { get; set; }
    public int? ReversesMovementId { get; set; }
    public int CreatedBy { get; set; }

    public Lot? Lot { get; set; }
    public StockLocation? Location { get; set; }
    public InventoryMovement? ReversesMovement { get; set; }

    /// <summary>The transfer this row belongs to, when stock was moved.</summary>
    public int? TransferId { get; set; }
    public StockTransfer? Transfer { get; set; }

    /// <summary>The count that produced this adjustment.</summary>
    public int? StockCountId { get; set; }
    public StockCount? StockCount { get; set; }

    /// <summary>The distributor session this row was recorded in, if any.</summary>
    public int? DistributorSessionId { get; set; }
    public Distributors.DistributorSession? DistributorSession { get; set; }

    public bool IsInbound => Quantity > 0m;

    /// <summary>Kinds that add stock. Reversal takes the sign of the row it reverses, so it is either.</summary>
    public static bool IsInboundKind(InventoryMovementKind kind) => kind
        is InventoryMovementKind.Opening
        or InventoryMovementKind.Receipt
        or InventoryMovementKind.PurchaseReturn
        or InventoryMovementKind.SaleReturn
        or InventoryMovementKind.TransferIn;

    /// <summary>Signs the movement quantity from its kind. Reversal and CountAdjust keep their own sign.</summary>
    public static decimal SignedQuantity(InventoryMovementKind kind, decimal magnitude)
    {
        var amount = Math.Abs(magnitude);
        if (amount == 0m)
            throw new DomainException("A movement quantity cannot be zero.");
        return IsInboundKind(kind) ? amount : -amount;
    }

    public void Validate()
    {
        if (Quantity == 0m)
            throw new DomainException($"Movement {Id} has Quantity 0; it would not change any balance.");
        if (!SignAgreesWithKind(Kind, Quantity))
            throw new DomainException(
                $"Movement Kind {Kind} is inconsistent with the sign of Quantity {Quantity}.");
        if ((Kind == InventoryMovementKind.Adjust || Kind == InventoryMovementKind.Scrap)
            && string.IsNullOrWhiteSpace(ReasonCode))
            throw new DomainException($"Movement Kind {Kind} requires a ReasonCode.");
    }

    private static bool SignAgreesWithKind(InventoryMovementKind kind, decimal quantity)
        => kind switch
        {
            InventoryMovementKind.Opening or InventoryMovementKind.Receipt
                or InventoryMovementKind.PurchaseReturn or InventoryMovementKind.SaleReturn
                or InventoryMovementKind.TransferIn => quantity > 0m,
            InventoryMovementKind.Sale or InventoryMovementKind.TransferOut
                or InventoryMovementKind.Scrap => quantity < 0m,
            // CountAdjust, Adjust and Reversal may go either way.
            _ => true,
        };
}