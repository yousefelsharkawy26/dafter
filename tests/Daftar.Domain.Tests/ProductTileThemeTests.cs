using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The product tiles on the sell and receive screens are painted in the Start button's blue.
///
/// <para>
/// Colour is the one thing a build cannot check. XAML parses happily with a mistyped hex, and a
/// navy caption on a blue tile compiles and ships just the same, so these read the values the
/// styles actually applied rather than the ones written in the file.
/// </para>
///
/// <para>
/// The text tests are the ones that matter. A TextBlock does not take its foreground from the
/// button it sits in: repaint the tile blue and every unstyled line inside it stays dark. The
/// receive tile also carries a badge class that was never defined anywhere in the project, so
/// nothing at all was styling it.
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
    /// per-thread, so a view constructed on the xUnit pool thread cannot resolve a cursor factory
    /// and the first style that touches one throws -- which is what these two screens did.
    /// </summary>
    private static void Inspect(Func<Control> build, Action<Control> seed, Action<Button[]> check)
    {
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            // Sized: the tiles live in a repeater, and nothing in a template is realised until
            // the grid has room for it.
            window.Width = 1500;
            window.Height = 950;

            var view = build();
            window.Content = view;
            window.Show();
            AvaloniaUi.Settle();

            // Both screens load on attach and refill their product list from the database, which
            // is empty here and would wipe anything added beforehand.
            seed(view);
            AvaloniaUi.Settle();

            check(TilesOf(view));
        });
    }

    [Fact]
    public void The_Sell_Tile_Is_Painted_In_The_Start_Buttons_Blue()
    {
        Inspect(
            () => new Views.SellView { DataContext = new SellViewModel(_services) },
            view => ((SellViewModel)view.DataContext!).FilteredProducts
                .Add(new ProductOption(1, "SKU-1", "منتج تجريبي")),
            tiles =>
            {
                Assert.NotEmpty(tiles);
                Assert.All(tiles, t => Assert.Equal(StartBlue, ColourOf(t.Background)));
            });
    }

    [Fact]
    public void The_Receive_Tile_Is_Painted_In_The_Start_Buttons_Blue()
    {
        Inspect(
            () => new Views.ReceiveView { DataContext = new ReceiveViewModel(_services) },
            view => ((ReceiveViewModel)view.DataContext!).FilteredProducts
                .Add(new ReceiveProductOption(1, "SKU-2", "صنف استلام")),
            tiles =>
            {
                Assert.NotEmpty(tiles);
                Assert.All(tiles, t => Assert.Equal(StartBlue, ColourOf(t.Background)));
            });
    }

    [Fact]
    public void Every_Line_Of_Text_On_A_Sell_Tile_Is_White()
    {
        Inspect(
            () => new Views.SellView { DataContext = new SellViewModel(_services) },
            view => ((SellViewModel)view.DataContext!).FilteredProducts
                .Add(new ProductOption(1, "SKU-1", "منتج تجريبي")),
            AssertNoDarkText);
    }

    [Fact]
    public void Every_Line_Of_Text_On_A_Receive_Tile_Is_White()
    {
        Inspect(
            () => new Views.ReceiveView { DataContext = new ReceiveViewModel(_services) },
            view => ((ReceiveViewModel)view.DataContext!).FilteredProducts
                .Add(new ReceiveProductOption(1, "SKU-2", "صنف استلام")),
            AssertNoDarkText);
    }

    private static void AssertNoDarkText(Button[] tiles)
    {
        Assert.NotEmpty(tiles);

        var dark = new List<string>();

        foreach (var tile in tiles)
        foreach (var text in tile.GetVisualDescendants().OfType<TextBlock>())
        {
            var colour = ColourOf(text.Foreground);
            if (colour is not { } c || c == Colors.White)
                continue;

            dark.Add($"\"{text.Text}\" is {c}");
        }

        Assert.Empty(dark);
    }
}