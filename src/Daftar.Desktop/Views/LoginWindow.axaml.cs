using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Daftar.ViewModels;

namespace Daftar.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel? _viewModel;

    public LoginWindow()
    {
        InitializeComponent();
    }

    public LoginWindow(LoginViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += async (_, _) =>
        {
            await viewModel.InitializeAsync();
            var usernameBox = this.FindControl<AutoCompleteBox>("UsernameBox");
            var passwordBox = this.FindControl<TextBox>("PasswordBox");

            if (string.IsNullOrWhiteSpace(viewModel.Username))
                usernameBox?.Focus();
            else
                passwordBox?.Focus();
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null || e.PropertyName != nameof(LoginViewModel.Succeeded) || !_viewModel.Succeeded)
            return;

        var shell = new MainWindow
        {
            DataContext = new MainWindowViewModel
            {
                Services = App.Services,
                CurrentUserName = _viewModel.SignedInUser?.FullName ?? _viewModel.Username,
            },
        };

        shell.Show();
        Close();
    }
}