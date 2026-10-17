using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Daftar.Converters;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.ViewModels;
using Daftar.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// Arabic counts its nouns in ranges, and the noun moves with the number: exactly two takes the
/// dual, three to ten the plural, and zero, one and eleven and up leave it singular.
///
/// <para>
/// The application was written as though the singular covered every number. The session banner
/// reported "تم التحميل — 5 جولة، 1 موزع", which says five round, and the overdue figure on the
/// reports screen was seeded as "0 متأخرة" — a plural adjective with no noun left for it to
/// describe. Both are the same mistake, and it was made in about thirty places.
/// </para>
/// </summary>
public sealed class ArabicTextTests
{
    [Theory]
    [InlineData(0, "0 جولة")]
    [InlineData(1, "1 جولة")]
    [InlineData(2, "2 جولتان")]
    [InlineData(3, "3 جولات")]
    [InlineData(5, "5 جولات")]
    [InlineData(10, "10 جولات")]
    [InlineData(11, "11 جولة")]
    [InlineData(99, "99 جولة")]
    [InlineData(100, "100 جولة")]
    [InlineData(1000, "1000 جولة")]
    public void The_Noun_Takes_The_Form_Its_Number_Asks_For(int count, string expected)
        => Assert.Equal(expected, ArabicText.Count(count, "جولة", "جولتان", "جولات"));

    [Fact]
    public void The_Banner_That_Read_Five_Round_Now_Reads_Five_Rounds()
    {
        // The live evidence, from the van-load step: five rounds and one distributor were reported
        // as "5 جولة، 1 موزع" -- five rounds in the singular, one distributor in the singular, and
        // nothing in the phrase to tell the two cases apart.
        var rounds = ArabicText.Count(5, "جولة", "جولتان", "جولات");
        var distributors = ArabicText.Count(1, "موزع", "موزعان", "موزعون");

        Assert.Equal("5 جولات، 1 موزع", $"{rounds}، {distributors}");
    }

    /// <summary>
    /// The adjective agrees with the number, so the form passed in has to be the whole phrase and
    /// not the bare noun. With the noun alone, two invoices would read "2 فاتورتان متأخرة".
    ///
    /// <para>
    /// The zero row is the case the reports screen got wrong. It was seeded as "0 متأخرة", where
    /// the adjective survived the zero and the noun did not, so the tile described nothing at all
    /// until a number was put in front of it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0, "0 فاتورة متأخرة")]
    [InlineData(1, "1 فاتورة متأخرة")]
    [InlineData(2, "2 فاتورتان متأخرتان")]
    [InlineData(5, "5 فواتير متأخرة")]
    [InlineData(11, "11 فاتورة متأخرة")]
    public void The_Adjective_Travels_With_The_Noun_It_Describes(int count, string expected)
        => Assert.Equal(expected, ArabicText.Count(count, "فاتورة متأخرة", "فاتورتان متأخرتان", "فواتير متأخرة"));

    /// <summary>
    /// The word without the number, for the start screen's low-stock tile: the figure there is a
    /// text block of its own and the word sits beside it a size smaller.
    ///
    /// <para>
    /// Two is the count that tells the forms apart. Zero and one read "صنف" as well, so a word that
    /// never moved at all would still have passed at either of them — which is how the literal
    /// "أصناف" survived on a tile whose count is usually three or more.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0, "صنف")]
    [InlineData(1, "صنف")]
    [InlineData(2, "صنفان")]
    [InlineData(3, "أصناف")]
    [InlineData(10, "أصناف")]
    [InlineData(11, "صنف")]
    [InlineData(100, "صنف")]
    public void The_Word_Alone_Takes_The_Form_Its_Number_Asks_For(int count, string expected)
        => Assert.Equal(expected, ArabicText.Noun(count, "صنف", "صنفان", "أصناف"));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(11)]
    public void The_Phrase_Is_The_Word_With_The_Number_In_Front_Of_It(int count)
    {
        // One range with two callers. If these two ever disagree, one screen is showing a form the
        // other has already ruled out.
        Assert.Equal($"{count} {ArabicText.Noun(count, "صنف", "صنفان", "أصناف")}",
            ArabicText.Count(count, "صنف", "صنفان", "أصناف"));
    }
}

/// <summary>
/// The binding side of the same rule, for the screens that write the phrase straight into XAML.
///
/// <para>
/// Those screens named the noun in a <c>StringFormat</c>, where the noun is a piece of text and
/// cannot move with the number in front of it. The parameter carries the three forms separated by
/// <c>|</c> and the range is decided by <see cref="ArabicText"/> alone, so the two cannot drift
/// apart.
/// </para>
/// </summary>
public sealed class ArabicCountConverterTests
{
    private static readonly ArabicCountConverter Converter = new();

    private static object? Convert(object? value, object? parameter)
        => Converter.Convert(value, typeof(string), parameter, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(0, "0 صنف")]
    [InlineData(2, "2 صنفان")]
    [InlineData(3, "3 أصناف")]
    [InlineData(12, "12 صنف")]
    public void A_Count_Takes_The_Same_Form_The_View_Model_Would_Give_It(int count, string expected)
        => Assert.Equal(expected, Convert(count, "صنف|صنفان|أصناف"));

    [Fact]
    public void A_Count_That_Arrives_As_Text_Is_Still_Counted()
        => Assert.Equal("3 أصناف", Convert("3", "صنف|صنفان|أصناف"));

