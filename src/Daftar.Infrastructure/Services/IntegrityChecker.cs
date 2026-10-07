using Microsoft.EntityFrameworkCore;
using Shared = Daftar.Domain.Shared;

namespace Daftar.Infrastructure.Services;

public sealed record IntegrityProblem(string Check, string Detail);

public sealed record IntegrityReport(
    int ChecksRun,
    IReadOnlyList<IntegrityProblem> Problems)
{
    public bool IsClean => Problems.Count == 0;

    public override string ToString() => IsClean
        ? $"OK — {ChecksRun} checks, no problems."
        : $"FAILED — {Problems.Count} problem(s) in {ChecksRun} checks:\n"
          + string.Join("\n", Problems.Select(p => $"  [{p.Check}] {p.Detail}"));
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

    /// <summary>2. Every posted sale, purchase or payment has exactly one ledger row.</summary>
    private async Task<List<IntegrityProblem>> PostedDocumentsHaveLedgerRowsAsync(
        string kind,
        IReadOnlyList<(int DocumentId, string DocNo)> posted,
        IReadOnlyList<(int SourceId, string Kind)> ledgerRows,
        List<IntegrityProblem> problems)
    {
        foreach (var (documentId, docNo) in posted)
        {
            var count = ledgerRows.Count(r => r.SourceId == documentId);

            if (count == 0)
                problems.Add(new IntegrityProblem(
                    $"{kind}-ledger-row",
                    $"Posted {kind} {docNo} has no PartyAccountTransactions row."));
            else if (count > 1)
                problems.Add(new IntegrityProblem(
                    $"{kind}-ledger-row",
                    $"Posted {kind} {docNo} has {count} ledger rows; expected exactly 1."));
        }

        return problems;
    }

    private async Task<List<IntegrityProblem>> EveryPostedDocumentHasItsLedgerRowAsync(CancellationToken ct)
    {
        var problems = new List<IntegrityProblem>();

        var sales = await context.Sales
            .Where(s => s.Status == Shared.DocumentStatus.Posted)
            .Select(s => new { s.Id, s.DocNo })
            .ToListAsync(ct);
        var purchases = await context.Purchases
            .Where(p => p.Status == Shared.DocumentStatus.Posted)
            .Select(p => new { p.Id, p.DocNo })
            .ToListAsync(ct);
        var ledgerRows = await context.PartyAccountTransactions
            .Select(t => new { t.SourceId, t.SourceType, t.Kind })
            .ToListAsync(ct);

        var byKind = ledgerRows
            .Where(r => r.SourceType is "Sale" or "Purchase")
            .Select(r => (r.SourceId ?? 0, r.SourceType))
            .ToList();

        await PostedDocumentsHaveLedgerRowsAsync(
            "sale", sales.Select(s => (s.Id, s.DocNo ?? "?")).ToList(),
            byKind.Where(r => r.Item2 == "Sale").Select(r => (r.Item1, r.Item2)).ToList(), problems);

        await PostedDocumentsHaveLedgerRowsAsync(
            "purchase", purchases.Select(p => (p.Id, p.DocNo ?? "?")).ToList(),
            byKind.Where(r => r.Item2 == "Purchase").Select(r => (r.Item1, r.Item2)).ToList(), problems);

        // Every posted payment has exactly one cash row.
        var payments = await context.Payments
            .Where(p => p.Status == Shared.DocumentStatus.Posted)
            .Select(p => new { p.Id, p.DocNo })
            .ToListAsync(ct);
        var cashRows = await context.CashTransactions
            .Where(t => t.PaymentId != null)
            .Select(t => t.PaymentId!.Value)
            .ToListAsync(ct);

        foreach (var payment in payments)
        {
            var count = cashRows.Count(id => id == payment.Id);
            if (count == 0)
                problems.Add(new IntegrityProblem(
                    "payment-cash-row",
                    $"Posted payment {payment.DocNo} has no CashTransactions row."));
            else if (count > 1)
                problems.Add(new IntegrityProblem(
                    "payment-cash-row",
                    $"Posted payment {payment.DocNo} has {count} cash rows; expected exactly 1."));
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

        var highest = issued
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => long.TryParse(n[prefix.Length..], out var v) ? v : 0L)
            .DefaultIfEmpty(0L)
            .Max();

        return highest + 1;
    }
}