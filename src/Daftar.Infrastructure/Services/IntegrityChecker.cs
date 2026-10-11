using Microsoft.EntityFrameworkCore;
using Shared = Daftar.Domain.Shared;

namespace Daftar.Infrastructure.Services;

public sealed record IntegrityProblem(string Check, string Detail);

public sealed record IntegrityReport(
    int ChecksRun,
    IReadOnlyList<IntegrityProblem> Problems,
    bool Completed = true)
{
    /// <summary>
    /// True when the checks finished and found nothing. False when the checker itself could not
    /// run, so "unverified" is never mistaken for "verified and fine".
    /// </summary>
    public bool IsClean => Completed && Problems.Count == 0;

    /// <summary>The scan threw or ran out of memory; nothing was verified either way.</summary>
    public static IntegrityReport Skipped() => new(0, Array.Empty<IntegrityProblem>(), Completed: false);

    /// <summary>Startup could not get the database into a usable state.</summary>
    public static IntegrityReport Failed(string check, string detail)
        => new(0, [new IntegrityProblem(check, detail)], Completed: false);

    public override string ToString()
    {
        if (Problems.Count > 0)
            return $"FAILED — {Problems.Count} problem(s):\n"
                 + string.Join("\n", Problems.Select(p => $"  [{p.Check}] {p.Detail}"));

        return !Completed
            ? "SKIPPED — the integrity check did not run; the ledger is unverified."
            : $"OK — {ChecksRun} checks, no problems.";
    }
}

/// <summary>
/// The integrity checks from the schema spec. Intended to run on startup, before a backup, or on
/// demand — this is the thing that notices if the database and the ledgers have drifted apart.
///
/// Every sum is computed in C#. SQLite stores decimals as TEXT, so SUM() over a decimal column
/// is not trustworthy and would defeat the point of the check.
/// </summary>
public sealed class IntegrityChecker(DaftarContext context)
{
    /// <summary>Runs all four checks. Does not fix anything — reporting only.</summary>
    public async Task<IntegrityReport> RunAllAsync(CancellationToken ct = default)
    {
        var problems = new List<IntegrityProblem>();

        problems.AddRange(await BalancesMatchMovementsAsync(ct));
        problems.AddRange(await EveryPostedDocumentHasItsLedgerRowAsync(ct));
        problems.AddRange(await PostedLinesAreFullyAllocatedAsync(ct));
        problems.AddRange(await PostedDocumentsAreNumberedAsync(ct));

        return new IntegrityReport(4, problems);
    }

    /// <summary>1. Every (lot, location) balance equals the sum of its movements, and is non-negative.</summary>
    private async Task<List<IntegrityProblem>> BalancesMatchMovementsAsync(CancellationToken ct)
    {
        var problems = new List<IntegrityProblem>();

        var balances = await context.StockBalances.ToListAsync(ct);
        var movements = await context.InventoryMovements.ToListAsync(ct);

        var summed = movements
            .GroupBy(m => (m.LotId, m.LocationId))
            .ToDictionary(
                g => g.Key,
                g => Shared.Rounding.Quantity(g.Sum(m => m.Quantity)));

        foreach (var balance in balances)
        {
            var key = (balance.LotId, balance.LocationId);
            summed.TryGetValue(key, out var expected);

            if (expected != balance.Quantity)
                problems.Add(new IntegrityProblem(
                    "balance-vs-movements",
                    $"StockBalances (Lot {key.LotId}, Location {key.LocationId}) is {balance.Quantity} "
                    + $"but its movements sum to {expected}."));

            if (balance.Quantity < 0m)
                problems.Add(new IntegrityProblem(
                    "negative-balance",
                    $"StockBalances (Lot {key.LotId}, Location {key.LocationId}) is negative: {balance.Quantity}."));
        }

        // Movements with no balance row at all are drift too.
        foreach (var (key, total) in summed)
        {
            var exists = balances.Any(b => b.LotId == key.LotId && b.LocationId == key.LocationId);
            if (!exists && total != 0m)
                problems.Add(new IntegrityProblem(
                    "missing-balance",
                    $"Movements exist for (Lot {key.LotId}, Location {key.LocationId}) summing to {total}, "
                    + "but there is no StockBalances row."));
        }

        return problems;
    }

