using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Daftar.Desktop.Receiving;

/// <summary>
/// One line the scanner thinks it saw. Nothing here is trusted: Arabic OCR is roughly 60% accurate
/// on product names while digits are near perfect, so the manager confirms every row before it
/// reaches a purchase.
/// </summary>
public sealed record ScannedLine(
    string RawText,
    string Text,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? Total)
{
    /// <summary>Set once the manager picks which product this actually is.</summary>
    public int? ProductId { get; init; }

    public int? ProductUnitId { get; init; }

    /// <summary>How close the scanned name is to the matched product, 0-100.</summary>
    public int MatchScore { get; init; }

    public bool IsConfirmed => ProductId is not null;
}

/// <summary>Everything the scanner could work out from one image.</summary>
public sealed record ScannedInvoice(
    string RawText,
    string? InvoiceNumber,
    string? SupplierGuess,
    IReadOnlyList<ScannedLine> Lines)
{
    public static ScannedInvoice Empty { get; } = new(string.Empty, null, null, []);
}

/// <summary>
/// Turns OCR text into candidate invoice lines. A pure function over strings: no database, no
/// image handling, so every rule here is testable without Tesseract installed.
/// </summary>
public static partial class InvoiceParser
{
    /// <summary>Arabic-Indic digits, which OCR often returns instead of 0-9.</summary>
    private static readonly char[] ArabicIndic =
        ['٠', '١', '٢', '٣', '٤', '٥', '٦', '٧', '٨', '٩'];

    /// <summary>Extended Arabic-Indic digits (used in invoice forms).</summary>
    private static readonly char[] ArabicIndicExtended =
        ['۰', '۱', '۲', '۳', '۴', '۵', '۶', '۷', '۸', '۹'];

