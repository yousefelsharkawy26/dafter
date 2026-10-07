using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Daftar.ViewModels;

namespace Daftar.Views;

public partial class DashboardView : UserControl
{
    private bool _loaded;

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Load once, when the view model arrives — TabHost creates views lazily.
        if (_loaded || DataContext is not DashboardViewModel vm)
            return;

        _loaded = true;
        await vm.LoadAsync();
    }
}