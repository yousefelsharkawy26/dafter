namespace Daftar.Domain.Shared;

/// <summary>Lifecycle shared by all documents (Purchases, Sales, Payments, Transfers, Counts).</summary>
public enum DocumentStatus
{
    Draft,
    Posted,
    Cancelled,
}