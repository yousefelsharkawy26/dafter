using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Daftar.Infrastructure;
using Daftar.ViewModels;
using Daftar.Views.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// A customer is chosen by pressing the row, so the row is what the press has to reach -- and the
/// list must not paint a choice of its own that the wizard does not mean.
/// </summary>
///
/// <para>
/// A result row used to be a tall item holding a short button: the item's own padding and
/// alignment left a band 10 pixels deep above and below the button, and a press landing in it did
/// nothing at all -- four fifths of the row's height, around the part that acted. Measured, the
/// item was 896x57 holding a button 872x37. The row now fills its item, and the list's own
/// selection has no colour left to paint.
/// </para>
///
/// <para>
/// Both halves are asserted against the laid-out tree and a rendered frame rather than against the
/// XAML, because both defects were things the layout did with the marks rather than the marks
/// themselves. A press is proved to land by translating the row's edges into the button's own
/// space, and the paint is proved absent by reading the colour the row is actually filled with.
/// </para>
public sealed class WizardCustomerRowTests
{
    /// <summary>
    /// The most the item may reserve around its button, on each axis. The item's template keeps two
    /// pixels of its own between one row and the next, which no style here can take away: the theme
    /// sets it on the template's child, and a theme's setter outranks a style's. What is asserted is
    /// that nothing else is reserved -- the band above the fix was twenty pixels on this axis and
    /// twenty-four on the other.
    /// </summary>
    private const double Reserve = 4.0;

    /// <summary>
    /// How far inside the row a press is still counted as being on it. A pixel clear of the item's
    /// own reserve, so the press is on the row as it is presented rather than in the seam.
    /// </summary>
    private const double Inset = 3.0;

    [Fact]
    public void A_Press_On_A_Customer_Row_Reaches_The_Button_It_Holds()
    {
        var shapes = new List<string>();
        var rows = 0;
        var samples = 0;
        var misses = new List<string>();
        var widest = 0.0;
        var tallest = 0.0;

        AvaloniaUi.Run(window =>
        {
            var (_, list) = Show(window);

            foreach (var row in list.GetVisualDescendants().OfType<ListBoxItem>())
            {
                rows++;

                var button = row.GetVisualDescendants().OfType<Button>().First();

                shapes.Add(
                    $"a {row.Bounds.Width:0}x{row.Bounds.Height:0} row holds"
                    + $" a {button.Bounds.Width:0}x{button.Bounds.Height:0} button");

                widest = Math.Max(widest, row.Bounds.Width - button.Bounds.Width);
                tallest = Math.Max(tallest, row.Bounds.Height - button.Bounds.Height);

                // The middle of each edge and each corner, a pixel clear of the row's own seam.
                var points = new[]
                {
                    new Point(Inset, Inset),
                    new Point(row.Bounds.Width - Inset, Inset),
                    new Point(Inset, row.Bounds.Height - Inset),
                    new Point(row.Bounds.Width - Inset, row.Bounds.Height - Inset),
                    new Point(row.Bounds.Width / 2, Inset),
                    new Point(row.Bounds.Width / 2, row.Bounds.Height - Inset),
                    new Point(Inset, row.Bounds.Height / 2),
                    new Point(row.Bounds.Width - Inset, row.Bounds.Height / 2),
                    new Point(row.Bounds.Width / 2, row.Bounds.Height / 2),
                };

                foreach (var at in points)
                {
                    samples++;

                    var inside = row.TranslatePoint(at, button);

                    if (inside is not { } point || !button.Bounds.Contains(point))
                        misses.Add($"({at.X:0},{at.Y:0}) of {shapes[^1]} reaches nothing");
                }
            }
        });

        Assert.Equal(2, rows);
        Assert.Equal(18, samples);
        Assert.Empty(misses);
        Assert.True(
            widest <= Reserve && tallest <= Reserve,
            $"the row reserves more than its own seam around the button: {widest:0}x{tallest:0}"
            + $" pixels over {string.Join("; ", shapes)}");
    }

