using Daftar.Domain.Shared;

namespace Daftar.Domain.Finance;

public enum PaymentDirection
{
    /// <summary>Money coming in from a customer.</summary>
    In,

    /// <summary>Money going out to a supplier.</summary>
    Out,
}

public enum PaymentMethod
{
    Cash,
    Transfer,
    Wallet,
    Card,

    /// <summary>Treated as cleared immediately until a Cheques table exists.</summary>
    Cheque,
}

/// <summary>
/// A receipt or payment voucher. Writing one also moves the party's balance and the cash account,
/// in the same transaction as the sale when the till takes the money at the point of sale.
/// </summary>
public sealed class Payment : IAuditable, IDomainValidatable
{
    public int Id { get; set; }
    public PaymentDirection Direction { get; set; }

    public int PartyId { get; set; }
    public Parties.Party? Party { get; set; }

    /// <summary>A distributor's collection goes to his custody account until settlement.</summary>
    public int CashAccountId { get; set; }
    public CashAccount? CashAccount { get; set; }

    public PaymentMethod Method { get; set; } = PaymentMethod.Cash;

    /// <summary>Always positive; <see cref="Direction"/> carries the sense.</summary>
    public decimal Amount { get; set; }

    /// <summary>Transfer reference, cheque number or receipt number.</summary>
    public string? Reference { get; set; }

    /// <summary>Optional: the invoice this settles.</summary>
    public int? SaleId { get; set; }
    public Sales.Sale? Sale { get; set; }

    public int? PurchaseId { get; set; }
    public Purchasing.Purchase? Purchase { get; set; }

    /// <summary>The work period this collection belongs to. Added with the P3 migration.</summary>
    public int? DistributorSessionId { get; set; }
    public Distributors.DistributorSession? DistributorSession { get; set; }

    public DateOnly? DueDate { get; set; }
    public string? Notes { get; set; }

    public string? DocNo { get; set; }
    public DateOnly DocDate { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;
    public DateTime? PostedAt { get; set; }
    public int? PostedBy { get; set; }
    public DateTime? CancelledAt { get; set; }
    public int? CancelledBy { get; set; }
    public string? CancelReason { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public bool IsPosted => Status == DocumentStatus.Posted;
    public bool IsDraft => Status == DocumentStatus.Draft;

    /// <summary>
    /// Signed effect on the party balance, from our side. The two directions differ, because
    /// they sit on opposite halves of the same balance: a receipt from a customer reduces what
    /// they owe us, and paying a supplier reduces what we owe them.
    /// <para>
    /// So money in is negative and money out is positive. Treating both as negative would only
    /// be right if sales and purchases both pushed the balance the same way, which they do not.
    /// </para>
    /// </summary>
    public decimal PartyBalanceDelta => Direction == PaymentDirection.In ? -Amount : Amount;

    /// <summary>Signed effect on the cash account: a receipt adds money.</summary>
    public decimal CashDelta => Direction == PaymentDirection.In ? Amount : -Amount;

    public void Post(string docNo, int userId, DateTime nowUtc)
    {
        if (!IsDraft)
            throw new DomainException($"Payment {Id} is already {Status}.");
        if (string.IsNullOrWhiteSpace(docNo))
            throw new DomainException("A posted payment needs a document number.");

        DocNo = docNo;
        Status = DocumentStatus.Posted;
        PostedAt = nowUtc;
        PostedBy = userId;
        CancelReason = null;
    }

    public void Cancel(string reason, int userId, DateTime nowUtc)
    {
        if (IsPosted)
            throw new DomainException("A posted payment cannot be cancelled; post a reversing voucher.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Cancelling requires a reason.");

        Status = DocumentStatus.Cancelled;
        CancelledAt = nowUtc;
        CancelledBy = userId;
        CancelReason = reason;
    }

    public void Validate()
    {
        if (Amount <= 0m)
            throw new DomainException($"Payment amount must be positive, got {Amount}.");
        if (PartyId <= 0)
            throw new DomainException("A payment needs a party.");
        if (CashAccountId <= 0)
            throw new DomainException("A payment needs a cash account.");
        if (Status == DocumentStatus.Posted && DocNo is null)
            throw new DomainException("A posted payment must have a DocNo.");
        if (Status == DocumentStatus.Cancelled && string.IsNullOrWhiteSpace(CancelReason))
            throw new DomainException("A cancelled payment needs a CancelReason.");
        if (SaleId is not null && PurchaseId is not null)
            throw new DomainException("A payment settles either a sale or a purchase, not both.");
    }
}