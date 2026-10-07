using Daftar.Domain.Distributors;
using Daftar.Domain.Finance;
using Daftar.Domain.Inventory;
using Daftar.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Daftar.Infrastructure.Services;

public sealed record SettlementResult(
    int SessionId,
    int? StockCountId,
    string? CountDocNo,
    decimal SalesTotal,
    decimal ReturnsTotal,
    decimal CollectionsTotal,
    decimal ExpectedCash,
    decimal CountedCash,
    decimal CashDifference,
    decimal StockShortageValue,
    decimal CommissionAmount);

/// <summary>
/// The distributor day, end to end.
///
/// Opening a session starts his period. Loading the vehicle is an ordinary StockTransfer linked to
/// the session. Settling counts the vehicle, counts the cash, and computes his commission — all in
/// one transaction, so a session is never half-settled.
///
/// His collections land in his custody account as they happen, so ExpectedCash is simply that
/// account's balance at close: the money he is holding, from the ledger rather than from memory.
/// </summary>
public sealed class DistributorSessionService(
    DaftarContext context,
    StockCountService counts,
    StockTransferPostingService transfers)
{
    public async Task<DistributorSession> OpenAsync(
        int distributorId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        var alreadyOpen = await context.DistributorSessions
            .AnyAsync(s => s.DistributorId == distributorId && s.Status == DistributorSessionStatus.Open, cancellationToken);

        if (alreadyOpen)
            throw new DomainException($"Distributor {distributorId} already has an open session.");

        var session = new DistributorSession
        {
            DistributorId = distributorId,
            Status = DistributorSessionStatus.Open,
            OpenedAt = DateTime.UtcNow,
            OpenedBy = userId,
        };

        context.DistributorSessions.Add(session);
        await context.SaveChangesAsync(cancellationToken);
        return session;
    }

    /// <summary>Moves stock from the warehouse onto his vehicle, tagged to his session.</summary>
    public async Task<TransferPostingResult> LoadVehicleAsync(
        int sessionId,
        int warehouseLocationId,
        IReadOnlyList<(int LotId, decimal BaseQuantity)> lines,
        int userId,
        DateOnly? businessDate = null,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireOpenAsync(sessionId, cancellationToken);
        var distributor = await context.Distributors
            .FirstAsync(d => d.Id == session.DistributorId, cancellationToken);

        var transfer = new StockTransfer
        {
            FromLocationId = warehouseLocationId,
            ToLocationId = distributor.StockLocationId,
            DistributorSessionId = sessionId,
            DocDate = businessDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
        };
        context.StockTransfers.Add(transfer);
        await context.SaveChangesAsync(cancellationToken);

        foreach (var (lotId, quantity) in lines)
        {
            var lot = await context.Lots.FirstOrDefaultAsync(l => l.Id == lotId, cancellationToken)
                ?? throw new DomainException($"Lot {lotId} does not exist.");
            transfer.AddItem(lot, quantity);
        }

        await context.SaveChangesAsync(cancellationToken);
        return await transfers.PostAsync(transfer.Id, userId, businessDate, cancellationToken);
    }

    /// <summary>
    /// Settles the session in one transaction: count the vehicle, count the cash, write the
    /// shortage adjustment, take his commission, and record the whole snapshot on the session.
    /// </summary>
    public async Task<SettlementResult> SettleAsync(
        int sessionId,
        decimal countedCash,
        IReadOnlyDictionary<int, decimal>? countedQuantities = null,
        int userId = 1,
        CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction tx = await TransactionScope.BeginAsync(context, cancellationToken);
        try
        {
            var result = await SettleCoreAsync(sessionId, countedCash, countedQuantities, userId, cancellationToken);
            await TransactionScope.CommitAsync(tx, context, cancellationToken);
            return result;
        }
        catch
        {
            await TransactionScope.RollbackAsync(tx, context, cancellationToken);
            throw;
        }
    }

    private async Task<SettlementResult> SettleCoreAsync(
        int sessionId,
        decimal countedCash,
        IReadOnlyDictionary<int, decimal>? countedQuantities,
        int userId,
        CancellationToken cancellationToken)
    {
        var session = await context.DistributorSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            ?? throw new DomainException($"Session {sessionId} does not exist.");

        if (!session.IsOpen)
            throw new DomainException($"Session {sessionId} is already closed.");

        var distributor = await context.Distributors
            .FirstAsync(d => d.Id == session.DistributorId, cancellationToken);

        // 1. Count the vehicle. A van with nothing on it has nothing to count, and a count document
        // with no lines is not a document — so the count is skipped rather than failing settlement.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var hasStock = await context.StockBalances
            .AnyAsync(b => b.LocationId == distributor.StockLocationId, cancellationToken);

        int? countId = null;
        string? countDocNo = null;
        var shortage = 0m;

        if (hasStock)
        {
            var count = await counts.BeginAsync(
                distributor.StockLocationId, today, userId, sessionId, cancellationToken);

            if (countedQuantities is not null)
            {
                foreach (var (lotId, counted) in countedQuantities)
                    await counts.RecordCountAsync(count.Id, lotId, counted, cancellationToken);
            }

            var countResult = await counts.PostAsync(count.Id, userId, cancellationToken);
            countId = count.Id;
            countDocNo = countResult.DocNo;
            shortage = await counts.ShortageValueAsync(count.Id, cancellationToken);
        }

        // 2. What he sold, returned and collected during the session.
        var sales = await context.Sales
            .Where(s => s.DistributorSessionId == sessionId && s.Status == DocumentStatus.Posted)
            .ToListAsync(cancellationToken);

        var salesTotal = Rounding.Amount(sales
            .Where(s => s.Type == Domain.Sales.SaleType.Invoice).Sum(s => s.Total));
        var returnsTotal = Rounding.Amount(sales
            .Where(s => s.Type == Domain.Sales.SaleType.Return).Sum(s => s.Total));

        var collectionsTotal = Rounding.Amount(await context.Payments
            .Where(p => p.DistributorSessionId == sessionId
                     && p.Status == DocumentStatus.Posted
                     && p.Direction == PaymentDirection.In)
            .Select(p => (decimal?)p.Amount)
            .SumAsync(cancellationToken) ?? 0m);

        // 3. Expected cash is his custody balance, from the ledger rather than from memory.
        var expectedCash = Rounding.Amount(await context.CashTransactions
            .Where(t => t.CashAccountId == distributor.CashAccountId)
            .Select(t => (decimal?)t.Amount)
            .SumAsync(cancellationToken) ?? 0m);

        var cashDifference = Rounding.Amount(countedCash - expectedCash);

        // 4. Commission, on whichever basis he is on.
        var basis = distributor.CommissionBasis == CommissionBasis.Sales
            ? Rounding.Amount(salesTotal - returnsTotal)
            : collectionsTotal;
        var commission = distributor.CommissionOn(basis);

        // 5. The snapshot.
        session.Status = DistributorSessionStatus.Closed;
        session.ClosedAt = DateTime.UtcNow;
        session.ClosedBy = userId;
        session.SalesTotal = salesTotal;
        session.ReturnsTotal = returnsTotal;
        session.CollectionsTotal = collectionsTotal;
        session.ExpectedCash = expectedCash;
        session.CountedCash = Rounding.Amount(countedCash);
        session.CashDifference = cashDifference;
        session.StockShortageValue = shortage;
        session.CommissionAmount = commission;
        session.StockCountId = countId;

        // Commission is payable from the company, so it leaves the custody account when he has
        // already banked it. Recorded as an expense against the same account for simplicity.
        if (commission > 0m)
        {
            context.CashTransactions.Add(new CashTransaction
            {
                CashAccountId = distributor.CashAccountId,
                OccurredAt = DateTime.UtcNow,
                BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Kind = CashTransactionKind.Expense,
                Amount = -commission,
                Category = ExpenseCategory.Commission,
                DistributorSessionId = sessionId,
                Note = $"Commission for session {sessionId}",
                CreatedBy = userId,
            });
        }

        await context.SaveChangesAsync(cancellationToken);

        return new SettlementResult(
            sessionId, countId, countDocNo,
            salesTotal, returnsTotal, collectionsTotal,
            expectedCash, Rounding.Amount(countedCash), cashDifference,
            shortage, commission);
    }

    private async Task<DistributorSession> RequireOpenAsync(int sessionId, CancellationToken cancellationToken)
    {
        var session = await context.DistributorSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            ?? throw new DomainException($"Session {sessionId} does not exist.");

        if (!session.IsOpen)
            throw new DomainException($"Session {sessionId} is already closed.");

        return session;
    }
}