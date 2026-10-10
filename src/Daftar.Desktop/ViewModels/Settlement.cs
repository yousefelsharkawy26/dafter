using System;
using System.Collections.Generic;
using System.Linq;
using Daftar.Domain.Finance;

namespace Daftar.ViewModels;

/// <summary>
/// How much of a document is settled when it is posted. The document itself is unaffected — a
/// purchase still makes us owe the supplier and a sale still makes the customer owe us — this only
/// decides whether a payment voucher is written alongside it.
/// </summary>
public enum SettlementChoice
{
    /// <summary>Paid in full at the counter.</summary>
    Full,

    /// <summary>Part paid now, the rest left outstanding.</summary>
    Partial,

    /// <summary>Nothing paid now; it becomes a receivable or a payable.</summary>
    Deferred,
}

/// <summary>Arabic labels and account rules for a way of paying.</summary>
public static class PaymentMethods
{
    public static readonly IReadOnlyList<PaymentMethod> Supported =
    [
        PaymentMethod.Cash,
        PaymentMethod.Wallet,
    ];

    public static string Label(this PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => "كاش (الخزينة النقدية)",
        PaymentMethod.Wallet => "محفظة إلكترونية (فودافون كاش / انستاباي)",
        PaymentMethod.Card => "فيزا / ماستركارد",
        PaymentMethod.Transfer => "تحويل بنكي",
        PaymentMethod.Cheque => "شيك",
        _ => method.ToString(),
    };

    /// <summary>
    /// Which accounts a method can be received into. A wallet transfer is not a note in the till,
    /// so offering the cash account for it would misreport where the money went.
    /// </summary>
    public static IReadOnlyList<CashAccountType> AccountsFor(this PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => [CashAccountType.Cash],
        PaymentMethod.Wallet => [CashAccountType.Wallet],
        PaymentMethod.Card or PaymentMethod.Transfer or PaymentMethod.Cheque => [CashAccountType.Bank],
        _ => [CashAccountType.Cash, CashAccountType.Wallet],
    };
}

/// <summary>Shared behaviour for choosing how to settle a document at the moment it is posted.</summary>
public sealed class Settlement
{
    public SettlementChoice Choice { get; set; } = SettlementChoice.Deferred;

    public PaymentMethod Method { get; set; } = PaymentMethod.Cash;

    public int? CashAccountId { get; set; }

    /// <summary>Only meaningful for <see cref="SettlementChoice.Partial"/>.</summary>
    public decimal PartialAmount { get; set; }

    /// <summary>
    /// The amount to write a voucher for, given the document total. Never more than the total:
    /// handing over more is change, not a credit against the next invoice.
    /// </summary>
    public decimal AmountFor(decimal total) => Choice switch
    {
        SettlementChoice.Full => total,
        SettlementChoice.Partial => Math.Min(PartialAmount, total),
        _ => 0m,
    };

    public bool ShouldWriteVoucher(decimal total) => AmountFor(total) > 0m;
}