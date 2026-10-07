using Avalonia.Controls;
using Daftar.ViewModels;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        // Ctrl+Tab / Ctrl+PageUp / Ctrl+PageDown move between open pages, like a browser.
        if (e.KeyModifiers == Avalonia.Input.KeyModifiers.Control
            && e.Key is Avalonia.Input.Key.Tab or Avalonia.Input.Key.PageDown or Avalonia.Input.Key.PageUp)
        {
            vm.FocusOffset(e.Key == Avalonia.Input.Key.PageUp ? -1 : 1);
            e.Handled = true;
        }
    }
}