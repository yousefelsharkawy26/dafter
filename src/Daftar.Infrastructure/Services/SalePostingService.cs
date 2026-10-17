using Daftar.Domain.Inventory;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Sales;
using Daftar.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace Daftar.Infrastructure.Services;

public sealed record SalePostingResult(
    int SaleId,
    string DocNo,
    IReadOnlyList<(int LotId, decimal Quantity)> Allocations);

/// <summary>
/// Posting a sale invoice — drawing stock out of specific lots.
///
/// In one transaction: the header takes its DocNo, each line allocates across batches oldest-first,
/// the balances drop, inventory movements are written, and the party's receivable is recorded.
/// Any shortfall aborts the whole post, so a sale can never consume stock that was not there.
///
/// Returns post the mirror image, restocking the same lots.
/// </summary>
public sealed class SalePostingService(DaftarContext context)
{
    public async Task<SalePostingResult> PostAsync(
        int saleId,
        int userId,
        DateOnly? businessDate = null,
        CancellationToken cancellationToken = default)
    {
        context.RequirePermission(Daftar.Domain.Security.Role.Codes.SalesCreate);

        await using TransactionScope tx = await TransactionScope.BeginAsync(context, cancellationToken);
        try
        {
            var result = await PostCoreAsync(saleId, userId, businessDate, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Refuses the sale when the customer may not take the credit it leaves them owing: an account
    /// the business has stopped trading with, one that is still pending approval, or a balance past
    /// the credit limit.
    ///
    /// <para>
    /// <see cref="Daftar.Domain.Parties.Party.CreditLimit"/> has been stored, edited behind its
    /// own permission and shown on screen since the schema was written, and nothing consulted it.
    /// A customer with a 500 limit could be invoiced for anything, and the limit on the parties
    /// screen was a note rather than a rule.
    /// </para>
    ///
    /// <para>
    /// The check belongs here, after the voucher, and not inside <see cref="PostAsync"/>: the
    /// invoice is posted first because the voucher references it, so mid-transaction the balance
    /// sits at its peak and a cash sale would be refused for headroom the customer never used.
    /// Reading the ledger here gives the figure the customer is actually left owing.
    /// </para>
    ///
    /// <para>
    /// Being a separate call rather than an invariant, it holds only where a caller invokes it.
    /// The two paths that create an invoice do; the return path reduces the balance and so cannot
    /// breach a limit from below.
    /// </para>
    ///
    /// <para>
    /// Only the credit is gated, so a sale that leaves nothing owing is allowed to every account the
    /// business is still willing to trade with. <see cref="Daftar.Domain.Parties.PartyStatus"/>
    /// documents <c>Pending</c> as "cash only until the manager approves", and it is how every
    /// customer a distributor creates in the field starts: refusing those outright turned the settle
    /// wizard's own new customers into ones it could never invoice.
    /// </para>
    /// </summary>
    public async Task EnsureCreditAllowsAsync(int saleId, CancellationToken cancellationToken = default)
    {
        var sale = await context.Sales
            .Where(s => s.Id == saleId)
            .Select(s => new { s.PartyId, s.Party!.Name, s.Party!.CreditLimit, s.Party!.Status })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainException($"Sale {saleId} does not exist.");

        // A blocked or archived account is not a credit question: the business is not trading with
        // it, and a balance of zero does not make it sellable.
        if (sale.Status is Domain.Parties.PartyStatus.Blocked or Domain.Parties.PartyStatus.Archived)
            throw new DomainException(
                $"حساب '{sale.Name}' مش نشط، مش ينفع تتباعله فاتورة.");

        // The ledger is the balance of record. Both the invoice and the voucher are already in
        // it by now, so this is what the customer is left owing, not what they were charged.
        var balance = Rounding.Amount(await context.PartyAccountTransactions
            .Where(t => t.PartyId == sale.PartyId)
            .Select(t => (decimal?)t.Amount)
            .SumAsync(cancellationToken) ?? 0m);

        // Only what they owe us is credit, and a negative balance is money they are owed, which no
        // limit should cap. Nothing owing means nothing was sold on credit, so no status can refuse
        // it -- see the Pending note above.
        if (balance <= 0m)
            return;

        if (sale.Status == Domain.Parties.PartyStatus.Pending)
            throw new DomainException(
                $"حساب '{sale.Name}' لسه مستني موافقة المدير — ينفع يشتري نقدي بس.");

        if (balance > sale.CreditLimit)
            throw new DomainException(
                $"رصيد '{sale.Name}' بعد الفاتورة {balance:N2} ج.م، وهو أكبر من حد الائتمان "
                + $"{sale.CreditLimit:N2} ج.م. عدّل الحد أو حطّم نقدي.");
    }

    private async Task<SalePostingResult> PostCoreAsync(
        int saleId,
        int userId,
        DateOnly? businessDate,
        CancellationToken cancellationToken)
    {
        // Stamped with whoever is signed in, not with whatever the caller passed: every
        // call site in the app was passing a literal 1, so every document recorded the seeded
        // admin as the one who posted it while the audit log named the real operator.
        userId = context.PostingActor(userId);
        var sale = await context.Sales
            .Include(s => s.Items)
            .ThenInclude(i => i.Lots)
            .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken)
            ?? throw new DomainException($"Sale {saleId} does not exist.");

        if (!sale.IsDraft)
            throw new DomainException($"Sale {saleId} is already {sale.Status}.");

        var now = DateTime.UtcNow;
        var date = businessDate ?? sale.DocDate;

        var docNo = await NextDocNoAsync(
            sale.Type == SaleType.Return ? DocumentType.SaleReturn : DocumentType.Sale, date.Year, cancellationToken);

        var allocations = new List<(int, decimal)>();

        foreach (var item in sale.Items)
        {
            var isReturn = sale.Type == SaleType.Return;

            // A return restocks the batches the original line drew from, in the amounts it drew.
            if (isReturn)
            {
                var returned = await RestockReturnedLineAsync(sale, item, date, now, userId, cancellationToken);
                allocations.AddRange(returned);
            }
            else
            {
                var balances = await LoadBalancesAsync(item.ProductId, sale.LocationId, cancellationToken);

                // Throws when the sale would go negative or only blocked stock exists.
                item.AllocateFrom(balances);

                foreach (var line in item.Lots)
                {
                    ApplyMovement(
                        lotId: line.LotId,
                        productId: item.ProductId,
                        locationId: sale.LocationId,
                        kind: InventoryMovementKind.Sale,
                        magnitude: line.BaseQuantity,
                        sourceType: MovementSourceType.Sale,
                        sourceId: sale.Id,
                        userId: userId,
                        businessDate: date,
                        nowUtc: now);

                    allocations.Add((line.LotId, line.BaseQuantity));
                }
            }
        }

        sale.Post(docNo, userId, now);

        // The receivable: a sale increases what the customer owes us.
        context.PartyAccountTransactions.Add(new Domain.Finance.PartyAccountTransaction
        {
            PartyId = sale.PartyId,
            OccurredAt = now,
            BusinessDate = date,
            Kind = sale.Type == SaleType.Return
                ? Domain.Finance.PartyLedgerKind.SaleReturn
                : Domain.Finance.PartyLedgerKind.Sale,
            Amount = sale.Type == SaleType.Return ? -sale.Total : sale.Total,
            DueDate = sale.DueDate,
            SourceType = "Sale",
            SourceId = sale.Id,
            CreatedBy = userId,
        });

        await context.SaveChangesAsync(cancellationToken);

        return new SalePostingResult(sale.Id, docNo, allocations);
    }

    /// <summary>
    /// A return line puts stock back into exactly the lots the original line drew from. Returned
    /// quantity is capped by what was sold and not already returned, so a lot cannot be credited twice.
    /// </summary>
    private async Task<List<(int LotId, decimal Quantity)>> RestockReturnedLineAsync(
        Sale sale,
        SaleItem item,
        DateOnly businessDate,
        DateTime now,
        int userId,
        CancellationToken cancellationToken)
    {
        if (item.OriginalSaleItemId is null)
            throw new DomainException($"Return line {item.LineNo} has no OriginalSaleItemId.");

        var result = new List<(int, decimal)>();

        foreach (var draw in item.Lots)
        {
            var lot = await context.Lots.FirstOrDefaultAsync(l => l.Id == draw.LotId, cancellationToken)
                ?? throw new DomainException($"Lot {draw.LotId} does not exist.");

            ApplyMovement(
                lotId: lot.Id,
                productId: item.ProductId,
                locationId: sale.LocationId,
                kind: InventoryMovementKind.SaleReturn,
                magnitude: draw.BaseQuantity,
                sourceType: MovementSourceType.Sale,
                sourceId: sale.Id,
                userId: userId,
                businessDate: businessDate,
                nowUtc: now);

            result.Add((lot.Id, draw.BaseQuantity));
        }

        return result;
    }

    private async Task<List<StockBalance>> LoadBalancesAsync(
        int productId,
        int locationId,
        CancellationToken cancellationToken)
        => await context.StockBalances
            .Include(b => b.Lot)
            .Where(b => b.LocationId == locationId && b.Lot!.ProductId == productId && b.Quantity > 0m)
            .ToListAsync(cancellationToken);

    private async Task<string> NextDocNoAsync(DocumentType docType, int year, CancellationToken cancellationToken)
    {
        var sequence = await context.DocumentSequences
            .FirstOrDefaultAsync(s => s.DocType == docType && s.Year == year, cancellationToken)
            ?? await context.DocumentSequences
                .FirstOrDefaultAsync(s => s.DocType == docType && s.Year == 0, cancellationToken);

        if (sequence is null)
            throw new DomainException($"No DocumentSequences row for {docType} in {year}.");

        return sequence.Take(year);
    }

    /// <summary>Stages a movement and moves the balance; the caller commits them together.</summary>
    public void ApplyMovement(
        int lotId,
        int productId,
        int locationId,
        InventoryMovementKind kind,
        decimal magnitude,
        MovementSourceType sourceType,
        int? sourceId,
        int userId,
        DateOnly businessDate,
        DateTime nowUtc)
    {
        var quantity = InventoryMovement.SignedQuantity(kind, magnitude);

        var balance = context.StockBalances
            .Include(b => b.Lot)
            .FirstOrDefault(b => b.LotId == lotId && b.LocationId == locationId);

        if (balance is null)
        {
            throw new InsufficientStockException(
                $"No stock balance for lot {lotId} at location {locationId}.");
        }

        balance.Apply(quantity, nowUtc);

        context.InventoryMovements.Add(new InventoryMovement
        {
            OccurredAt = nowUtc,
            BusinessDate = businessDate,
            ProductId = productId,
            LotId = lotId,
            LocationId = locationId,
            Kind = kind,
            Quantity = quantity,
            SourceType = sourceType,
            SourceId = sourceId,
            CreatedBy = userId,
        });
    }
}