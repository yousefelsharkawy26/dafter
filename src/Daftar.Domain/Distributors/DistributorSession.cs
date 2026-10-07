using Daftar.Domain.Shared;

namespace Daftar.Domain.Distributors;

public enum DistributorSessionStatus
{
    Open,
    Closed,
}

/// <summary>
/// One distributor's work period. Closing it IS the settlement: the vehicle is counted, the cash
/// is counted, and the differences plus his commission are all recorded in one transaction.
///
/// A distributor has at most one open session — the database enforces it with a partial unique
/// index.
/// </summary>
public sealed class DistributorSession : IAuditable, IDomainValidatable
{
    public int Id { get; set; }
    public int DistributorId { get; set; }
    public Distributor? Distributor { get; set; }

    public DistributorSessionStatus Status { get; set; } = DistributorSessionStatus.Open;

    public DateTime OpenedAt { get; set; }
    public int OpenedBy { get; set; }
    public DateTime? ClosedAt { get; set; }
    public int? ClosedBy { get; set; }

    // The snapshot, written at close.
    public decimal? SalesTotal { get; set; }
    public decimal? ReturnsTotal { get; set; }
    public decimal? CollectionsTotal { get; set; }

    /// <summary>The custody account balance at close.</summary>
    public decimal? ExpectedCash { get; set; }

    /// <summary>What he actually had in the till.</summary>
    public decimal? CountedCash { get; set; }

    /// <summary>CountedCash - ExpectedCash. Negative means he is short.</summary>
    public decimal? CashDifference { get; set; }

    /// <summary>Shortage valued at lot cost, from the closing count.</summary>
    public decimal? StockShortageValue { get; set; }

    public decimal? CommissionAmount { get; set; }

    /// <summary>The closing count of his vehicle.</summary>
    public int? StockCountId { get; set; }
    public Inventory.StockCount? StockCount { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public bool IsOpen => Status == DistributorSessionStatus.Open;

    /// <summary>He is short if the counted cash came up under what the ledger expected.</summary>
    public bool IsCashShort => CashDifference is < 0;

    public void Validate()
    {
        if (DistributorId <= 0)
            throw new DomainException("A session needs a distributor.");
        if (Status == DistributorSessionStatus.Closed)
        {
            if (ClosedAt is null || ClosedBy is null)
                throw new DomainException("A closed session needs ClosedAt and ClosedBy.");
            if (CashDifference is null)
                throw new DomainException("A closed session needs the cash difference recorded.");
        }
        else if (ClosedAt is not null)
        {
            throw new DomainException("An open session cannot have ClosedAt.");
        }
    }
}