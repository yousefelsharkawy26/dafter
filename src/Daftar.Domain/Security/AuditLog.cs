using Daftar.Domain.Shared;

namespace Daftar.Domain.Security;

public static class AuditAction
{
    public const string Insert = "Insert";
    public const string Update = "Update";
    public const string Delete = "Delete";
    public const string Post = "Post";
    public const string Cancel = "Cancel";
    public const string Login = "Login";
    public const string LoginFailed = "LoginFailed";
}

/// <summary>
/// Append-only trail for master data and document status changes. NOT used for the ledgers
/// (InventoryMovements, PartyAccountTransactions, CashTransactions) — those are their own log.
///
/// Does not implement IAppendOnly: its columns are At / UserId (nullable, no FK) rather than
/// OccurredAt / CreatedBy, because a failed login has no user.
/// </summary>
public sealed class AuditLog
{
    public int Id { get; set; }
    public DateTime At { get; set; }

    /// <summary>null = system.</summary>
    public int? UserId { get; set; }

    public string Action { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;

    /// <summary>JSON of changed columns only.</summary>
    public string? OldValues { get; set; }

    public string? NewValues { get; set; }
}