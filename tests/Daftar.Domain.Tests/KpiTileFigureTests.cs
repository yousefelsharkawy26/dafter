using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Daftar.Views.Components;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The figure on a KPI tile is painted, and wears the brush it is given when it is given one.
/// </summary>
///
/// <para>
/// The tile reads its brush like this: <c>Foreground="<![CDATA[{Binding ValueBrush}]]>"</c>. A null
/// brush is still a value, so the binding set the property to null, which painted nothing at all and
/// removed the number from every tile that was neither a gain nor a loss — the sessions wizard's
/// invoice count and sales total among them. The comment on the property promised the opposite
/// (<em>"left null the value takes the ordinary text colour"</em>), and the tiles either side of the
/// blank ones, which do pass a brush, looked fine, so a glance at the screen would not have found it.
/// </para>
///
/// <para>
/// The brush is applied in the code-behind now, falling back to the theme's own ink. These assert
/// the rendered brush rather than the markup, because the markup is what was wrong: put the inline
/// binding back and the first test fails, stop applying the given brush and the second one does,
/// drop the fallback and the third one does.
/// </para>
public sealed class KpiTileFigureTests
{
    /// <summary>
    /// The window base the tiles sit on. The panel fill is partly transparent, so the figure's
    /// effective colour is the figure over the panel over this.
    /// </summary>
    private static readonly Color PageBase = Color.FromRgb(0xF3, 0xF3, 0xF3);

