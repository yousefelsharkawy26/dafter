using System;
using System.Collections.Generic;
using System.Linq;

namespace Daftar.Desktop.Receiving;

/// <summary>A candidate product for a scanned line, with the score that produced it.</summary>
public sealed record ProductMatch(int ProductId, string Sku, string Name, int Score);

/// <summary>
/// Matches a scanned name against the catalogue.
///
/// Arabic OCR gets product names roughly 60% right, so this never decides anything on its own —
/// it only proposes the best candidates, ranked, for the manager to accept. Scoring is therefore
/// tuned for "is this the same thing?" rather than for precision.
///
/// Two signals, added together:
///   - a barcode/SKU appearing literally in the line is decisive
///   - otherwise, word overlap after folding the Arabic orthographic variants
/// </summary>
public static class ProductMatcher
{
    /// <summary>Below this, nothing is worth showing as a suggestion.</summary>
    public const int MinimumScore = 25;

    /// <summary>Ranks catalogue entries against one scanned line, best first.</summary>
    public static IReadOnlyList<ProductMatch> Rank(
        string scannedText,
        IEnumerable<(int Id, string Sku, string Name)> catalogue,
        int limit = 5)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var query = InvoiceParser.NormaliseForCompare(scannedText);

        if (query.Length == 0)
            return [];

        var queryWords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var scored = new List<ProductMatch>();

        foreach (var (id, sku, name) in catalogue)
        {
            var score = Score(query, queryWords, sku, name);
            if (score >= MinimumScore)
                scored.Add(new ProductMatch(id, sku, name, score));
        }

        return scored
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    private static int Score(string query, string[] queryWords, string sku, string name)
    {
        // A SKU printed on the invoice is the one thing OCR reads reliably.
        if (!string.IsNullOrWhiteSpace(sku)
            && query.Contains(InvoiceParser.NormaliseForCompare(sku), StringComparison.Ordinal))
        {
            return 100;
        }

        var candidate = InvoiceParser.NormaliseForCompare(name);

        if (candidate.Length == 0)
            return 0;

        if (candidate == query)
            return 95;

        // Every query word appearing in the candidate is the strongest fuzzy signal, because OCR
        // corrupts characters far more often than it drops whole words.
        var hits = queryWords.Count(w => w.Length >= 2 && candidate.Contains(w, StringComparison.Ordinal));
        var coverage = queryWords.Length == 0 ? 0 : hits * 100 / queryWords.Length;

        // And the reverse: candidate words found in the query, so a long catalogue name that
        // merely contains the scanned words still scores.
        var candidateWords = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var reverseHits = candidateWords.Count(w => w.Length >= 2 && query.Contains(w, StringComparison.Ordinal));
        var reverse = candidateWords.Length == 0 ? 0 : reverseHits * 100 / candidateWords.Length;

        return (coverage * 2 + reverse) / 3;
    }
}