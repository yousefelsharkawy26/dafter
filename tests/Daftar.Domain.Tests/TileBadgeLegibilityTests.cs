using Avalonia.Controls;
using Avalonia.Media;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// A badge on a product tile is readable.
/// </summary>
///
/// <para>
/// The badge is the one thing on the tile that is <em>not</em> white: a pale caution pill saying
/// "مخزون منخفض" sits on the Start blue. It came out invisible — cream text on a cream pill, a
/// measured contrast of 1.04 — while every other line on the tile was perfectly legible, so a
/// screenshot of the screen as a whole did not show it. Only cropping in and asking did.
/// </para>
///
/// <para>
/// Cause: <c>Button.tile TextBlock</c> whitens every text run inside a tile, and Avalonia resolves
/// competing setters by source order rather than by specificity. The badge rules were written
/// first, so the catch-all overtook them and the badge inherited white.
/// </para>
///
/// <para>
/// This asserts the rendered colours rather than the stylesheet, because the stylesheet is exactly
/// what was wrong. Every badge kind is covered: they share the broken ordering, and there is no
/// reason to wait for a screenshot to find the next one.
/// </para>
/// </summary>
public sealed class TileBadgeLegibilityTests
{
    /// <summary>
    /// Every badge a tile can show, with the caption it carries.
    /// </summary>
    public static TheoryData<string> BadgeKinds => new() { "warning", "danger", "success", "info", "count" };

    private static double Luminance(Color c) =>
        (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>
    /// A colour-pair contrast ratio, the WCAG definition. 1.0 is invisible; 4.5 is the AA floor
    /// for body text.
    /// </summary>
    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);

        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Theory]
    [MemberData(nameof(BadgeKinds))]
    public void A_Badge_On_A_Tile_Is_Legible_Whatever_It_Says(string kind)
    {
        AvaloniaUi.Run(window =>
        {
            var tile = new Button { Classes = { "tile" } };
            var badge = new Border { Classes = { "badge", kind } };
            var text = new TextBlock { Text = "مخزون منخفض" };

            badge.Child = text;
            tile.Content = badge;
            window.Content = tile;
            AvaloniaUi.Settle();

            var foreground = (text.Foreground as ISolidColorBrush)?.Color;
            var background = (badge.Background as ISolidColorBrush)?.Color;

            // Both must actually have resolved. An unresolved DynamicResource yields a null brush,
            // and a null brush renders as the inherited one, which is how a style that reads as
            // correct ends up producing white on cream.
            Assert.NotNull(foreground);
            Assert.NotNull(background);

            var ratio = Contrast(foreground!.Value, background!.Value);

            Assert.True(
                ratio >= 4.5,
                $"{kind} badge: {foreground} on {background} has contrast {ratio:F2},"
                + " below the 4.5 floor");
        });
    }
}