    /// <summary>
    /// WCAG relative luminance: the sRGB channels are linearised before they are weighted. The flat
    /// 0.2126R+0.7152G+0.0722B shortcut taken straight off the sRGB values rates mid-tone accents far
    /// too generously — it scores the accent blue at 2.3, where the real figure is 4.35.
    /// </summary>
    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static Color Over(Color foreground, Color background)
    {
        var alpha = foreground.A / 255.0;

        return Color.FromRgb(
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    /// <summary>
    /// A colour-pair contrast ratio, the WCAG definition. 1.0 is invisible; 4.5 is the AA floor.
    /// </summary>
    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);

        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>One rendered tile, read on the UI thread and handed back as plain values.</summary>
    private sealed record Rendered(string? Text, Color? Ink, Color? Fill, double Size, FontWeight Weight);

    /// <summary>
    /// Renders one tile and hands back the things that matter about its figure. They are read on the
    /// UI thread and returned as values, because the properties belong to that thread.
    ///
    /// <para>
    /// The window is shown rather than merely filled. An unshown window has no visual tree at all,
    /// and without one the tile's <c>RelativeSource</c> bindings resolve to nothing — the figure read
    /// back empty and every colour assertion still passed. A test that cannot fail proves nothing.
    /// </para>
    /// </summary>
    private static Rendered Render(string? brush)
    {
        string? text = null;
        Color? ink = null;
        Color? fill = null;
        var size = 0.0;
        var weight = FontWeight.Normal;

        AvaloniaUi.Run(window =>
        {
            var tile = new KpiTile { Label = "عدد الفواتير", Value = "1" };

            if (brush is not null)
                tile.ValueBrush = Brush.Parse(brush);

            window.Content = tile;
            window.Show();
            AvaloniaUi.Settle();

            var descendants = tile.GetLogicalDescendants().ToList();

            var figure = descendants.OfType<TextBlock>().First(t => t.Name == "ValueText");

            text = figure.Text;
            ink = (figure.Foreground as ISolidColorBrush)?.Color;
            size = figure.FontSize;
            weight = figure.FontWeight;

            fill = (descendants.OfType<Border>()
                    .First(b => b.Classes.Contains("panel"))
                    .Background as ISolidColorBrush)?.Color;
        });

        return new Rendered(text, ink, fill, size, weight);
    }

    [Fact]
    public void A_Figure_With_No_Brush_Still_Takes_The_Ordinary_Ink()
    {
        var rendered = Render(brush: null);

        // It has to be there before it can be painted: an empty figure is the failure this guards.
        Assert.Equal("1", rendered.Text);

        // Both brushes must actually have resolved: an unresolved brush is null, and null is exactly
        // what made the figure disappear rather than fall back to anything.
        Assert.NotNull(rendered.Ink);
        Assert.NotNull(rendered.Fill);

        Assert.True(rendered.Ink!.Value.A > 128, $"the figure's ink is barely there: {rendered.Ink.Value}");

        var background = Over(rendered.Fill!.Value, PageBase);
        var foreground = Over(rendered.Ink.Value, background);
        var ratio = Contrast(foreground, background);

        Assert.True(
            ratio >= 4.5,
            $"an unbrushed figure is {foreground} on {background}, contrast {ratio:F2},"
            + " below the 4.5 floor");
    }

    [Fact]
    public void A_Figure_Given_A_Brush_Wears_That_Brush()
    {
        var rendered = Render(brush: "#0F7B0F");

        Assert.Equal("1", rendered.Text);
        Assert.NotNull(rendered.Ink);
        Assert.Equal(Color.FromRgb(0x0F, 0x7B, 0x0F), rendered.Ink!.Value);
    }

    /// <summary>
    /// The figure's own size decides which contrast floor it owes. At 16 DIP bold it is ordinary
    /// text and owes 4.5 — and the four colours the sessions and reports pages hand it (the accent
    /// blue <c>#0078D7</c>/<c>#0078D4</c>, the commission purple <c>#8764B8</c>, the cash-difference
    /// orange <c>#D24726</c>) measure 4.35 to 4.46 on the panel, so every one of them failed. At
    /// <c>FontSizeTitle</c> — 20 DIP bold — the figure is WCAG "large text", owes 3.0, and all four
    /// clear it: the number got the size a KPI figure should have had, and no screen was repainted.
    ///
    /// <para>
    /// The test reads the size back off the rendered figure and derives the floor from it, so the two
    /// cannot drift apart. Put the size back to 16 and the four failing colours fail again.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("#0078D7")] // الجولات المفتوحة, إجمالي المبيعات
    [InlineData("#0078D4")] // إجمالي التحصيلات, المتأخرات (the reports page)
    [InlineData("#8764B8")] // إجمالي العمولات
    [InlineData("#D24726")] // صافي فروق الكاش
    [InlineData("#107C41")] // إجمالي التحصيلات (green)
    [InlineData("#D13438")] // المتأخرات المستحقة (red)
    public void A_Figure_Clears_The_Floor_Its_Own_Size_Owes(string brush)
    {
        var rendered = Render(brush);

        Assert.NotNull(rendered.Ink);
        Assert.NotNull(rendered.Fill);

        // WCAG 1.4.3: 24 DIP, or 18.66 DIP bold and up, is where the floor drops from 4.5 to 3.0.
        var large = rendered.Size >= 24.0
                 || (rendered.Size >= 18.66 && rendered.Weight >= FontWeight.Bold);

        var floor = large ? 3.0 : 4.5;

        var background = Over(rendered.Fill!.Value, PageBase);
        var foreground = Over(rendered.Ink!.Value, background);
        var ratio = Contrast(foreground, background);

        Assert.True(
            ratio >= floor,
            $"{brush} renders {foreground} on {background}: contrast {ratio:F2}, and a figure of"
            + $" {rendered.Size} DIP / {rendered.Weight} owes {floor:F1}");
    }

    [Fact]
    public void A_Figure_Whose_Brush_Is_Taken_Away_Goes_Back_To_The_Ordinary_Ink()
    {
        Color? given = null;
        Color? withdrawn = null;

        AvaloniaUi.Run(window =>
        {
            var tile = new KpiTile
            {
                Label = "عدد الفواتير",
                Value = "1",
                ValueBrush = Brush.Parse("#0F7B0F"),
            };

            window.Content = tile;
            window.Show();
            AvaloniaUi.Settle();

            var figure = tile.GetLogicalDescendants().OfType<TextBlock>()
                .First(t => t.Name == "ValueText");

            given = (figure.Foreground as ISolidColorBrush)?.Color;

            // The same tile, reused without a brush — a grid of them would do this on every refresh.
            tile.ValueBrush = null;
            AvaloniaUi.Settle();

            withdrawn = (figure.Foreground as ISolidColorBrush)?.Color;
        });

        Assert.Equal(Color.FromRgb(0x0F, 0x7B, 0x0F), given);
        Assert.NotNull(withdrawn);
        Assert.NotEqual(given, withdrawn);
        Assert.True(withdrawn!.Value.A > 128, $"the figure kept a ghost of its old ink: {withdrawn.Value}");
    }
}
