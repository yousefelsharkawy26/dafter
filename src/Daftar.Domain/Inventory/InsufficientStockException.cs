namespace Daftar.Domain.Inventory;

/// <summary>Raised when a movement would take stock below zero.</summary>
public sealed class InsufficientStockException : Shared.DomainException
{
    public InsufficientStockException(string message) : base(message)
    {
    }
}