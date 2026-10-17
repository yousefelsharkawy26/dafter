using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Daftar.ViewModels;

namespace Daftar.Views;

public partial class PaymentsView : UserControl
{
    private bool _loaded;

    public PaymentsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_loaded || DataContext is not PaymentsViewModel vm)
            return;

        _loaded = true;
        await ScreenLoad.Run(vm.LoadAsync, m => vm.Status = m);
    }
}