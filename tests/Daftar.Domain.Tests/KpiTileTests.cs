using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Daftar.Views.Components;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// One box for the headline numbers, whatever the screen.
///
/// <para>
/// It was written out twenty times across four screens in three different looks — the wrapper was
/// <c>card</c> in one, <c>surface</c> in the next and <c>panel</c> in the third, with three
/// paddings and two label styles. So the same figure did not look like the same figure depending
/// on where it was read, and restyling the number meant finding twenty places.
/// </para>
///
/// <para>
/// Two of the three shapes are two-line and one is three-line, so the optional parts are the risk:
/// a collapsed icon that still takes space, or a note that shows an empty line.
/// </para>
/// </summary>
public sealed class KpiTileTests
{
    /// <summary>
    /// Built on the UI thread: Avalonia's service locator is per-thread, and a control built on the
    /// xUnit pool thread cannot resolve a cursor factory.
    /// </summary>
    private static void Show(System.Func<KpiTile> build, System.Action<KpiTile> check)
    {
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            window.Width = 900;
            window.Height = 600;

            var tile = build();
            window.Content = tile;
            window.Show();
            AvaloniaUi.Settle();

            check(tile);
        });
    }

    private static TextBlock[] Texts(KpiTile tile) =>
        tile.GetVisualDescendants().OfType<TextBlock>().ToArray();

    /// <summary>
    /// The slot the note goes in. Reached through the card's own Border because the framework
    /// puts ContentControls of its own in the tree too, and picking the wrong one would make this
    /// test pass whatever the note did.
    /// </summary>
    private static ContentControl NoteHost(KpiTile tile) =>
        tile.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("panel"))
            .GetVisualDescendants().OfType<ContentControl>().Single();

    [Fact]
    public void The_Label_And_The_Figure_Both_Show()
    {
        Show(() => new KpiTile { Label = "إجمالي المبيعات", Value = "12,400 ج.م" }, tile =>
        {
            var texts = Texts(tile).Select(t => t.Text).ToArray();

            Assert.Contains("إجمالي المبيعات", texts);
            Assert.Contains("12,400 ج.م", texts);
        });
    }

    [Fact]
    public void The_Figure_Is_Bold_And_The_Label_Is_Not()
    {
        // A number you have to read carefully is a number you misread. This is the part of the
        // look that every one of the twenty copies had to get right by hand.
        Show(() => new KpiTile { Label = "إجمالي المبيعات", Value = "12,400" }, tile =>
        {
            var value = Texts(tile).Single(t => t.Text == "12,400");
            var label = Texts(tile).Single(t => t.Text == "إجمالي المبيعات");

            Assert.Equal(Avalonia.Media.FontWeight.Bold, value.FontWeight);
            Assert.NotEqual(Avalonia.Media.FontWeight.Bold, label.FontWeight);
        });
    }

    [Fact]
    public void An_Optional_Icon_Collapses_Rather_Than_Reserving_Space()
    {
        // An invisible TextBlock still reserves its line in the StackPanel, so a screen that does
        // not use icons would show a gap above every figure.
        Show(() => new KpiTile { Label = "المبيعات", Value = "10" }, tile =>
        {
            // The label and the figure both carry text, so the only empty one left is the icon.
            var icon = Texts(tile).Single(t => string.IsNullOrEmpty(t.Text));
            Assert.False(icon.IsVisible);
        });
    }

    [Fact]
    public void An_Icon_Shows_When_Given()
    {
        Show(() => new KpiTile { Label = "المبيعات", Icon = "📊", Value = "10" }, tile =>
        {
            var icon = Texts(tile).Single(t => t.Text == "📊");
            Assert.True(icon.IsVisible);
        });
    }

    [Fact]
    public void An_Optional_Note_Collapses_Rather_Than_Leaving_An_Empty_Line()
    {
        Show(() => new KpiTile { Label = "المبيعات", Value = "10" }, tile =>
        {
            Assert.False(NoteHost(tile).IsVisible);
        });

        Show(() => new KpiTile { Label = "المبيعات", Value = "10", Detail = "٤ فواتير" }, tile =>
        {
            var note = NoteHost(tile);
            Assert.True(note.IsVisible);
            Assert.Equal("٤ فواتير", Texts(tile).Single(t => t.Text == "٤ فواتير").Text);
        });
    }

    [Fact]
    public void The_Accent_Colour_Reaches_The_Figure_And_Only_The_Figure()
    {
        // Green on a gain, red on a loss. Applied to the label it would tint the caption instead of
        // the money, which is the whole point of having it.
        Show(() => new KpiTile
             {
                 Label = "صافي الربح",
                 Value = "1,200",
                 ValueBrush = new SolidColorBrush(Color.Parse("#107C41")),
             },
             tile =>
             {
                 var texts = Texts(tile);
                 var value = texts.Single(t => t.Text == "1,200");
                 var label = texts.Single(t => t.Text == "صافي الربح");

                 Assert.Equal(Color.Parse("#107C41"), ((ISolidColorBrush)value.Foreground!).Color);
                 Assert.NotEqual(Color.Parse("#107C41"), ((ISolidColorBrush)label.Foreground!).Color);
             });
    }

    [Fact]
    public void With_No_Accent_The_Figure_Takes_The_Ordinary_Text_Colour()
    {
        // A figure is not good news or bad news by being a figure.
        Show(() => new KpiTile { Label = "المبيعات", Value = "10" }, tile =>
        {
            var value = Texts(tile).Single(t => t.Text == "10");
            Assert.False(value.Foreground is SolidColorBrush b && b.Color == Color.Parse("#107C41"));
        });
    }
}