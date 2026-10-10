using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.Infrastructure;

public static class DaftarServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQLite context with the audit interceptor. <paramref name="databasePath"/> is the
    /// file on disk; foreign keys and WAL are configured on the connection string.
    /// </summary>
    public static IServiceCollection AddDaftarPersistence(
        this IServiceCollection services,
        string databasePath)
    {
        services.AddSingleton<ICurrentUser, CurrentUser>();
        services.AddScoped<IUserAuthorizer, UserAuthorizer>();
        services.AddScoped<AuditStampInterceptor>();

        services.AddDbContext<DaftarContext>((provider, options) =>
        {
            options.UseSqlite(SqliteConnectionString(databasePath));
            options.AddInterceptors(provider.GetRequiredService<AuditStampInterceptor>());
        });

        services.AddScoped<Services.PurchasePostingService>();
        services.AddScoped<Services.SalePostingService>();
        services.AddScoped<Services.PaymentPostingService>();
        services.AddScoped<Services.StockTransferPostingService>();
        services.AddScoped<Services.StockCountService>();
        services.AddScoped<Services.SaleReturnService>();
        services.AddScoped<Services.PriceResolutionService>();
        services.AddScoped<Services.IntegrityChecker>();
        services.AddScoped<Services.DistributorSessionService>();

        return services;
    }

    public static void AddDaftarDevelopmentDefaults(this DbContextOptionsBuilder options, string databasePath)
    {
        options.UseSqlite(SqliteConnectionString(databasePath));
        options.EnableSensitiveDataLogging();
        options.LogTo(Console.WriteLine);
    }

    private static string SqliteConnectionString(string databasePath)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,

            // Private cache, not Shared. Shared cache swaps SQLite's file-level locking for
            // table-level locks between connections in the same process, which raises
            // SQLITE_LOCKED -- an error SQLite's busy handler deliberately does NOT retry, unlike
            // SQLITE_BUSY. With WAL the private cache is the recommended configuration anyway.

            // ON DELETE RESTRICT is declared in the model, so foreign keys must be enforced.
            // Microsoft.Data.Sqlite already defaults this to true; stated for the record.
            ForeignKeys = true,

            // Drives busy_timeout. A second writer waits this long instead of failing instantly,
            // which is what the till needs when a receipt posts while a report is reading.
            DefaultTimeout = 30,
        };

        return builder.ToString();
    }
}