    /// <summary>A money/quantity token: digits, dot or comma, optional thousands separator.</summary>
    [GeneratedRegex(@"[\d٠-٩۰-۹][\d٠-٩۰-۹.,٫٬]*", RegexOptions.Compiled)]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"رقم\s*(?:الفاتورة)?\s*[:#\-]?\s*([A-Za-z0-9][A-Za-z0-9\-/]*)", RegexOptions.Compiled)]
    private static partial Regex InvoiceNumber();

    /// <summary>Labels a supplier goes by on an invoice, most specific first.</summary>
    private static readonly string[] SupplierMarkers =
        [":المورد", "المورد", ":Supplier", "Supplier"];

    /// <summary>Words that mark a heading or a total, never a product line.</summary>
    private static readonly string[] SkipWords =
    [
        "الصنف", "الكمية", "السعر", "الإجمالي", "اجمالي", "الاجمالي", "الإجمالى",
        "رقم", "الفاتورة", "فاتورة", "المورد", "التاريخ", "الخصم", "الضريبه",
        "الضريبة", "invoice", "total", "subtotal", "qty", "price", "amount", "item", "no", "date",
    ];

    /// <summary>
    /// Parses OCR text into candidate lines plus the invoice number. A row needs at least a name
    /// and one number; two or more numbers are read as qty, price, total.
    /// </summary>
    public static ScannedInvoice Parse(string? ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
            return ScannedInvoice.Empty;

        var text = NormaliseDigits(StripBidiMarks(ocrText));

        var invoiceNumber = InvoiceNumber().Match(text) is { Success: true } m
            ? m.Groups[1].Value.TrimEnd('.', ',')
            : null;

        var lines = new List<ScannedLine>();
        var supplierGuess = string.Empty;

        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var row = raw.Trim();

            if (row.Length == 0)
                continue;

            if (supplierGuess.Length == 0 && TryReadSupplier(row, out var supplier))
            {
                supplierGuess = supplier;
                continue;
            }

            if (InvoiceNumber().IsMatch(row))
                continue;

            var line = TryParseLine(row);
            if (line is not null)
                lines.Add(line);
        }

        return new ScannedInvoice(text, invoiceNumber, NullIfEmpty(supplierGuess), lines);
    }

    private static ScannedLine? TryParseLine(string row)
    {
        var numbers = ExtractNumbers(row);

        if (numbers.Count == 0)
            return null;

        // The name is whatever is left once the numbers are taken out.
        var name = NumberToken().Replace(row, " ");
        name = CleanName(name);

        if (name.Length == 0)
            return null;

        // Fewer than two numbers is a name plus one figure, which is too ambiguous to act on.
        if (numbers.Count < 2)
            return null;

        // An invoice table always ends with qty, price, total. Numbers earlier than that belong to
        // the product name ("لبن جهينة 1 لتر" has a 1 in it), so the LAST three are the figures.
        return numbers.Count >= 3
            ? new ScannedLine(row, name, numbers[^3], numbers[^2], numbers[^1])
            : new ScannedLine(row, name, numbers[0], null, numbers[1]);
    }

    private static bool TryReadSupplier(string row, out string supplier)
    {
        supplier = string.Empty;

        foreach (var marker in SupplierMarkers)
        {
            var at = row.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                continue;

            var value = row[(at + marker.Length)..].Trim(' ', ':', '-');
            if (value.Length >= 2)
            {
                supplier = CleanName(value);
                return true;
            }
        }

        return false;
    }

    /// <summary>Money-like tokens on a row, in reading order.</summary>
    private static List<decimal> ExtractNumbers(string row)
    {
        var found = new List<decimal>();

        foreach (Match match in NumberToken().Matches(row))
        {
            var token = match.Value.Replace(" ", string.Empty).Trim();

            // OCR likes to glue the thousands separator into the value: "1,1520" or "1.520.00".
            if (token.Count(c => c is '.' or ',' or '٫' or '٬') > 1)
            {
                // Keep only the last separator group, which is the decimals.
                var lastDot = token.LastIndexOfAny(['.', ',', '٫', '٬']);
                var tail = token[lastDot..].TrimStart('.', ',', '٫', '٬');
                token = token[..lastDot].Replace(".", string.Empty).Replace(",", string.Empty) + "." + tail;
            }

            token = token.Replace(',', '.').Replace('٫', '.').Replace('٬', '.');

            if (decimal.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                && value > 0m)
            {
                found.Add(value);
            }
        }

        return found;
    }

    /// <summary>
    /// Folds the Arabic orthographic variants OCR and humans disagree about, so two spellings of
    /// the same word compare equal: أ/إ/آ -> ا, ى -> ي, ة -> ه, and no diacritics.
    /// </summary>
    public static string NormaliseForCompare(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var s = value.Trim().ToLowerInvariant();

        s = s.Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا')
             .Replace('ى', 'ي').Replace('ة', 'ه')
             .Replace('ؤ', 'و').Replace('ئ', 'ي');

        // Strip tatweel and harakat.
        s = s.Replace("ـ", string.Empty, StringComparison.Ordinal);

        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is >= 'ً' and <= 'ٰ' or 'ٟ' and 'ۖ')
                continue;
            sb.Append(c);
        }

        // Collapse whitespace and drop anything that is not a letter or digit.
        var cleaned = new System.Text.StringBuilder(sb.Length);
        foreach (var c in sb.ToString())
        {
            if (char.IsLetterOrDigit(c))
                cleaned.Append(c);
            else if (cleaned.Length > 0 && cleaned[^1] != ' ')
                cleaned.Append(' ');
        }

        return cleaned.ToString().Trim();
    }

    private static string CleanName(string value)
    {
        var cleaned = NumberToken().Replace(value, " ").Trim();

        foreach (var word in SkipWords)
        {
            if (NormaliseForCompare(cleaned).Equals(NormaliseForCompare(word), StringComparison.Ordinal))
                return string.Empty;
        }

        return Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
    }

    /// <summary>
    /// Strips the invisible bidi control characters OCR wraps around Arabic runs (LRM, RLM, ALM,
    /// and the marks Word/Excel leave behind). They are invisible on screen but break every
    /// substring match, so they come off first.
    /// </summary>
    private static string StripBidiMarks(string text)
    {
        Span<char> marks = ['\u200E', '\u200F', '\u061C', '\u200B', '\u200C', '\u200D', '\uFEFF'];

        if (!text.AsSpan().ContainsAny(marks))
            return text;

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!marks.Contains(c))
                sb.Append(c);
        }

        return sb.ToString();
    }

    private static string NormaliseDigits(string text)
    {
        var chars = text.ToCharArray();

        for (var i = 0; i < chars.Length; i++)
        {
            var at = Array.IndexOf(ArabicIndic, chars[i]);
            if (at < 0)
                at = Array.IndexOf(ArabicIndicExtended, chars[i]);

            if (at >= 0)
                chars[i] = (char)('0' + at);
        }

        return new string(chars);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}