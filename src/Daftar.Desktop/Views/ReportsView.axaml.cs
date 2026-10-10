using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Daftar.ViewModels;

namespace Daftar.Views;

public partial class ReportsView : UserControl
{
    /// <summary>Columns pulled out of a grid, kept so the next permission change can put them back.</summary>
    private readonly Dictionary<DataGrid, List<DataGridColumn>> _withheld = [];

    private bool _loaded;
    private ReportsViewModel? _viewModel;

    public ReportsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_loaded || DataContext is not ReportsViewModel vm)
            return;

        _loaded = true;
        _viewModel = vm;
        vm.PropertyChanged += OnViewModelPropertyChanged;

        await vm.InitializeAsync();
    }

    /// <summary>
    /// Takes the cost, margin and profit columns out when the role lacks Reports.Cost.
    ///
    /// The figures are already blanked in the view model, so this is presentation, not
    /// enforcement. But a column headed "التكلفة" filled with dashes reads as a broken report
    /// rather than as an answer the role is not entitled to, which is the wrong thing to show.
    ///
    /// Avalonia's DataGridColumn has no Visibility property and cannot be named with x:Name, so
    /// the columns are removed from the collection and held here instead.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ReportsViewModel.CanViewCost) || _viewModel is null)
            return;

        var canSee = _viewModel.CanViewCost;

        ToggleCostColumns(SalesGrid, canSee, "التكلفة", "صافي الربح", "نسبة الربح");
        ToggleCostColumns(ProductsGrid, canSee, "إجمالي التكلفة", "صافي الربح", "نسبة الربح %");
    }

    private void ToggleCostColumns(DataGrid grid, bool canSee, params string[] headers)
    {
        if (canSee)
        {
            if (!_withheld.Remove(grid, out var restored))
                return;

            foreach (var column in restored)
                grid.Columns.Add(column);

            return;
        }

        if (_withheld.ContainsKey(grid))
            return;

        var kept = new List<DataGridColumn>();
        var removed = new List<DataGridColumn>();

        foreach (var column in grid.Columns)
        {
            if (column.Header is string header && Array.IndexOf(headers, header) >= 0)
                removed.Add(column);
            else
                kept.Add(column);
        }

        if (removed.Count == 0)
            return;

        grid.Columns.Clear();
        foreach (var column in kept)
            grid.Columns.Add(column);

        _withheld[grid] = removed;
    }
}
