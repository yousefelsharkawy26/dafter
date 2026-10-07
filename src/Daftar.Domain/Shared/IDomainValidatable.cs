namespace Daftar.Domain.Shared;

/// <summary>
/// Implemented by entities the database cannot police (SQLite stores decimals as TEXT, so
/// there is no numeric CHECK). The DbContext calls <see cref="Validate"/> on every tracked
/// instance before saving, so a bad value fails loudly instead of corrupting a ledger.
/// </summary>
public interface IDomainValidatable
{
    void Validate();
}