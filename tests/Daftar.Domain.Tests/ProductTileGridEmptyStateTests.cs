using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Daftar.ViewModels;
using Daftar.Views.Components;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The grid says what to show when it has nothing to show.
///
/// <para>
/// Both till screens wrote the empty state into their own markup, side by side with the grid, and
/// the hint under the message had drifted by one vowel between them. Neither is a difference
/// anybody chose.
/// </para>
///
/// <para>
/// The risk in moving it is the one this pins: a list that starts empty and then fills, or fills
/// and then empties as the cashier types, has to flip the message each way. A grid that shows both
/// the message and the tiles at once looks like a fault.
/// </para>
/// </summary>
public sealed class ProductTileGridEmptyStateTests
{
    private static void Show(System.Func<ProductTileGrid> build, System.Action<ProductTileGrid> check)
    {
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            window.Width = 1500;
            window.Height = 950;

            var grid = build();
            window.Content = grid;
            window.Show();
            AvaloniaUi.Settle();

            check(grid);
        });
    }

    private static StackPanel MessageOf(ProductTileGrid grid) =>
        grid.GetVisualDescendants().OfType<StackPanel>()
            .Single(p => p.Children.OfType<TextBlock>().Any(t => t.Text == "لا توجد أصناف مطابقة"));

    private static ScrollViewer TilesOf(ProductTileGrid grid) =>
        grid.GetVisualDescendants().OfType<ScrollViewer>().Single();

    [Fact]
    public void An_Empty_List_Shows_The_Message_And_No_Grid()
    {
        Show(() => new ProductTileGrid { ItemsSource = new ObservableCollection<ProductTileItem>() },
             grid =>
             {
                 Assert.False(grid.HasItems);
                 Assert.True(MessageOf(grid).IsVisible);
                 Assert.False(TilesOf(grid).IsVisible);
             });
    }

    [Fact]
    public void A_Filled_List_Hides_The_Message()
    {
        var tile = new ProductTileItem(
            new object(), 1, "SKU-1", "أرز", ProductTileVariant.Sell,
            [new ProductTileUnit(1, "كجم", 1m, 42m, 35m, 5m)],
            _ => "42 ج",
            _ => { },
            new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }));

        Show(() => new ProductTileGrid
             {
                 ItemsSource = new ObservableCollection<ProductTileItem> { tile },
             },
             grid =>
             {
                 Assert.True(grid.HasItems);
                 Assert.False(MessageOf(grid).IsVisible);
                 Assert.True(TilesOf(grid).IsVisible);
             });
    }

    [Fact]
    public void Typing_A_Search_That_Matches_Nothing_Takes_The_Message_Over_And_Gives_It_Back()
    {
        // The whole point of moving it: one message, and it has to be right both ways as the
        // cashier types. This is the sequence that breaks a version wired the other way round.
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            window.Width = 1500;
            window.Height = 950;

            var grid = new ProductTileGrid
            {
                ItemsSource = new ObservableCollection<ProductTileItem>(),
            };
            window.Content = grid;
            window.Show();
            AvaloniaUi.Settle();

            Assert.True(MessageOf(grid).IsVisible);

            grid.ItemsSource = new ObservableCollection<ProductTileItem>
            {
                new(new object(), 1, "SKU-1", "أرز", ProductTileVariant.Sell,
                     [new ProductTileUnit(1, "كجم", 1m, 42m, 35m, 5m)],
                     _ => "42 ج",
                     _ => { },
                     new CommunityToolkit.Mvvm.Input.RelayCommand(() => { })),
            };
            AvaloniaUi.Settle();

            Assert.False(MessageOf(grid).IsVisible);
            Assert.True(TilesOf(grid).IsVisible);

            grid.ItemsSource = new ObservableCollection<ProductTileItem>();
            AvaloniaUi.Settle();

            Assert.True(MessageOf(grid).IsVisible);
            Assert.False(TilesOf(grid).IsVisible);
        });
    }

    [Fact]
    public void Emptying_The_Same_List_Takes_The_Message_Over_Again()
    {
        // The screens do not hand the grid a new list each time: filtering clears the collection
        // they already gave it and refills it. A grid that only recomputes when the reference
        // changes shows the message once and never takes it back, and stays blank for good.
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            window.Width = 1500;
            window.Height = 950;

            var items = new ObservableCollection<ProductTileItem>();
            var grid = new ProductTileGrid { ItemsSource = items };
            window.Content = grid;
            window.Show();
            AvaloniaUi.Settle();

            Assert.True(MessageOf(grid).IsVisible);

            items.Add(new(new object(), 1, "SKU-1", "أرز", ProductTileVariant.Sell,
                          [new ProductTileUnit(1, "كجم", 1m, 42m, 35m, 5m)],
                          _ => "42 ج",
                          _ => { },
                          new CommunityToolkit.Mvvm.Input.RelayCommand(() => { })));
            AvaloniaUi.Settle();

            Assert.False(MessageOf(grid).IsVisible);

            items.Clear();
            AvaloniaUi.Settle();

            Assert.True(MessageOf(grid).IsVisible);
            Assert.False(TilesOf(grid).IsVisible);
        });
    }

    [Fact]
    public void A_Null_List_Counts_As_Empty_Rather_Than_Throwing()
    {
        // The screens load asynchronously; the grid is attached and asked to lay out before the
        // first load finishes.
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            window.Width = 1500;
            window.Height = 950;

            var grid = new ProductTileGrid();
            window.Content = grid;
            window.Show();
            AvaloniaUi.Settle();

            Assert.False(grid.HasItems);
            Assert.True(MessageOf(grid).IsVisible);
        });
    }
}