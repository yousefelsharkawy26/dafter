using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Daftar.ViewModels;

namespace Daftar.Views;

public partial class ReturnsView : UserControl
{
    private bool _loaded;

    public ReturnsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_loaded || DataContext is not ReturnsViewModel vm)
            return;

        _loaded = true;
        await vm.InitializeAsync();
    }
}

