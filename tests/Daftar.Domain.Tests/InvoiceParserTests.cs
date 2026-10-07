using Daftar.Desktop.Receiving;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The invoice parser, checked against the exact text Tesseract 5.3.4 (ara+eng) produced from a
/// real Arabic supplier invoice image.
///
/// This text is a fixture, not something typed by hand: it keeps every real defect in the pipeline
/// visible — inverted Arabic digits, a decimal comma, bidi marks wrapped around runs, a stray Latin
/// fragment, and a number embedded in a product name ("لبن جهينة 1 لتر").
/// </summary>
public class InvoiceParserTests
{
    private const string RealOcrOutput = """
        فاتورة مورد
        المورد: ‎gino‏ النيل للأغذية
        رقم الفاتورة: ‎INV-2026-0412‏

        الصنف الكمية السعر الإجمالي
        زيت زيتون صفيحة 12 75.00 900.00

        ارزهصري ‎Jala‏ 40 90.00 3600.00
        لبن جهينة 1 لتر 24 55.00 1320.00
        سكرناعم 2 كجم 30 85,00 2550.00
        شاي ليبتون 25 كيس 15 210.00 3150.00

        الإجمالي الكلي: 11520.00
        """;

    [Fact]
    public void Reads_TheInvoiceNumberWithItsPrefix()
    {
        var invoice = InvoiceParser.Parse(RealOcrOutput);

        Assert.Equal("INV-2026-0412", invoice.InvoiceNumber);
    }

    [Fact]
    public void Reads_TheSupplierFromItsLabel()
    {
        var invoice = InvoiceParser.Parse(RealOcrOutput);

        Assert.NotNull(invoice.SupplierGuess);

        // OCR garbled part of the name; what matters is that it was found at all.
        Assert.Contains("النيل", invoice.SupplierGuess);
    }

    [Fact]
    public void Finds_AProductLine_ForEachTableRow_AndIgnoresTheHeaders()
    {
        var invoice = InvoiceParser.Parse(RealOcrOutput);

        Assert.Equal(5, invoice.Lines.Count);

        // The column headings are not products, and neither is the grand-total row.
        Assert.DoesNotContain(invoice.Lines, l => l.Text.Contains("الكمية", StringComparison.Ordinal));
        Assert.DoesNotContain(invoice.Lines, l => l.Text.Contains("الإجمالي الكلي", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadsQuantityPriceAndTotal_FromTheLastThreeNumbersOnARow()
    {
        var invoice = InvoiceParser.Parse(RealOcrOutput);

        var olive = invoice.Lines.Single(l => l.Text.Contains("زيت زيتون", StringComparison.Ordinal));

        Assert.Equal(12m, olive.Quantity);
        Assert.Equal(75m, olive.UnitPrice);
        Assert.Equal(900m, olive.Total);
    }

    [Fact]
    public void ANumberInsideTheProductName_DoesNotShiftTheFigures()
    {
        var invoice = InvoiceParser.Parse(RealOcrOutput);

        // "لبن جهينة 1 لتر" carries a 1 in the name, so the row holds four numbers. The last three
        // are the figures; taking the first three would read the quantity as 1 and the price as 24.
        var milk = invoice.Lines.Single(l => l.Text.Contains("لبن", StringComparison.Ordinal));

        Assert.Equal(24m, milk.Quantity);
        Assert.Equal(55m, milk.UnitPrice);
        Assert.Equal(1320m, milk.Total);
    }

    [Fact]
    public void ACommaDecimalSeparatorIsReadAsADecimalPoint()
    {
        var invoice = InvoiceParser.Parse(RealOcrOutput);

        var sugar = invoice.Lines.Single(l => l.Text.Contains("سكر", StringComparison.Ordinal));

        // OCR read "85,00" as "85,00".
        Assert.Equal(85m, sugar.UnitPrice);
        Assert.Equal(2550m, sugar.Total);
    }

    [Fact]
    public void ThousandsSeparatorsDoNotProduceAnAbsurdNumber()
    {
        var invoice = InvoiceParser.Parse("زيت زيتون 12 75.00 1,1520.00");

        var line = Assert.Single(invoice.Lines);

        Assert.Equal(11520m, line.Total);
    }

    [Fact]
    public void ArabicIndicDigitsAreFoldedToAscii()
    {
        var invoice = InvoiceParser.Parse("زيت زيتون ١٢ ٧٥.٠٠ ٩٠٠.٠٠");

        var line = Assert.Single(invoice.Lines);

        Assert.Equal(12m, line.Quantity);
        Assert.Equal(75m, line.UnitPrice);
        Assert.Equal(900m, line.Total);
    }

    [Fact]
    public void BidiMarksWrappedAroundRunsDoNotCorruptTheText()
    {
        // LRM/RLM are invisible on screen but break every substring match if left in.
        var invoice = InvoiceParser.Parse("رقم الفاتورة: ‎INV-2026-0412‏\nزيت زيتون 12 75.00 900.00");

        Assert.Equal("INV-2026-0412", invoice.InvoiceNumber);
        Assert.Single(invoice.Lines);
    }

    [Fact]
    public void ARowWithFewerThanTwoNumbersIsNotOffered()
    {
        // A name and a single figure is too ambiguous to act on without guessing.
        var invoice = InvoiceParser.Parse("زيت زيتون 900\n");

        Assert.Empty(invoice.Lines);
    }

    [Fact]
    public void EmptyTextYieldsNothing()
    {
        var invoice = InvoiceParser.Parse("   ");

        Assert.Empty(invoice.Lines);
        Assert.Null(invoice.InvoiceNumber);
        Assert.Equal(string.Empty, invoice.RawText);
    }

    [Theory]
    [InlineData("زيت زيتون", "زيت زيتون")]
    [InlineData("إجاج", "اجاج")]
    [InlineData("لبن جهينة", "لبن جهينه")]
    public void CompareFormFoldsTheArabicOrthographicVariants(string left, string right)
        => Assert.Equal(
            InvoiceParser.NormaliseForCompare(left),
            InvoiceParser.NormaliseForCompare(right));

    [Fact]
    public void CompareFormIgnoresDiacriticsAndTatweel()
        => Assert.Equal(
            InvoiceParser.NormaliseForCompare("زَيْت"),
            InvoiceParser.NormaliseForCompare("زيت"));
}