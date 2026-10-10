using Microsoft.EntityFrameworkCore;

namespace Daftar.Infrastructure;

/// <summary>
/// One-time database bootstrap, run from the Desktop composition root.
///
/// Order matters: WAL must be set before Migrate, because EF wraps migrations in a transaction and
/// SQLite refuses to change journal mode inside one.
/// </summary>
public static class DaftarDatabase
{
    /// <summary>Sets WAL, applies migrations, then seeds first-run data.</summary>
    public static async Task InitializeAsync(
        DaftarContext context,
        string adminPasswordHash,
        CancellationToken ct = default)
    {
        // Outside a transaction, so this actually takes effect. WAL is persistent once set.
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", ct);

        await context.Database.MigrateAsync(ct);
        await Seed.SeedAsync(context, adminPasswordHash, ct);

        var report = await new Services.IntegrityChecker(context).RunAllAsync(ct);
        if (!report.IsClean)
        {
            Console.WriteLine($"[INTEGRITY ALERT] {report}");
        }
    }
}