using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Daftar.Infrastructure;
using Daftar.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// Stock transfer is parked: hidden, and refused. Both halves are tested, because they fail
/// differently — a hidden button is still reachable from a stale binding, and a command that
/// still runs is the more expensive of the two mistakes.
/// </summary>
public sealed class StockTransferParkedTests
{
    private static StockViewModel NewViewModel() =>
        new(new ServiceCollection()
            .AddDaftarPersistence(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "daftar-stockview-unused.db"))
            .BuildServiceProvider());

    [Fact]
    public void The_Feature_Is_Off()
    {
        // Stated as its own test so that turning it back on is a visible, deliberate act rather
        // than something the rest of the file quietly stops checking.
        Assert.False(StockViewModel.StockTransferEnabled);
        Assert.False(new StockViewModel().IsStockTransferEnabled);
    }

    [Fact]
    public void The_Header_Button_Does_Not_Open_It()
    {
        var vm = NewViewModel();

        vm.ToggleTransferPanel();

        Assert.False(vm.IsTransferPanelOpen);
    }

    [Fact]
    public void The_Row_Button_Does_Not_Open_It_Either()
    {
        var vm = NewViewModel();
        var row = new StockRow(
            LotId: 1, ProductId: 1, LocationId: 1,
            ProductName: "Name", Sku: "SKU",
            LocationName: "Location", LocationType: "Warehouse",
            Quantity: 0m, QuantityDisplay: "0", BaseUnit: "pcs",
            UnitCost: 0m, UnitCostText: "0", TotalValue: 0m, TotalValueText: "0",
            SupplierName: null, ReceivedOn: "", IsLow: false, IsBlocked: false);

        vm.OpenTransferForStock(row);

        Assert.False(vm.IsTransferPanelOpen);
    }

    [Fact]
    public void The_Header_Button_Is_Not_Drawn_And_Not_Enabled()
    {
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            var view = new Views.StockView { DataContext = NewViewModel() };
            window.Content = view;
            window.Show();
            AvaloniaUi.Settle();

            var button = view.FindControl<Button>("TransferButton");

            Assert.NotNull(button);
            // Both, not just hidden: a greyed-out button tells an operator the feature exists and
            // makes them wonder why it does nothing.
            Assert.False(button!.IsVisible);
            Assert.False(button.IsEnabled);
        });
    }

    [Fact]
    public void The_Row_Button_Is_Not_Drawn()
    {
        // The row button lives in a DataTemplate, so it only exists once there is a row. The grid
        // needs a real size to realise one, which is why the window is sized here rather than left
        // to the default.
        AvaloniaUi.EnsureStarted();

        AvaloniaUi.Run(window =>
        {
            window.Width = 1400;
            window.Height = 900;

            var vm = NewViewModel();
            vm.FilteredRows.Add(new StockRow(
                LotId: 1, ProductId: 1, LocationId: 1,
                ProductName: "Name", Sku: "SKU",
                LocationName: "Location", LocationType: "Warehouse",
                Quantity: 1m, QuantityDisplay: "1", BaseUnit: "pcs",
                UnitCost: 1m, UnitCostText: "1", TotalValue: 1m, TotalValueText: "1",
                SupplierName: null, ReceivedOn: "", IsLow: false, IsBlocked: false));

            var view = new Views.StockView { DataContext = vm };
            window.Content = view;
            window.Show();
            AvaloniaUi.Settle();

            var buttons = view.GetVisualDescendants().OfType<Button>().ToArray();

            // Sanity: the row has to be realised at all, or this would pass for the wrong reason
            // the same way the first version of this test did.
            Assert.NotEmpty(buttons);

            Assert.DoesNotContain(buttons, b => MentionsTransfer(b));
        });
    }

    private static bool MentionsTransfer(Button button)
    {
        var text = string.Join(' ', button.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => t is not null));

        return text.Contains("تحويل", System.StringComparison.Ordinal);
    }
}