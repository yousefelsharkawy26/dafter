using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.Infrastructure.Services;
using Daftar.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        // A file sink, so a failed posting leaves a trace. Every diagnostic used to go to
        // Console.WriteLine, which a GUI process has nowhere to show and nobody reads.
        services.AddLogging(b => b.AddProvider(new Daftar.Desktop.FileLoggerProvider()));

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
    public static IntegrityReport InitializeDatabase(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        var logger = scope.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
            ?.CreateLogger("Daftar.Bootstrap");

        return DaftarDatabase.InitializeAsync(
                context,
                PasswordHasher.Hash(FirstRunAdminPassword),
                CancellationToken.None,
                logger,
                (destination, token) => BackupBeforeMigrationAsync(services, destination, token))
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Copies the database aside before a migration rewrites the schema or the ledger, so a bad
    /// migration is recoverable. VACUUM INTO produces a consistent copy even with WAL active.
    /// </summary>
    private static async Task<bool> BackupBeforeMigrationAsync(
        IServiceProvider services,
        string destination,
        CancellationToken ct)
    {
        using var backupScope = services.CreateScope();
        var db = backupScope.ServiceProvider.GetRequiredService<DaftarContext>();

        var file = await BackupStore.ForApp.CreateAtAsync(db, destination, ct);
        return File.Exists(file.Path);
    }
}