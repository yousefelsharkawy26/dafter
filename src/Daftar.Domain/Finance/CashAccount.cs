using Daftar.Domain.Shared;

namespace Daftar.Domain.Finance;

public enum CashAccountType
{
    Cash,
    Bank,
    Wallet,

    /// <summary>
    /// A distributor's cash custody. Collections land here and stay until the session is settled,
    /// so the manager can see whose money he is holding.
    /// </summary>
    DistributorCustody,
}

/// <summary>Where money physically sits: the till, the bank, a wallet, or a distributor's custody.</summary>
public sealed class CashAccount : IAuditable, IDomainValidatable
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public CashAccountType Type { get; set; } = CashAccountType.Cash;
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public List<CashTransaction> Transactions { get; set; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new DomainException("Cash account name is required.");
    }
}