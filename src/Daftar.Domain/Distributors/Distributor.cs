using Daftar.Domain.Shared;

namespace Daftar.Domain.Distributors;

public enum CommissionBasis
{
    Sales,
    Collections,
}

/// <summary>
/// A field seller. Owns a vehicle (a StockLocation of type Vehicle) and a cash custody account;
/// his collections land in that account and stay there until his session is settled, so the
/// manager can always see whose money he is holding.
/// </summary>
public sealed class Distributor : IAuditable, IDomainValidatable
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }

    /// <summary>Optional login for the future mobile app.</summary>
    public int? UserId { get; set; }
    public Security.User? User { get; set; }

    /// <summary>His vehicle. Unique: one distributor per vehicle.</summary>
    public int StockLocationId { get; set; }
    public Inventory.StockLocation? StockLocation { get; set; }

    /// <summary>His cash custody. Unique: one distributor per custody account.</summary>
    public int CashAccountId { get; set; }
    public Finance.CashAccount? CashAccount { get; set; }

    public decimal CommissionRate { get; set; }

    /// <summary>Whether commission is earned on what he sold or on what he collected.</summary>
    public CommissionBasis CommissionBasis { get; set; } = CommissionBasis.Sales;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public List<DistributorSession> Sessions { get; set; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new DomainException("Distributor name is required.");
        if (StockLocationId <= 0)
            throw new DomainException($"Distributor '{Name}' needs a vehicle.");
        if (CashAccountId <= 0)
            throw new DomainException($"Distributor '{Name}' needs a cash custody account.");
        if (CommissionRate is < 0 or > 100)
            throw new DomainException($"Distributor '{Name}' has CommissionRate {CommissionRate}; must be 0..100.");
    }

    /// <summary>Commission on a base amount, rounded to 2 dp.</summary>
    public decimal CommissionOn(decimal basis) => Rounding.Amount(basis * CommissionRate / 100m);
}