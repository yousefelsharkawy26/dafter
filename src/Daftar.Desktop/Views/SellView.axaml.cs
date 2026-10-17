using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Daftar.ViewModels;

namespace Daftar.Views;

public partial class SellView : UserControl
{
    private bool _loaded;

    public SellView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => FocusSearch();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_loaded || DataContext is not SellViewModel vm)
            return;

        _loaded = true;
        await ScreenLoad.Run(vm.LoadAsync, m => vm.Status = m);
        FocusSearch();
    }

    private void FocusSearch()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var searchBox = this.FindControl<Views.Components.SearchBox>("SearchBox");
            searchBox?.FocusSearch();
        });
    }

    private void OnSearchKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter && DataContext is SellViewModel vm)
        {
            vm.SubmitSearchCommand.Execute(null);
            e.Handled = true;
            FocusSearch();
        }
    }
}