using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Daftar.Views.Sessions;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The number inside a stepper dot sits in the middle of the dot.
/// </summary>
///
/// <para>
/// The dot's TextBlock was left to stretch, and a stretched TextBlock begins its text at the leading
/// edge and the top — which in this right-to-left layout parked the digit against the top right of
/// the disc, half over its edge, on every step. All five dots were alike, so the eye read it as the
/// font rather than as the layout.
/// </para>
///
/// <para>
/// The rendered pixels are measured, not the controls. A stretched TextBlock's <c>Bounds</c> fills
/// the disc, so its box centres on the dot whether the glyph does or not: an assertion on bounds
/// passes on the broken layout as readily as on the fixed one, which was tried and did exactly that.
/// The glyph's ink is what moved, so the ink is what is measured.
/// </para>
public sealed class WizardStepperTests
{
    /// <summary>
    /// How far the digit's ink may sit from the middle of the disc. The glyphs do not centre to the
    /// pixel — the line box keeps room for ascenders and descenders that these digits do not use —
    /// so a pixel or so is normal and three is the brokenness worth catching: dozens of pixels out.
    /// </summary>
    private const double Tolerance = 3.0;

    /// <summary>
    /// The darkest and lightest a pixel can be and still count as part of the digit. Anything
    /// closer to the disc's own colour than this is treated as background, which keeps the
    /// anti-aliased fringe out of the measurement.
    /// </summary>
    private const double InkDistance = 0.15;

    /// <summary>
    /// Fewer ink pixels than this and the digit was not drawn at all. Half the smallest count a real
    /// "1" produces, so it guards against a blank disc without tripping over font metrics.
    /// </summary>
    private const int LeastInk = 6;

    [Fact]
    public void The_Number_Inside_A_Step_Dot_Sits_In_The_Middle_Of_The_Dot()
    {
        var measured = new List<string>();
        var offsets = new List<(double Across, double Down)>();

        AvaloniaUi.Run(window =>
        {
            var wizard = new DistributorWizard();

            window.Content = wizard;
            window.Show();
            AvaloniaUi.Settle();

            var size = new PixelSize(
                (int)Math.Ceiling(wizard.Bounds.Width),
                (int)Math.Ceiling(wizard.Bounds.Height));

            using var shot = new RenderTargetBitmap(size);
            shot.Render(wizard);

            var frame = ReadPixels(shot, size);

            foreach (var dot in wizard.GetLogicalDescendants().OfType<Border>()
                         .Where(border => border.Classes.Contains("stepDot")))
            {
                var digit = dot.GetLogicalChildren().OfType<TextBlock>().First();
                var corner = dot.TranslatePoint(default, wizard) ?? default;

                var middle = new Point(
                    corner.X + (dot.Bounds.Width / 2),
                    corner.Y + (dot.Bounds.Height / 2));

                // Inset from the rim: the ring around the disc is a different colour from the disc
                // and is symmetrical, so it would drag the centre of mass back to the middle and
                // hide the very offset being looked for.
                var radius = (Math.Min(dot.Bounds.Width, dot.Bounds.Height) / 2) - 3;

                var ink = Ink(frame, size.Width, middle, radius);

                measured.Add(
                    $"{digit.Text}: disc {dot.Bounds.Width:0}px, ink {ink.Count}px,"
                    + $" at ({ink.Across - middle.X:+0.0;-0.0;0.0}, {ink.Down - middle.Y:+0.0;-0.0;0.0})");

                if (ink.Count >= LeastInk)
                    offsets.Add((ink.Across - middle.X, ink.Down - middle.Y));
            }
        });

        Assert.Equal(5, offsets.Count);

        for (var step = 0; step < offsets.Count; step++)
        {
            var (across, down) = offsets[step];

            Assert.True(
                Math.Abs(across) <= Tolerance && Math.Abs(down) <= Tolerance,
                $"digit {step + 1} is off centre — {measured[step]}");
        }
    }

    private static byte[] ReadPixels(RenderTargetBitmap bitmap, PixelSize size)
    {
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            bitmap.CopyPixels(new PixelRect(size), pinned.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            pinned.Free();
        }

        return buffer;
    }

    /// <summary>
    /// The centre of mass of the digit's ink inside a disc, in bitmap coordinates.
    ///
    /// <para>
    /// The disc's own colour is taken to be the middle pixel of the sample rather than any fixed
    /// value, because the digit is the minority of the area in every kind of dot: dark on a pale
    /// disc for the current and waiting steps, pale on the accent disc for the finished ones.
    /// </para>
    /// </summary>
    private static (int Count, double Across, double Down) Ink(
        byte[] frame, int width, Point middle, double radius)
    {
        var samples = new List<(int X, int Y, double Luminance)>();

        for (var y = (int)Math.Ceiling(middle.Y - radius); y <= (int)(middle.Y + radius); y++)
        {
            for (var x = (int)Math.Ceiling(middle.X - radius); x <= (int)(middle.X + radius); x++)
            {
                if (x < 0 || y < 0 || x >= width || (y * width * 4) + 3 >= frame.Length)
                    continue;

                if (Math.Pow(x + 0.5 - middle.X, 2) + Math.Pow(y + 0.5 - middle.Y, 2) > radius * radius)
                    continue;

                var at = ((y * width) + x) * 4;

                // Bgra8888: blue, green, red, alpha.
                var luminance =
                    ((0.2126 * frame[at + 2]) + (0.7152 * frame[at + 1]) + (0.0722 * frame[at])) / 255.0;

                samples.Add((x, y, luminance));
            }
        }

        var background = samples.Select(one => one.Luminance).Order().ElementAt(samples.Count / 2);

        var ink = samples.Where(one => Math.Abs(one.Luminance - background) > InkDistance).ToList();

        if (ink.Count == 0)
            return (0, middle.X, middle.Y);

        return (
            ink.Count,
            ink.Average(one => one.X + 0.5),
            ink.Average(one => one.Y + 0.5));
    }
}
