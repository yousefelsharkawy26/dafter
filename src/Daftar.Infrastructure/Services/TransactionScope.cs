using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Daftar.Infrastructure.Services;

/// <summary>
/// Posting services nest: settling a distributor session runs a stock count and a transfer, and each
/// of those is itself a posting operation that wants a transaction. EF refuses a second
/// BeginTransaction on one connection, so an inner service reuses the transaction the outer one
/// opened and only the outermost commits or rolls back.
/// </summary>
public static class TransactionScope
{
    public static async Task<IDbContextTransaction> BeginAsync(
        DbContext context,
        CancellationToken cancellationToken)
        => context.Database.CurrentTransaction is { } existing
            ? existing
            : await context.Database.BeginTransactionAsync(cancellationToken);

    public static Task CommitAsync(
        IDbContextTransaction transaction,
        DbContext context,
        CancellationToken cancellationToken)
        => IsOutermost(transaction, context)
            ? transaction.CommitAsync(cancellationToken)
            : Task.CompletedTask;

    public static Task RollbackAsync(
        IDbContextTransaction transaction,
        DbContext context,
        CancellationToken cancellationToken)
        => IsOutermost(transaction, context)
            ? transaction.RollbackAsync(cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// True when this transaction is the one EF is tracking as current, i.e. nobody else will
    /// commit it for us. A reused inner transaction has an owner further up the stack.
    /// </summary>
    private static bool IsOutermost(IDbContextTransaction transaction, DbContext context)
        => ReferenceEquals(context.Database.CurrentTransaction, transaction);
}