    /// <summary>
    /// 2. Every posted sale, purchase or payment has exactly one ledger row, carrying the right
    /// amount.
    ///
    /// The amounts are the point. A row that exists but says 90 for a 100 invoice passes a
    /// count-only check and leaves the customer over-billed by 10 with nothing to notice it. The
    /// sign is compared by magnitude, because the sign is the ledger's own convention and is
    /// already guarded by PartyAccountTransaction.Validate; what matters here is that the two
    /// sides are the same number.
    /// </summary>
    private static void PostedDocumentsHaveLedgerRows(
        string kind,
        IReadOnlyList<(int DocumentId, string DocNo, decimal Total)> posted,
        IReadOnlyList<(int SourceId, decimal Amount)> ledgerRows,
        List<IntegrityProblem> problems)
    {
        foreach (var (documentId, docNo, total) in posted)
        {
            var rows = ledgerRows.Where(r => r.SourceId == documentId).ToList();

            if (rows.Count == 0)
            {
                problems.Add(new IntegrityProblem(
                    $"{kind}-ledger-row",
                    $"Posted {kind} {docNo} has no PartyAccountTransactions row."));
                continue;
            }

            if (rows.Count > 1)
            {
                problems.Add(new IntegrityProblem(
                    $"{kind}-ledger-row",
                    $"Posted {kind} {docNo} has {rows.Count} ledger rows; expected exactly 1."));
                continue;
            }

            if (Math.Abs(rows[0].Amount) != Math.Abs(total))
            {
                problems.Add(new IntegrityProblem(
                    $"{kind}-ledger-amount",
                    $"Posted {kind} {docNo} is {total} but its ledger row says {rows[0].Amount}."));
            }
        }
    }

