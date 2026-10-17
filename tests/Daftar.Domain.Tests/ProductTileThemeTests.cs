using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The product tiles on the sell and receive screens are painted in the Start button's blue, and an
/// out-of-stock one stays grey and readable.
///
/// <para>
/// Colour is the one thing a build cannot check. XAML parses happily with a mistyped hex, and a
/// navy caption on a blue tile compiles and ships just the same, so these read the values the
/// styles actually applied rather than the ones written in the file.
/// </para>
///
/// <para>
/// Both states are tested because they are the two things that can be wrong independently: a blue
/// tile with dark text, and a grey one with white text. Each has its own failure and its own fix.
/// </para>
///
/// <para>
/// Both screens are rendered against a real, migrated database. They load on attach, and against
/// an empty file that load throws out of an async void handler, which fails the test for a reason
/// that has nothing to do with the colour.
/// </para>
/// </summary>
public sealed class ProductTileThemeTests : IAsyncLifetime, IDisposable
{
    /// <summary>Theme.axaml, Button.start-btn.</summary>
    private static readonly Color StartBlue = Color.Parse("#0078D7");

    private static readonly Color EmptyGrey = Color.Parse("#E9E9E9");

    private static readonly Color EmptyText = Color.Parse("#3A3A3A");

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"daftar-tiles-{Guid.NewGuid():N}.db");

    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _services = new ServiceCollection().AddDaftarPersistence(_dbPath).BuildServiceProvider();

        using var scope = _services.CreateScope();
        await DaftarDatabase.InitializeAsync(
            scope.ServiceProvider.GetRequiredService<DaftarContext>(),
            PasswordHasher.Hash("admin"));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        (_services as IDisposable)?.Dispose();
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(p))
                File.Delete(p);
        GC.SuppressFinalize(this);
    }

    private static Button[] TilesOf(Control root) =>
        root.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Classes.Contains("tile"))
            .ToArray();

    private static Color? ColourOf(IBrush? brush) =>
        (brush as ISolidColorBrush)?.Color;

    /// <summary>
    /// Builds the view on the UI thread, not on the test thread. Avalonia's service locator is
    /// per-thread, and a view built on the xUnit pool thread cannot resolve a cursor factory.
    /// The seed runs after attach: both screens load on open and would clear anything added first.
    /// </summary>
    private static void Inspect(Func<Control> build, Action<Control> seed, Action<Button[]> check)
    {
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            // Sized: the tiles live in a repeater, and nothing in a template is realised until the
            // grid has room for it.
            window.Width = 1500;
            window.Height = 950;

            var view = build();
            window.Content = view;
            window.Show();
            AvaloniaUi.Settle();

            seed(view);
            AvaloniaUi.Settle();

            check(TilesOf(view));
        });
    }

    // ---------- arrangement ----------

    // The screens render ProductTileItem, not their own product type, so the arrangement builds
    // the same tile the ViewModel builds. Anything less and these tests would be checking a shape
    // the screen never sees.

    private static Action<Control> SellProduct(bool inStock) => view =>
    {
        var option = new ProductOption(1, "SKU-1", "أرز مصري فاخر");
        var unit = new CartUnitOption(1, "كجم", 1m, 42m, 35m, inStock ? 12m : 0m);
        option.AvailableUnits.Add(unit);
        option.SelectedUnit = unit;

        ((SellViewModel)view.DataContext!).FilteredProducts.Add(new ProductTileItem(
            option, 1, "SKU-1", "أرز مصري فاخر", ProductTileVariant.Sell,
            [new ProductTileUnit(1, "كجم", 1m, 42m, 35m, inStock ? 12m : 0m)],
            u => u.Price % 1 == 0 ? $"{u.Price:N0} ج" : $"{u.Price:N2} ج",
            _ => { },
            new RelayCommand(() => { })));
    };

    private static Action<Control> SellTwoUnits => view =>
    {
        var option = new ProductOption(1, "SKU-1", "أرز مصري فاخر");
        option.AvailableUnits.Add(new CartUnitOption(1, "كجم", 1m, 42m, 35m, 12m));
        option.AvailableUnits.Add(new CartUnitOption(2, "كرتونة", 10m, 380m, 320m, 2m));
        option.SelectedUnit = option.AvailableUnits[0];

        ((SellViewModel)view.DataContext!).FilteredProducts.Add(new ProductTileItem(
            option, 1, "SKU-1", "أرز مصري فاخر", ProductTileVariant.Sell,
            [new ProductTileUnit(1, "كجم", 1m, 42m, 35m, 12m),
             new ProductTileUnit(2, "كرتونة", 10m, 380m, 320m, 2m)],
            u => u.Price % 1 == 0 ? $"{u.Price:N0} ج" : $"{u.Price:N2} ج",
            _ => { },
            new RelayCommand(() => { })));
    };

    private static Action<Control> ReceiveProduct(bool inStock) => view =>
    {
        var option = new ReceiveProductOption(1, "SKU-2", "زيت زيتون بكر");
        var unit = new ReceiveUnitOption(1, "لتر", 1m, 60m, inStock ? 5m : 0m);
        option.AvailableUnits.Add(unit);
        option.SelectedUnit = unit;

        ((ReceiveViewModel)view.DataContext!).FilteredProducts.Add(new ProductTileItem(
            option, 1, "SKU-2", "زيت زيتون بكر", ProductTileVariant.Receive,
            [new ProductTileUnit(1, "لتر", 1m, 0m, 60m, inStock ? 5m : 0m)],
            u => u.PurchasePrice > 0 ? $"{u.PurchasePrice:N2} ج.م" : "سعر غير محدد",
            _ => { },
            new RelayCommand(() => { })));
    };

    private Control SellView() => new Views.SellView { DataContext = new SellViewModel(_services) };

    private Control ReceiveView() =>
        new Views.ReceiveView { DataContext = new ReceiveViewModel(_services) };

    // ---------- in stock: the blue tile ----------

    [Fact]
    public void The_Sell_Tile_Is_Painted_In_The_Start_Buttons_Blue()
    {
        Inspect(SellView, SellProduct(inStock: true), tiles =>
        {
            Assert.NotEmpty(tiles);
            Assert.All(tiles, t =>
            {
                Assert.True(t.IsEnabled);
                Assert.Equal(StartBlue, ColourOf(t.Background));
            });
        });
    }

    [Fact]
    public void The_Receive_Tile_Is_Painted_In_The_Start_Buttons_Blue()
    {
        Inspect(ReceiveView, ReceiveProduct(inStock: true), tiles =>
        {
            Assert.NotEmpty(tiles);
            Assert.All(tiles, t => Assert.Equal(StartBlue, ColourOf(t.Background)));
        });
    }

    [Fact]
    public void Every_Line_Of_Text_On_An_In_Stock_Tile_Is_White()
    {
        // A TextBlock does not take its foreground from the button it sits in: repaint the tile
        // blue and every unstyled line inside it stays dark. The receive tile also carries a badge
        // class that was never defined anywhere in the project, so nothing was styling it at all.
        Inspect(SellView, SellProduct(inStock: true), AssertAllWhite);
        Inspect(ReceiveView, ReceiveProduct(inStock: true), AssertAllWhite);
    }

    // ---------- out of stock: the grey tile ----------

    [Fact]
    public void An_Out_Of_Stock_Tile_Is_Grey_But_Still_Readable()
    {
        // Repainting the tile exposed something the pale card had been hiding.
        // Theme/Components.axaml fades every disabled tile to 0.55 opacity, which dark text on a
        // pale card survived and grey-on-grey does not: out-of-stock products came out at about
        // half strength, unreadable. So the fade is cancelled and the text goes dark.
        //
        // These rules have to be last in the collection. Avalonia resolves two rules that set the
        // same property by order, not by specificity, and the catch-all placed after them wins
        // silently -- which is exactly what the first attempt did.
        Inspect(SellView, SellProduct(inStock: false), tiles =>
        {
            var tile = Assert.Single(tiles);
            Assert.False(tile.IsEnabled);

            Assert.Equal(1d, tile.Opacity);
            Assert.Equal(EmptyGrey, ColourOf(tile.Background));

            foreach (var text in tile.GetVisualDescendants().OfType<TextBlock>())
                Assert.Equal(EmptyText, ColourOf(text.Foreground));
        });
    }

    [Fact]
    public void The_Receive_On_Hand_Figure_Has_A_Chip_Around_It()
    {
        // It was a bare number floating on the tile: the badge class it uses was never defined
        // anywhere in the project, so nothing at all styled it and nothing said what it counted.
        Inspect(ReceiveView, ReceiveProduct(inStock: true), tiles =>
        {
            var tile = Assert.Single(tiles);
            var badge = tile.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("info"));

            Assert.NotNull(badge);
            Assert.NotNull(badge!.Background);
            Assert.NotEqual(Colors.Transparent, ColourOf(badge.Background));
        });
    }

    [Fact]
    public void The_Sell_Tile_Names_Its_Unit_And_Says_What_Is_On_Hand()
    {
        // Found by looking, not by reading the markup: on the real screen the unit picker was
        // empty and neither the "/ unit" suffix nor the on-hand figure appeared, while the price
        // was perfectly correct and every other test here passed. The price was enough to make the
        // tile look healthy.
        Inspect(SellView, SellProduct(inStock: true), tiles =>
        {
            var tile = Assert.Single(tiles);
            var texts = tile.GetVisualDescendants().OfType<TextBlock>().ToArray();

            Assert.Contains(texts, t => t.Text == "/ كجم");
            Assert.Contains(texts, t => t.Text == "متاح: 12");
        });
    }

    [Fact]
    public void The_Unit_Picker_Shows_The_Unit_It_Is_On()
    {
        // An empty white box with a chevron in it tells the cashier nothing about what they are
        // about to sell, and it looks like the control failed to load rather than like a choice.
        //
        // Two units, or the picker is not shown at all and this passes against nothing. It did.
        Inspect(SellView, SellTwoUnits, tiles =>
        {
            var picker = Assert.Single(tiles[0].GetVisualDescendants().OfType<ComboBox>());

            Assert.True(picker.IsEffectivelyVisible);
            Assert.NotNull(picker.SelectedItem);

            // The tile opens on the first unit, so that is what the box must be showing.
            Assert.Equal("كجم", ((ProductTileUnit)picker.SelectedItem!).UnitName);

            // And the name is actually painted, not merely selected: the box was empty on screen
            // while SelectedItem was perfectly set, which is the whole failure.
            //
            // Dark as well as present. The unit name was rendered white on a white picker, so the
            // box looked empty while every assertion about its content passed.
            var labels = picker.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && t.Text is { Length: > 0 })
                .ToArray();

            Assert.NotEmpty(labels);
            Assert.All(labels, t => Assert.NotEqual(Colors.White, ColourOf(t.Foreground)));
            Assert.Contains(labels, t => t.Text == "كجم");
        });
    }

    [Fact]
    public void The_Tile_Fills_The_Cell_It_Is_Laid_Out_In()
    {
        // The square is now a UserControl wrapping the Button, which is one more thing between the
        // grid and the control the cashier clicks. Every other test here can pass on a tile that
        // has collapsed to a few pixels: the colour of a square you cannot see is still correct.
        // So this asks the question the rest of them cannot.
        Inspect(SellView, SellProduct(inStock: true), tiles =>
        {
            var tile = Assert.Single(tiles);

            Assert.True(tile.Bounds.Width > 100, $"width was {tile.Bounds.Width}");
            Assert.True(tile.Bounds.Height > 60, $"height was {tile.Bounds.Height}");

            // The square and the control that wraps it have to agree, or the grid thinks it
            // laid out a full cell while the button inside drew a fraction of one.
            var wrapper = tile.GetVisualAncestors().OfType<UserControl>().First();
            Assert.Equal(wrapper.Bounds.Width, tile.Bounds.Width, 2);
            Assert.Equal(wrapper.Bounds.Height, tile.Bounds.Height, 2);
        });
    }

    // ---------- shared assertion ----------

    private static void AssertAllWhite(Button[] tiles)
    {
        Assert.NotEmpty(tiles);

        var dark = new List<string>();

        foreach (var tile in tiles)
        foreach (var text in tile.GetVisualDescendants().OfType<TextBlock>())
        {
            // The unit picker is a white box sitting inside a blue tile, so its own text has to be
            // dark — that is the one place on the tile where white would be invisible, and it was
            // white for a while, which is why the picker looked empty. Everything else is on blue
            // and has to be white.
            if (text.GetVisualAncestors().OfType<ComboBox>().Any())
                continue;

            // Badges are pale pills of their own sitting on the blue, so their text is dark ink by
            // design — the same exception as the picker, and for the same reason. Their legibility
            // is a separate contract with its own measured floor; see TileBadgeLegibilityTests.
            if (text.GetVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("badge")))
                continue;

            var colour = ColourOf(text.Foreground);
            if (colour is not { } c || c == Colors.White)
                continue;

            dark.Add($"\"{text.Text}\" is {c}");
        }

        Assert.Empty(dark);
    }
}