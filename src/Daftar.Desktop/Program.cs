using System;
using Avalonia;
using Daftar.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var services = Bootstrap.BuildServiceProvider();
        Bootstrap.InitializeDatabase(services);

        try
        {
            BuildAvaloniaApp(services).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            (services as IDisposable)?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp(IServiceProvider? services = null)
        => AppBuilder
            .Configure(() => new App(services))
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            // Avalonia's default font has no Arabic glyphs. Registering Cairo makes the family
            // resolvable, so every control falls back to it for Arabic text.
            .AfterSetup(_ => Fonts.Register())
            .LogToTrace();
}