    private async Task<List<IntegrityProblem>> EveryPostedDocumentHasItsLedgerRowAsync(CancellationToken ct)
    {
        var problems = new List<IntegrityProblem>();

        var sales = await context.Sales
            .Where(s => s.Status == Shared.DocumentStatus.Posted)
            .Select(s => new { s.Id, s.DocNo, s.Total })
            .ToListAsync(ct);
        var purchases = await context.Purchases
            .Where(p => p.Status == Shared.DocumentStatus.Posted)
            .Select(p => new { p.Id, p.DocNo, p.Total })
            .ToListAsync(ct);

        // Spelled out with || rather than an `is` pattern: this is translated to SQL, and an
        // expression tree cannot carry a pattern match.
        var ledgerRows = await context.PartyAccountTransactions
            .Where(t => t.SourceType == "Sale" || t.SourceType == "Purchase" || t.SourceType == "Payment")
            .Select(t => new { t.SourceId, t.SourceType, t.Amount })
            .ToListAsync(ct);

        PostedDocumentsHaveLedgerRows(
            "sale",
            sales.Select(s => (s.Id, s.DocNo ?? "?", s.Total)).ToList(),
            ledgerRows.Where(r => r.SourceType == "Sale")
                .Select(r => (r.SourceId ?? 0, r.Amount)).ToList(),
            problems);

        PostedDocumentsHaveLedgerRows(
            "purchase",
            purchases.Select(p => (p.Id, p.DocNo ?? "?", p.Total)).ToList(),
            ledgerRows.Where(r => r.SourceType == "Purchase")
                .Select(r => (r.SourceId ?? 0, r.Amount)).ToList(),
            problems);

        // A payment moves money in two places at once: the cash account it came out of, and the
        // party's balance it settles. Either one moving without the other is a real discrepancy,
        // and both were previously unchecked.
        var payments = await context.Payments
            .Where(p => p.Status == Shared.DocumentStatus.Posted)
            .Select(p => new { p.Id, p.DocNo, p.Amount })
            .ToListAsync(ct);

        var cashRows = await context.CashTransactions
            .Where(t => t.PaymentId != null)
            .Select(t => new { PaymentId = t.PaymentId!.Value, t.Amount })
            .ToListAsync(ct);

        foreach (var payment in payments)
        {
            var expected = Math.Abs(payment.Amount);

            var cash = cashRows.Where(r => r.PaymentId == payment.Id).ToList();
            if (cash.Count == 0)
            {
                problems.Add(new IntegrityProblem(
                    "payment-cash-row",
                    $"Posted payment {payment.DocNo} has no CashTransactions row."));
            }
            else if (cash.Count > 1)
            {
                problems.Add(new IntegrityProblem(
                    "payment-cash-row",
                    $"Posted payment {payment.DocNo} has {cash.Count} cash rows; expected exactly 1."));
            }
            else if (Math.Abs(cash[0].Amount) != expected)
            {
                problems.Add(new IntegrityProblem(
                    "payment-cash-amount",
                    $"Posted payment {payment.DocNo} is {payment.Amount} "
                    + $"but its cash row says {cash[0].Amount}."));
            }

            var partyRows = ledgerRows
                .Where(r => r.SourceType == "Payment" && r.SourceId == payment.Id)
                .ToList();

            if (partyRows.Count == 0)
            {
                problems.Add(new IntegrityProblem(
                    "payment-ledger-row",
                    $"Posted payment {payment.DocNo} has no PartyAccountTransactions row, "
                    + "so the amount collected never settled against the party's balance."));
            }
            else
            {
                var settled = partyRows.Sum(r => r.Amount);
                if (Math.Abs(settled) != expected)
                {
                    problems.Add(new IntegrityProblem(
                        "payment-ledger-amount",
                        $"Posted payment {payment.DocNo} is {payment.Amount} "
                        + $"but its ledger rows settle to {settled}."));
                }
            }
        }

        return problems;
    }

    /// <summary>3. Posted sale lines are fully covered by lots; posted invoice lines have a lot.</summary>
    private async Task<List<IntegrityProblem>> PostedLinesAreFullyAllocatedAsync(CancellationToken ct)
    {
        var problems = new List<IntegrityProblem>();

        var saleLines = await context.SaleItems
            .Where(i => i.Sale!.Status == Shared.DocumentStatus.Posted)
            .Select(i => new { i.Id, i.SaleId, i.BaseQuantity, i.Sale!.DocNo })
            .ToListAsync(ct);

        var allocations = await context.SaleItemLots
            .GroupBy(l => l.SaleItemId)
            .Select(g => new { SaleItemId = g.Key, Total = g.Sum(l => l.BaseQuantity) })
            .ToListAsync(ct);

        var allocatedBy = allocations.ToDictionary(a => a.SaleItemId, a => a.Total);

        foreach (var line in saleLines)
        {
            var total = allocatedBy.GetValueOrDefault(line.Id);
            var expected = Shared.Rounding.Quantity(line.BaseQuantity);

            if (total != expected)
                problems.Add(new IntegrityProblem(
                    "sale-line-lots",
                    $"Sale {line.DocNo} line {line.Id} is {expected} base units but its lots cover {total}."));
        }

        var purchaseLines = await context.PurchaseItems
            .Where(i => i.Purchase!.Status == Shared.DocumentStatus.Posted
                     && i.Purchase!.Type == Daftar.Domain.Purchasing.PurchaseType.Invoice)
            .Select(i => new { i.Id, i.LotId, i.Purchase!.DocNo })
            .ToListAsync(ct);

        foreach (var line in purchaseLines.Where(l => l.LotId is null))
            problems.Add(new IntegrityProblem(
                "purchase-line-lot",
                $"Posted purchase {line.DocNo} line {line.Id} has no LotId."));

        return problems;
    }

