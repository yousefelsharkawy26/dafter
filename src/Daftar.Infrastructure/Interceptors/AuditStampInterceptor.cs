using System.Text.Json;
using Daftar.Domain.Security;
using Daftar.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Daftar.Infrastructure;

/// <summary>
/// Stamps the +A columns and writes AuditLog rows.
///
/// Audited: master data and document status. NOT audited: the ledgers (they are their own log),
/// AuditLog itself (that would recurse), item tables (the header carries them) and StockBalances
/// (derived from movements).
/// </summary>
public sealed class AuditStampInterceptor(ICurrentUser currentUser) : SaveChangesInterceptor
{
    private static readonly HashSet<string> AuditedTypes =
    [
        nameof(Daftar.Domain.Catalog.Product),
        nameof(Daftar.Domain.Catalog.ProductUnit),
        nameof(Daftar.Domain.Parties.Party),
        nameof(Daftar.Domain.Security.User),
        nameof(Daftar.Domain.Security.Role),
        nameof(Daftar.Domain.Shared.Setting),
        nameof(Daftar.Domain.Inventory.StockLocation),
        nameof(Daftar.Domain.Purchasing.Purchase),
        nameof(Daftar.Domain.Sales.PriceList),
        nameof(Daftar.Domain.Sales.ProductPrice),
        nameof(Daftar.Domain.Finance.CashAccount),
        nameof(Daftar.Domain.Sales.Sale),
        nameof(Daftar.Domain.Finance.Payment),
        nameof(Daftar.Domain.Distributors.Distributor),
        nameof(Daftar.Domain.Inventory.StockTransfer),
        nameof(Daftar.Domain.Inventory.StockCount),
    ];

    /// <summary>Lot changes are only audited for status (block/unblock), never for cost or expiry.</summary>
    private static readonly HashSet<string> StatusOnlyTypes = [nameof(Daftar.Domain.Inventory.Lot)];

    private readonly List<AuditLog> _pending = [];

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        StampAndAudit(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        StampAndAudit(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void StampAndAudit(DbContext? context)
    {
        if (context is null)
            return;

        var now = DateTime.UtcNow;
        var userId = currentUser.UserId ?? 0;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is IAuditable auditable)
            {
                if (entry.State == EntityState.Added)
                {
                    auditable.CreatedAt = now;
                    auditable.CreatedBy = userId;
                }
                else if (entry.State == EntityState.Modified)
                {
                    auditable.UpdatedAt = now;
                    auditable.UpdatedBy = userId;
                }
            }

            var name = entry.Entity.GetType().Name;
            var action = entry.State switch
            {
                EntityState.Added => AuditAction.Insert,
                EntityState.Modified => AuditAction.Update,
                EntityState.Deleted => AuditAction.Delete,
                _ => null,
            };

            if (action is null || !IsAudited(name, entry))
                continue;

            var newValues = action == AuditAction.Insert || action == AuditAction.Update
                ? SerializeChangedProperties(entry)
                : null;

            _pending.Add(new AuditLog
            {
                At = now,
                UserId = currentUser.UserId,
                Action = action,
                EntityName = name,
                EntityId = PrimaryKey(entry),
                NewValues = newValues,
            });
        }

        if (_pending.Count == 0)
            return;

        // Added in the same change set, so the audit rows commit with the change they describe.
        context.AddRange(_pending);
        _pending.Clear();
    }

    private static bool IsAudited(string entityName, EntityEntry entry)
    {
        if (AuditedTypes.Contains(entityName))
            return true;

        return StatusOnlyTypes.Contains(entityName) && HasStatusChange(entry);
    }

    private static bool HasStatusChange(EntityEntry entry)
        => entry.State == EntityState.Added
        || entry.State == EntityState.Deleted
        || entry.Property(nameof(Daftar.Domain.Inventory.Lot.Status)).IsModified
        || entry.Property(nameof(Daftar.Domain.Inventory.Lot.BlockReason)).IsModified;

    private static string PrimaryKey(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
            return string.Empty;

        var parts = key.Properties
            .Select(p => Convert.ToString(entry.CurrentValues[p]) ?? string.Empty);
        return string.Join("|", parts);
    }

    /// <summary>
    /// Properties that must never be copied into the audit trail.
    ///
    /// AuditLogs is append-only at the database level, so anything written there is permanent and
    /// cannot be purged by changing the password it duplicates. A password hash written here is a
    /// second, permanent copy of credential material in a table that exports and backups expose
    /// far more freely than Users does.
    /// </summary>
    /// <remarks>
    /// This is a deny-list, which fails open for any column added later. That is the safer failure
    /// direction for the audit columns themselves, but the opposite for secrets, so any new
    /// sensitive column must be added here deliberately.
    /// </remarks>
    private static readonly HashSet<string> NeverAudit =
    [
        nameof(User.PasswordHash),
    ];

    private static string? SerializeChangedProperties(EntityEntry entry)
    {
        var changed = entry.Properties
            .Where(p => p.IsModified || entry.State == EntityState.Added)
            .Where(p => !string.Equals(p.Metadata.Name, nameof(IAuditable.UpdatedAt), StringComparison.Ordinal)
                     && !string.Equals(p.Metadata.Name, nameof(IAuditable.UpdatedBy), StringComparison.Ordinal))
            // Record that the credential changed, not what it became.
            .Select(p => NeverAudit.Contains(p.Metadata.Name)
                ? new KeyValuePair<string, string>(p.Metadata.Name, "(redacted)")
                : new KeyValuePair<string, string>(p.Metadata.Name, p.CurrentValue?.ToString() ?? "null"))
            .ToDictionary(p => p.Key, p => p.Value);

        return changed.Count == 0
            ? null
            : JsonSerializer.Serialize(changed);
    }
}