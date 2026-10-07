using Daftar.Domain.Shared;

namespace Daftar.Domain.Finance;

public enum CashTransactionKind
{
    Opening,
    Collection,
    SupplierPayment,
    Expense,
    TransferIn,
    TransferOut,
    Deposit,
    Withdrawal,
    Adjustment,
    Reversal,
}

public enum ExpenseCategory
{
    Fuel,
    Rent,
    Salary,
    Commission,
    Utilities,
    Maintenance,
    Other,
}

/// <summary>
/// Append-only cash ledger, one row per movement of money in or out of an account.
/// Amount is SIGNED: positive is in, negative is out.
///
/// A distributor's collections land in his DistributorCustody account and stay there until the
/// session is settled, so the manager can always see whose money he is holding.
/// </summary>
public sealed class CashTransaction : IAppendOnly, IDomainValidatable
{
    public int Id { get; set; }
    public int CashAccountId { get; set; }
    public CashAccount? CashAccount { get; set; }

    public DateTime OccurredAt { get; set; }
    public DateOnly BusinessDate { get; set; }
    public CashTransactionKind Kind { get; set; }

    /// <summary>SIGNED: + in, - out.</summary>
    public decimal Amount { get; set; }

    public int? PaymentId { get; set; }
    public Payment? Payment { get; set; }

    /// <summary>The work period this movement belongs to. Added with the P3 migration.</summary>
    public int? DistributorSessionId { get; set; }
    public Distributors.DistributorSession? DistributorSession { get; set; }

    /// <summary>Pairs the two rows of an account-to-account transfer.</summary>
    public string? TransferRef { get; set; }

    public ExpenseCategory? Category { get; set; }
    public string? Note { get; set; }
    public int? ReversesId { get; set; }
    public CashTransaction? Reverses { get; set; }
    public int CreatedBy { get; set; }

    public void Validate()
    {
        if (CashAccountId <= 0)
            throw new DomainException("A cash ledger row needs an account.");
        if (Amount == 0m)
            throw new DomainException($"Cash ledger row {Id} has Amount 0.");
    }
}