    /// <summary>
    /// The row keeps the colour it had. A selection of the list's own would say the customer was
    /// taken when all that happened was a press the button never saw.
    /// </summary>
    [Fact]
    public void Selecting_A_Customer_Row_Does_Not_Repaint_It()
    {
        var before = default((byte R, byte G, byte B));
        var after = default((byte R, byte G, byte B));

        AvaloniaUi.Run(window =>
        {
            var (wizard, list) = Show(window);

            var row = list.GetVisualDescendants().OfType<ListBoxItem>().First();
            var frame = FrameSize(wizard);
            var region = RegionOf(row, wizard, frame);

            before = Fill(Region(Frame(wizard, frame), frame.Width, region));

            list.SelectedIndex = 0;
            AvaloniaUi.Settle();
            AvaloniaUi.Settle();

            after = Fill(Region(Frame(wizard, frame), frame.Width, region));
        });

        Assert.True(
            before == after,
            $"selecting the row changed its fill from {Hex(before)} to {Hex(after)}");
    }

    private static (DistributorWizard Wizard, ListBox List) Show(Window window)
    {
        // The results go in before the data context is attached. The list reads its visibility from
        // a count that announces nothing when the collection is filled directly, so adding them
        // afterwards would leave the list hidden and both tests measuring an empty tree.
        var vm = new SessionsViewModel(Container())
        {
            WizardStep = WizardStep.Settle_Customer,
        };

        vm.WizardCustomerResults.Add(new CustomerLookupItem(1, "عميل نقدي", "01000000000"));
        vm.WizardCustomerResults.Add(new CustomerLookupItem(2, "سوبرماركت النور", "01098765432"));

        var wizard = new DistributorWizard { DataContext = vm };

        window.Content = wizard;
        window.Show();
        AvaloniaUi.Settle();

        var list = wizard.GetVisualDescendants()
            .OfType<ListBox>()
            .First(box => box.Name == "CustomerResults");

        return (wizard, list);
    }

    private static PixelSize FrameSize(Visual wizard) => new(
        (int)Math.Ceiling(wizard.Bounds.Width),
        (int)Math.Ceiling(wizard.Bounds.Height));

    /// <summary>The row's own rectangle in the frame, shrunk a little to stay off its border.</summary>
    private static PixelRect RegionOf(Visual row, Visual wizard, PixelSize frame)
    {
        var corner = row.TranslatePoint(default, wizard) ?? default;

        var x = Math.Max(0, (int)Math.Floor(corner.X) + 2);
        var y = Math.Max(0, (int)Math.Floor(corner.Y) + 2);
        var right = Math.Min(frame.Width, (int)Math.Ceiling(corner.X + row.Bounds.Width) - 2);
        var bottom = Math.Min(frame.Height, (int)Math.Ceiling(corner.Y + row.Bounds.Height) - 2);

        return new PixelRect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    /// <summary>
    /// The colour the region is mostly filled with -- the row's background rather than its ink, so
    /// that a change of weight or a moved glyph does not read as a change of fill.
    /// </summary>
    private static (byte R, byte G, byte B) Fill(byte[] pixels)
    {
        var counts = new Dictionary<uint, int>();

        for (var at = 0; at + 3 < pixels.Length; at += 4)
        {
            var colour = ((uint)pixels[at + 2] << 16) | ((uint)pixels[at + 1] << 8) | pixels[at];
            counts[colour] = counts.GetValueOrDefault(colour) + 1;
        }

        var fill = counts.OrderByDescending(pair => pair.Value).First().Key;
        return ((byte)(fill >> 16), (byte)(fill >> 8), (byte)fill);
    }

    private static string Hex((byte R, byte G, byte B) colour)
        => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    private static byte[] Frame(Visual of, PixelSize size)
    {
        using var shot = new RenderTargetBitmap(size);
        shot.Render(of);

        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            shot.CopyPixels(new PixelRect(size), pinned.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            pinned.Free();
        }

        return buffer;
    }

    private static byte[] Region(byte[] frame, int frameWidth, PixelRect region)
    {
        var bytes = new byte[region.Width * region.Height * 4];

        for (var y = 0; y < region.Height; y++)
            Buffer.BlockCopy(
                frame,
                (((region.Y + y) * frameWidth) + region.X) * 4,
                bytes,
                y * region.Width * 4,
                region.Width * 4);

        return bytes;
    }

    private static IServiceProvider Container()
        => new ServiceCollection()
            .AddDaftarPersistence(Path.Combine(Path.GetTempPath(), "daftar-wizard-row-unused.db"))
            .BuildServiceProvider();
}
