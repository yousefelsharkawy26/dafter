using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Daftar.ViewModels;
using Daftar.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar;

public partial class App : Application
{
    /// <summary>
    /// Set once at startup so TabHost can reach the container. Avalonia 12 removed AvaloniaLocator,
    /// and there is one window tree, so a plain holder is simpler than plumbing.
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

    private readonly IServiceProvider? _services;

    public App() { }

    public App(IServiceProvider? services) => _services = services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (_services is not null)
            Services = _services;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new LoginWindow(new LoginViewModel(Services!));

        base.OnFrameworkInitializationCompleted();
    }
}