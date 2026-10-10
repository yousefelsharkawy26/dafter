using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Daftar.Infrastructure.Services;

/// <summary>
/// Posting services nest: settling a distributor session runs a stock count and a transfer, and each
/// of those is itself a posting operation that wants a transaction. EF refuses a second
/// BeginTransaction on one connection, so an inner service joins the transaction the outer one
/// opened, and only the outermost commits or rolls back.
/// </summary>
/// <remarks>
/// <para>
/// Ownership is tracked explicitly rather than inferred from
/// <c>context.Database.CurrentTransaction</c>. Inferring it is unsound: when a service borrows an
/// ambient transaction, EF still reports that same object as the current one, so any
/// reference-equality test answers "I own this" for both the owner and the borrower. The inner
/// service would then commit the outer transaction early, <c>await using</c> would dispose it, and
/// the outer service's rollback would silently become a no-op — leaving a half-settled distributor
/// session with posted stock counts and an open session behind it.
/// </para>
/// <para>
/// So: <see cref="_owned"/> is non-null only for the scope that actually called BeginTransaction.
/// A borrower has <c>_owned == null</c>, which makes its commit and rollback no-ops and stops it
/// disposing a transaction it does not own.
/// </para>
/// <para>
/// The transaction is opened at <see cref="IsolationLevel.Serializable"/>. On SQLite this is
/// EF's strongest setting; the accompanying <c>busy_timeout</c> in the connection string makes
/// a genuine concurrent writer wait its turn rather than failing, and the single-instance guard
/// in the desktop composition root means two copies of the till rarely contend at all.
/// </para>
/// </remarks>
public sealed class TransactionScope : IDisposable, IAsyncDisposable
{
    private readonly IDbContextTransaction? _owned;

    private TransactionScope(IDbContextTransaction? owned) => _owned = owned;

    /// <summary>True when this scope opened the transaction and is responsible for ending it.</summary>
    public bool IsOwner => _owned is not null;

    /// <summary>
    /// Joins the ambient transaction if there is one, otherwise starts a new one and owns it.
    /// </summary>
    public static async Task<TransactionScope> BeginAsync(
        DbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Database.CurrentTransaction is not null
            ? new TransactionScope(owned: null)
            : new TransactionScope(await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken));
    }

    /// <summary>Commits if this scope owns the transaction; a no-op for a nested scope.</summary>
    public Task CommitAsync(CancellationToken cancellationToken = default)
        => _owned is null
            ? Task.CompletedTask
            : _owned.CommitAsync(cancellationToken);

    /// <summary>Rolls back if this scope owns the transaction; a no-op for a nested scope.</summary>
    public Task RollbackAsync(CancellationToken cancellationToken = default)
        => _owned is null
            ? Task.CompletedTask
            : _owned.RollbackAsync(cancellationToken);

    /// <summary>Releases the transaction only if this scope owns it.</summary>
    public void Dispose() => _owned?.Dispose();

    /// <inheritdoc cref="Dispose"/>
    public async ValueTask DisposeAsync()
    {
        if (_owned is not null)
            await _owned.DisposeAsync();
    }
}