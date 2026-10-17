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

    /// <summary>Set once this scope's commit has succeeded; a committed transaction cannot be undone.</summary>
    private bool _committed;

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
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_owned is null)
            return;

        await _owned.CommitAsync(cancellationToken);
        _committed = true;
    }

    /// <summary>
    /// Rolls back if this scope owns the transaction and it is still open; a no-op for a nested
    /// scope, and a no-op once the transaction has been committed.
    /// </summary>
    /// <remarks>
    /// Callers keep working after <see cref="CommitAsync"/> — the refresh that follows a posting
    /// reads back what was just written — and they do it inside the <c>try</c> whose <c>catch</c>
    /// rolls back. When that refresh is the thing that fails, the rollback is asked to undo a
    /// transaction that has already committed, and EF answers with <c>InvalidOperationException</c>
    /// from inside the catch block, replacing the reason the user was about to be shown. There is
    /// nothing left to undo after a commit, so declining here costs nothing and keeps the original
    /// failure intact.
    /// </remarks>
    public Task RollbackAsync(CancellationToken cancellationToken = default)
        => _owned is null || _committed
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