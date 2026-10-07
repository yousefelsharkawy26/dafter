using Daftar.Desktop.Receiving;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// Matching a garbled scanned name back to the catalogue. The matcher only ever proposes: a wrong
/// high-confidence guess would be worse than none, so these tests pin down the cases where it must
/// stay silent as much as the ones where it should fire.
/// </summary>
public class ProductMatcherTests
{
    private static readonly (int Id, string Sku, string Name)[] Catalogue =
    [
        (1, "OLIVE-1", "زيت زيتون صفيحة"),
        (2, "RICE-1", "ارز مصري فاخر"),
        (3, "MILK-1", "لبن جهينة 1 لتر"),
        (4, "SUGAR-1", "سكر ناعم 2 كجم"),
        (5, "TEA-1", "شاي ليبتون 25 كيس"),
        (6, "SUN-OIL", "زيت ذرة صنيون"),
        (7, "FULL-MILK", "لبن كامل الدسم"),
    ];

    private static ProductMatch? Best(string scanned)
        => ProductMatcher.Rank(scanned, Catalogue).FirstOrDefault();

    [Fact]
    public void AnExactNameScoresHighest()
    {
        var match = Best("زيت زيتون صفيحة");

        Assert.NotNull(match);
        Assert.Equal(1, match.ProductId);
        Assert.True(match.Score >= 90);
    }

    [Fact]
    public void AnOcrDroppedWordStillMatchesTheRightProduct()
    {
        // OCR dropped the "1" from "لبن جهينة 1 لتر" and rendered it "لبن جهينة لتر".
        var match = Best("لبن جهينة لتر");

        Assert.NotNull(match);
        Assert.Equal(3, match.ProductId);
    }

    [Fact]
    public void WordsRunTogetherAreStillMatched()
    {
        // OCR lost the space in "سكر ناعم".
        var match = Best("سكرناعم كجم");

        Assert.NotNull(match);
        Assert.Equal(4, match.ProductId);
    }

    [Fact]
    public void ASkuPrintedOnTheInvoiceWinsOutright()
    {
        // The Arabic name is missing entirely; only the code on the invoice identifies it.
        var match = ProductMatcher.Rank("MILK-1 24 55.00", Catalogue).First();

        Assert.Equal(3, match.ProductId);
        Assert.Equal(100, match.Score);
    }

    [Fact]
    public void TwoSimilarProductsAreRankedWithTheBetterOneFirst()
    {
        var ranked = ProductMatcher.Rank("لبن جهينة لتر", Catalogue);

        Assert.Equal(3, ranked[0].ProductId);      // لبن جهينة
        Assert.True(ranked[0].Score > ranked[1].Score);
    }

    [Fact]
    public void TextThatSharesNothingWithTheCatalogueYieldsNoSuggestion()
    {
        // Nothing resembling "warkash halaw" exists, and guessing would be worse than silence.
        Assert.Empty(ProductMatcher.Rank("ورقاش حلاوة", Catalogue));
    }

    [Fact]
    public void AnEmptyScanYieldsNoSuggestion()
        => Assert.Empty(ProductMatcher.Rank("   ", Catalogue));

    [Fact]
    public void TheResultListIsCapped()
        => Assert.True(ProductMatcher.Rank("زيت", Catalogue, limit: 2).Count <= 2);
}