    /// <summary>
    /// A parameter that is not three forms leaves the number where it was. Guessing which form was
    /// dropped would put the wrong noun under the number, which is the mistake this converter
    /// exists to stop: the number on its own is plain but true.
    /// </summary>
    [Fact]
    public void A_Parameter_With_Two_Forms_Leaves_The_Number_Alone()
        => Assert.Equal("5", Convert(5, "صنف|أصناف"));

    [Fact]
    public void A_Parameter_With_An_Empty_Form_Leaves_The_Number_Alone()
        => Assert.Equal("5", Convert(5, "صنف||أصناف"));

    [Fact]
    public void A_Value_That_Is_Not_A_Number_Is_Left_As_It_Is()
        => Assert.Equal("غير معروف", Convert("غير معروف", "صنف|صنفان|أصناف"));

    [Fact]
    public void No_Value_And_No_Parameter_Are_Both_Survivable()
    {
        Assert.Equal(string.Empty, Convert(null, "صنف|صنفان|أصناف"));
        Assert.Equal("5", Convert(5, null));
    }
}

/// <summary>
/// The word on its own, for the tiles that print the number themselves. The parameter and the
/// failure behaviour are the phrase converter's, so only the two things that differ are asserted
/// here: the range still decides the word, and the number is not in the result.
/// </summary>
public sealed class ArabicNounConverterTests
{
    private static readonly ArabicNounConverter Converter = new();

    private static object? Convert(object? value, object? parameter)
        => Converter.Convert(value, typeof(string), parameter, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(0, "صنف")]
    [InlineData(1, "صنف")]
    [InlineData(2, "صنفان")]
    [InlineData(3, "أصناف")]
    [InlineData(10, "أصناف")]
    [InlineData(11, "صنف")]
    public void The_Word_Takes_The_Same_Form_The_View_Model_Would_Give_It(int count, string expected)
        => Assert.Equal(expected, Convert(count, "صنف|صنفان|أصناف"));

    [Fact]
    public void The_Number_Is_Not_Printed_Again()
    {
        // The tile prints the count itself, so a word that carried it would read "3 3 أصناف".
        foreach (var count in new[] { 0, 1, 2, 3, 10, 11 })
            Assert.DoesNotContain(
                count.ToString(CultureInfo.InvariantCulture),
                (string?)Convert(count, "صنف|صنفان|أصناف") ?? string.Empty);
    }

    [Fact]
    public void A_Parameter_With_Two_Forms_Leaves_The_Value_Alone()
        => Assert.Equal("5", Convert(5, "صنف|أصناف"));

    [Fact]
    public void A_Parameter_With_An_Empty_Form_Leaves_The_Value_Alone()
        => Assert.Equal("5", Convert(5, "صنف||أصناف"));

    [Fact]
    public void A_Value_That_Is_Not_A_Number_Is_Left_As_It_Is()
        => Assert.Equal("غير معروف", Convert("غير معروف", "صنف|صنفان|أصناف"));

    [Fact]
    public void No_Value_And_No_Parameter_Are_Both_Survivable()
    {
        Assert.Equal(string.Empty, Convert(null, "صنف|صنفان|أصناف"));
        Assert.Equal("5", Convert(5, null));
    }
}

/// <summary>
/// One screen proves the XAML route end to end: the key resolves out of the application's own
/// resources, the three forms parse out of the markup extension, and the number that reaches the
/// text block is the one that was set.
///
/// <para>
/// The bounds and the parameter failures are proved above without a window; what needed a window
/// was the binding itself, because the key is looked up while the tree is being laid out and a
/// typo in it is silent until something is rendered.
/// </para>
/// </summary>
public sealed class ArabicCountBindingTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"daftar-arabic-count-{Guid.NewGuid():N}.db");

    private ServiceProviderFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = await ServiceProviderFixture.CreateAsync(_dbPath);

        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        await DaftarDatabase.InitializeAsync(ctx, PasswordHasher.Hash("admin"));
    }

    public Task DisposeAsync()
    {
        _fixture.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_Converter_Is_In_The_Applications_Resources()
    {
        object? resource = null;

        AvaloniaUi.Run(_ => resource = Application.Current?.TryFindResource("ArabicCount", out var found) == true
            ? found
            : null);

        Assert.IsType<ArabicCountConverter>(resource);
    }

    [Fact]
    public void The_Noun_Converter_Is_In_The_Applications_Resources()
    {
        // The start screen's low-stock tile is the only place that binds the word on its own, and a
        // key that is not in the resources fails silently at run time, not at build time.
        object? resource = null;

        AvaloniaUi.Run(_ => resource = Application.Current?.TryFindResource("ArabicNoun", out var found) == true
            ? found
            : null);

        Assert.IsType<ArabicNounConverter>(resource);
    }

    [Fact]
    public void The_Returns_Count_On_Screen_Moves_With_Its_Number()
    {
        var rendered = new List<string>();

        AvaloniaUi.Run(window =>
        {
            var vm = new ReturnsViewModel(_fixture.Services);

            window.Content = new ReturnsView { DataContext = vm };
            window.Show();

            // The screen loads itself the moment it is handed a view model, and the load writes
            // this count. Two pumps let that load finish before the count is set, or the load
            // would settle it back to the value it read and the test would be asserting on that
            // instead of on the binding.
            AvaloniaUi.Settle();
            AvaloniaUi.Settle();

            vm.TodayReturnsCount = 5;
            AvaloniaUi.Settle();

            rendered.AddRange(window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(block => block.Text ?? string.Empty));
        });

        // Five returns, not "5 مرتجع": the singular after a five is the defect, and it is what a
        // StringFormat would have produced.
        Assert.Contains("5 مرتجعات", rendered);
    }
}
