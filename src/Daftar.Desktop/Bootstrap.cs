using System;
using System.IO;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar;

/// <summary>
/// Composition root: builds the container and prepares the database before the window opens, so
/// the first screen never waits on a migration.
/// </summary>
public static class Bootstrap
{
    /// <summary>Password for the seeded admin on first run. MustChangePassword forces a change.</summary>
    private const string FirstRunAdminPassword = "admin";

    public static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddDaftarPersistence(DaftarPaths.DatabasePath);

        services.AddSingleton<Daftar.Services.INavigationService, Daftar.Services.NavigationService>();

        // View models are resolved per page by TabHost. Transient so each tab gets a fresh one.
        services.AddTransient<LoginViewModel>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<SellViewModel>();
        services.AddTransient<ProductsViewModel>();
        services.AddTransient<ReceiveViewModel>();
        services.AddTransient<PaymentsViewModel>();
        services.AddTransient<PartiesViewModel>();
        services.AddTransient<StockViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ReportsViewModel>();
        services.AddTransient<ReturnsViewModel>();
        services.AddTransient<SessionsViewModel>();

        return services.BuildServiceProvider();
    }

    /// <summary>Applies migrations and seeds. Safe to call on every start.</summary>
    public static void InitializeDatabase(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        DaftarDatabase.InitializeAsync(context, PasswordHasher.Hash(FirstRunAdminPassword))
            .GetAwaiter()
            .GetResult();
    }
}