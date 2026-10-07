namespace Daftar.Domain.Shared;

/// <summary>
/// Audit columns (+A) stamped by the SaveChanges interceptor on master data and documents.
/// <c>*By</c> holds a UserId with no FK: users are never deleted.
/// </summary>
public interface IAuditable
{
    DateTime CreatedAt { get; set; }
    int CreatedBy { get; set; }
    DateTime? UpdatedAt { get; set; }
    int? UpdatedBy { get; set; }
}

/// <summary>
/// Append-only tables carry OccurredAt + CreatedBy only — no UpdatedAt, because they are never updated.
/// </summary>
public interface IAppendOnly
{
    DateTime OccurredAt { get; set; }
    int CreatedBy { get; set; }
}