    /// <summary>4. Every posted document has a number, and numbers are contiguous per type.</summary>
    private async Task<List<IntegrityProblem>> PostedDocumentsAreNumberedAsync(CancellationToken ct)
    {
        var problems = new List<IntegrityProblem>();

        foreach (var (label, missing) in new (string, int?)[]
                 {
                     ("sale", await context.Sales
                         .Where(s => s.Status == Shared.DocumentStatus.Posted && s.DocNo == null)
                         .CountAsync(ct)),
                     ("purchase", await context.Purchases
                         .Where(p => p.Status == Shared.DocumentStatus.Posted && p.DocNo == null)
                         .CountAsync(ct)),
                     ("payment", await context.Payments
                         .Where(p => p.Status == Shared.DocumentStatus.Posted && p.DocNo == null)
                         .CountAsync(ct)),
                     ("transfer", await context.StockTransfers
                         .Where(t => t.Status == Shared.DocumentStatus.Posted && t.DocNo == null)
                         .CountAsync(ct)),
                     ("count", await context.StockCounts
                         .Where(c => c.Status == Shared.DocumentStatus.Posted && c.DocNo == null)
                         .CountAsync(ct)),
                 })
        {
            if (missing > 0)
                problems.Add(new IntegrityProblem(
                    "posted-without-number",
                    $"{missing} posted {label}(s) have no DocNo."));
        }

        // Contiguity: the assigned numbers for each type should be 1..n with no holes.
        var byType = await context.DocumentSequences
            .Where(s => s.Year == 0)
            .Select(s => new { s.DocType, s.Prefix, s.Padding, s.NextNumber })
            .ToListAsync(ct);

        foreach (var sequence in byType)
        {
            var next = (int)(await NextNumberFor(sequence.DocType, sequence.Prefix, sequence.Padding, ct));

            if (next != sequence.NextNumber)
                problems.Add(new IntegrityProblem(
                    "sequence-contiguity",
                    $"{sequence.DocType}: highest issued number is {next - 1} "
                    + $"but DocumentSequences.NextNumber is {sequence.NextNumber} — "
                    + (next > sequence.NextNumber ? "numbers were skipped" : "a number was reused")));
        }

        return problems;
    }

    /// <summary>The highest numeric document number actually issued for a type.</summary>
    private async Task<long> NextNumberFor(
        Shared.DocumentType docType,
        string prefix,
        int padding,
        CancellationToken ct)
    {
        var issued = new List<string>();

        if (docType == Shared.DocumentType.Party)
        {
            // Party codes are stored in Parties.Code, not in the transactional tables.
            issued.AddRange(await context.Parties
                .Where(p => p.Code != null).Select(p => p.Code!).ToListAsync(ct));
        }
        else
        {
            issued.AddRange(await context.Sales
                .Where(s => s.DocNo != null).Select(s => s.DocNo!).ToListAsync(ct));
            issued.AddRange(await context.Purchases
                .Where(p => p.DocNo != null).Select(p => p.DocNo!).ToListAsync(ct));
            issued.AddRange(await context.Payments
                .Where(p => p.DocNo != null).Select(p => p.DocNo!).ToListAsync(ct));

            if (docType is Shared.DocumentType.Transfer or Shared.DocumentType.Count)
            {
                issued.AddRange(await context.StockTransfers
                    .Where(t => t.DocNo != null).Select(t => t.DocNo!).ToListAsync(ct));
                issued.AddRange(await context.StockCounts
                    .Where(c => c.DocNo != null).Select(c => c.DocNo!).ToListAsync(ct));
            }
        }

        var highest = issued
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => long.TryParse(n[prefix.Length..], out var v) ? v : 0L)
            .DefaultIfEmpty(0L)
            .Max();

        return highest + 1;
    }
}