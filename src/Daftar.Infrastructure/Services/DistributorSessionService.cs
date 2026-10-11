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
    decimal CommissionAmount,
    decimal CashSalesTotal = 0m,
    decimal DebtCollectionsTotal = 0m,
    decimal CreditSalesTotal = 0m);

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
        // Settlement hands the company its money, pays commission and writes the van's stock
        // down, all from one call. Without this anyone who could reach it could close a session.
        context.RequirePermission(Daftar.Domain.Security.Role.Codes.PaymentsManage);

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
    /// Returns unsold stock from the vehicle back to the warehouse.
    /// If lines is null, all positive stock balances remaining on the vehicle are returned.
    /// </summary>
    public async Task<TransferPostingResult?> UnloadVehicleAsync(
        int sessionId,
        int warehouseLocationId,
        IReadOnlyList<(int LotId, decimal BaseQuantity)>? lines = null,
        int userId = 1,
        DateOnly? businessDate = null,
        CancellationToken cancellationToken = default)
    {
        var session = await context.DistributorSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            ?? throw new DomainException($"Session {sessionId} does not exist.");

        var distributor = await context.Distributors
            .FirstAsync(d => d.Id == session.DistributorId, cancellationToken);

        List<(int LotId, decimal BaseQuantity)> itemsToReturn;
        if (lines is not null)
        {
            itemsToReturn = lines.Where(l => l.BaseQuantity > 0).ToList();
        }
        else
        {
            var balances = await context.StockBalances
                .Where(b => b.LocationId == distributor.StockLocationId && b.Quantity > 0)
                .ToListAsync(cancellationToken);
            itemsToReturn = balances.Select(b => (b.LotId, b.Quantity)).ToList();
        }

        if (itemsToReturn.Count == 0)
            return null;

        var date = businessDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var transfer = new StockTransfer
        {
            FromLocationId = distributor.StockLocationId,
            ToLocationId = warehouseLocationId,
            DistributorSessionId = sessionId,
            DocDate = date,
            Notes = $"تفريغ مرتجع بضاعة متبقية من جولة #{sessionId} للمستودع",
        };
        context.StockTransfers.Add(transfer);
        await context.SaveChangesAsync(cancellationToken);

        foreach (var (lotId, quantity) in itemsToReturn)
        {
            var lot = await context.Lots.FirstOrDefaultAsync(l => l.Id == lotId, cancellationToken)
                ?? throw new DomainException($"Lot {lotId} does not exist.");
            transfer.AddItem(lot, quantity);
        }

        await context.SaveChangesAsync(cancellationToken);
        return await transfers.PostAsync(transfer.Id, userId, date, cancellationToken);
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
        await using TransactionScope tx = await TransactionScope.BeginAsync(context, cancellationToken);
        try
        {
            var result = await SettleCoreAsync(sessionId, countedCash, countedQuantities, userId, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
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
        //
        // "Nothing on it" means a non-zero quantity, not merely that a balance row exists. Selling
        // a lot out empties it by taking the quantity to zero rather than deleting the row, so
        // asking only whether the row was present counted every van that had ever been loaded and
        // produced a count document with no lines on every session thereafter.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var hasStock = await context.StockBalances
            .AnyAsync(b => b.LocationId == distributor.StockLocationId
                        && b.Quantity != 0m,
                     cancellationToken);

        int? countId = null;
        string? countDocNo = null;
        var shortage = 0m;

        if (hasStock)
        {
            var count = await counts.BeginAsync(
                distributor.StockLocationId, today, userId, sessionId, cancellationToken);

            foreach (var item in count.Items)
            {
                var counted = (countedQuantities is not null && countedQuantities.TryGetValue(item.LotId, out var val))
                    ? val
                    : item.SystemQty;

                await counts.RecordCountAsync(count.Id, item.LotId, counted, cancellationToken);
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

        var cashSalesTotal = Rounding.Amount(await context.Payments
            .Where(p => p.DistributorSessionId == sessionId
                     && p.Status == DocumentStatus.Posted
                     && p.Direction == PaymentDirection.In
                     && p.SaleId != null)
            .Select(p => (decimal?)p.Amount)
            .SumAsync(cancellationToken) ?? 0m);

        var debtCollectionsTotal = Rounding.Amount(await context.Payments
            .Where(p => p.DistributorSessionId == sessionId
                     && p.Status == DocumentStatus.Posted
                     && p.Direction == PaymentDirection.In
                     && p.SaleId == null)
            .Select(p => (decimal?)p.Amount)
            .SumAsync(cancellationToken) ?? 0m);

        var creditSalesTotal = Rounding.Amount(Math.Max(0m, salesTotal - cashSalesTotal));
        var collectionsTotal = Rounding.Amount(cashSalesTotal + debtCollectionsTotal);

        // 3. Expected cash is strictly what was collected during THIS session:
        //    Cash from sales + debt collections.
        //    Previous session balances must never carry over into this session.
        var expectedCash = collectionsTotal;

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

        // The company's till. Commission is the company's cost, so it is paid from here rather
        // than out of the money the distributor is carrying: charging the van meant the company
        // received the handover and paid the commission out of the same pot, and the custody
        // account went negative for a second and unrelated reason.
        var mainCashAccount = await context.CashAccounts
            .FirstOrDefaultAsync(a => a.Type == CashAccountType.Cash && a.IsActive, cancellationToken);

        if (commission > 0m)
        {
            context.CashTransactions.Add(new CashTransaction
            {
                CashAccountId = mainCashAccount?.Id ?? distributor.CashAccountId,
                OccurredAt = DateTime.UtcNow,
                BusinessDate = today,
                Kind = CashTransactionKind.Expense,
                Amount = -commission,
                Category = ExpenseCategory.Commission,
                DistributorSessionId = sessionId,
                Note = $"Commission for session {sessionId}",
                CreatedBy = userId,
            });
        }

        // Hand over counted cash from custody to company cashier account upon session close.
        // Counted, not expected: only what he actually has can be handed over. The gap between
        // the two is dealt with below.
        if (countedCash > 0m && mainCashAccount is not null && mainCashAccount.Id != distributor.CashAccountId)
        {
            var transferRef = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;

            context.CashTransactions.Add(new CashTransaction
            {
                CashAccountId = distributor.CashAccountId,
                OccurredAt = now,
                BusinessDate = today,
                Kind = CashTransactionKind.TransferOut,
                Amount = -countedCash,
                DistributorSessionId = sessionId,
                TransferRef = transferRef,
                Note = $"تسليم عهدة نقدية للجولة {sessionId} من {distributor.Name}",
                CreatedBy = userId,
            });

            context.CashTransactions.Add(new CashTransaction
            {
                CashAccountId = mainCashAccount.Id,
                OccurredAt = now,
                BusinessDate = today,
                Kind = CashTransactionKind.TransferIn,
                Amount = countedCash,
                DistributorSessionId = sessionId,
                TransferRef = transferRef,
                Note = $"استلام عهدة نقدية للجولة {sessionId} من {distributor.Name}",
                CreatedBy = userId,
            });
        }

        // A custody account holds only money being carried for the company, so it must be empty
        // once the session closes. Handing over the counted cash leaves any shortfall sitting
        // there with nothing behind it: the balance said the distributor was still holding the
        // company's money while the cash was simply gone. Writing the difference off as an
        // adjustment makes the loss a row somebody can find, and puts the account back to zero.
        if (cashDifference != 0m)
        {
            context.CashTransactions.Add(new CashTransaction
            {
                CashAccountId = distributor.CashAccountId,
                OccurredAt = DateTime.UtcNow,
                BusinessDate = today,
                Kind = CashTransactionKind.Adjustment,
                Amount = cashDifference,
                DistributorSessionId = sessionId,
                Note = cashDifference < 0
                    ? $"عجز نقدي في جولة {sessionId} من {distributor.Name}"
                    : $"زيادة نقدية في جولة {sessionId} من {distributor.Name}",
                CreatedBy = userId,
            });
        }

        await context.SaveChangesAsync(cancellationToken);

        return new SettlementResult(
            sessionId, countId, countDocNo,
            salesTotal, returnsTotal, collectionsTotal,
            expectedCash, Rounding.Amount(countedCash), cashDifference,
            shortage, commission,
            cashSalesTotal, debtCollectionsTotal, creditSalesTotal);
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