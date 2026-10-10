using System;
using Avalonia.Controls;
using Daftar.Infrastructure;
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
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.RequestSignOut += OnSignOut;

            vm.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.IsStartMenuOpen) && vm.IsStartMenuOpen)
                {
                    var sv = this.FindControl<ScrollViewer>("StartMenuScrollViewer");
                    if (sv is not null)
                    {
                        sv.Offset = new Avalonia.Vector(0, 0);
                    }
                }
            };
        }
    }

    private void OnSignOut()
    {
        if (App.Services is { } services)
        {
            // Clear the identity first. Everything written between sign-out and the next sign-in
            // used to be stamped with the previous user's id, which is precisely the window where
            // the audit trail is relied on.
            services.GetRequiredService<ICurrentUser>().SignOut();

            var loginVm = services.GetRequiredService<LoginViewModel>();
            var loginWin = new LoginWindow(loginVm);
            loginWin.Show();
            Close();
        }
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