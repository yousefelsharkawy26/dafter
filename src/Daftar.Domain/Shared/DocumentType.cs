namespace Daftar.Domain.Shared;

/// <summary>Document types that can own a number in <c>DocumentSequences</c>.</summary>
public enum DocumentType
{
    Purchase,
    PurchaseReturn,
    Sale,
    SaleReturn,
    PaymentIn,
    PaymentOut,
    Transfer,
    Count,
    Party,
}