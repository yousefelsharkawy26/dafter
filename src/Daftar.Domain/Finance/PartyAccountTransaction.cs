using Daftar.Domain.Shared;

namespace Daftar.Domain.Finance;

public enum PartyLedgerKind
{
    Opening,
    Sale,
    SaleReturn,
    Purchase,
    PurchaseReturn,

    /// <summary>Customer paid us: reduces the balance.</summary>
    PaymentIn,

    /// <summary>We paid a supplier: reduces what we owe.</summary>
    PaymentOut,
    Adjustment,
    Reversal,
}

/// <summary>
/// Append-only receivables/payables ledger, one row per event affecting a party's net balance.
/// Amount is SIGNED: positive means the party owes us more, negative means we owe them.
///
/// A party that is both customer and supplier has ONE net balance, because it is one table row.
/// </summary>
public sealed class PartyAccountTransaction : IAppendOnly, IDomainValidatable
{
    public int Id { get; set; }
    public int PartyId { get; set; }
    public Parties.Party? Party { get; set; }

    public DateTime OccurredAt { get; set; }
    public DateOnly BusinessDate { get; set; }
    public PartyLedgerKind Kind { get; set; }

    /// <summary>
    /// SIGNED from OUR side of the ledger: positive means the party owes us, negative means we
    /// owe them.
    /// <para>
    /// So a sale is positive (the customer owes us) and a purchase is negative (we owe the
    /// supplier). Every payment is negative whichever way it goes: money received from a
    /// customer and money paid to a supplier both reduce what that party owes.
    /// </para>
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>Set on invoice rows, for aging.</summary>
    public DateOnly? DueDate { get; set; }

    public string SourceType { get; set; } = string.Empty;
    public int? SourceId { get; set; }
    public string? Note { get; set; }
    public int? ReversesId { get; set; }
    public PartyAccountTransaction? Reverses { get; set; }
    public int CreatedBy { get; set; }

    public void Validate()
    {
        if (PartyId <= 0)
            throw new DomainException("A party ledger row needs a party.");
        if (Amount == 0m)
            throw new DomainException($"Party ledger row {Id} has Amount 0.");
        if (!SignAgreesWithKind(Kind, Amount))
            throw new DomainException($"Ledger Kind {Kind} is inconsistent with the sign of Amount {Amount}.");
    }

    /// <summary>
    /// A sale leaves the customer owing us (positive); a purchase leaves us owing the supplier
    /// (negative). This is the guardrail that keeps a payable from being recorded as a
    /// receivable, which is what happens when the two are given the same sign.
    /// </summary>
    private static bool SignAgreesWithKind(PartyLedgerKind kind, decimal amount) => kind switch
    {
        PartyLedgerKind.Sale => amount > 0m,
        PartyLedgerKind.SaleReturn => amount < 0m,

        PartyLedgerKind.Purchase => amount < 0m,
        PartyLedgerKind.PurchaseReturn => amount > 0m,

        // Money in from a customer reduces what they owe us, so it is negative. Money out to a
        // supplier reduces what we owe them, so it is positive: the two directions sit on
        // opposite halves of the same balance.
        PartyLedgerKind.PaymentIn => amount < 0m,
        PartyLedgerKind.PaymentOut => amount > 0m,

        // Opening, Adjustment and Reversal may go either way.
        _ => true,
    };
}