using Daftar.Domain.Shared;

namespace Daftar.Domain.Parties;

public enum PartyStatus
{
    Active,
    /// <summary>Cash only until the manager approves. How distributor-created customers start.</summary>
    Pending,
    Blocked,
    Archived,
}

public enum CustomerType
{
    Supermarket,
    Restaurant,
    Wholesale,
    Retail,
    Individual,
    Other,
}

/// <summary>
/// Customers AND suppliers in one table — a warehouse is frequently both, and splitting them
/// would duplicate the whole model. Role is expressed with the two flags.
/// </summary>
public sealed class Party : IAuditable, IDomainValidatable
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public bool IsCustomer { get; set; }
    public bool IsSupplier { get; set; }

    /// <summary>Required when <see cref="IsCustomer"/>.</summary>
    public CustomerType? CustomerType { get; set; }

    public string? Phone { get; set; }
    public string? Address { get; set; }

    /// <summary>Zone/route, used to plan distributor visits.</summary>
    public string? Area { get; set; }

    public string? ContactPerson { get; set; }
    public string? TaxNumber { get; set; }

    /// <summary>0 = cash only.</summary>
    public decimal CreditLimit { get; set; }

    public int PaymentTermsDays { get; set; }

    /// <summary>Priced off this list; null means the default list. Added with the P2 migration.</summary>
    public int? PriceListId { get; set; }

    public Sales.PriceList? PriceList { get; set; }

    /// <summary>The distributor who looks after this customer. Added with the P3 migration.</summary>
    public int? AssignedDistributorId { get; set; }

    public Distributors.Distributor? AssignedDistributor { get; set; }

    public PartyStatus Status { get; set; } = PartyStatus.Active;
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public bool IsCashOnly => CreditLimit <= 0m;

    /// <summary>
    /// Credit rule: active status and headroom for the amount. A Party in Pending, Blocked or
    /// Archived can never take credit, and CreditLimit 0 means cash regardless.
    /// </summary>
    public bool CanTakeCredit(decimal amount)
        => Status == PartyStatus.Active && amount <= CreditLimit;

    public void Validate()
    {
        if (!IsCustomer && !IsSupplier)
            throw new DomainException($"Party '{Code}' must be a customer, a supplier, or both.");
        if (IsCustomer && CustomerType is null)
            throw new DomainException($"Customer '{Code}' needs a CustomerType.");
        if (string.IsNullOrWhiteSpace(Name))
            throw new DomainException($"Party '{Code}' needs a name.");
        if (CreditLimit < 0m)
            throw new DomainException($"Party '{Code}' has a negative CreditLimit.");
        if (PaymentTermsDays < 0)
            throw new DomainException($"Party '{Code}' has negative PaymentTermsDays.");
    }
}