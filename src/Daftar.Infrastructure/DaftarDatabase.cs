using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daftar.Infrastructure;

/// <summary>
/// One-time database bootstrap, run from the Desktop composition root.
/// </summary>
public static class DaftarDatabase
{
    /// <summary>
    /// Sets WAL, applies migrations, then seeds first-run data.
    /// </summary>
    /// <param name="logger">
    /// Receives the integrity verdict. Without a sink it is discarded, which is the behaviour the
    /// desktop app had before: the one mechanism that can notice ledger drift wrote to a console
    /// that a GUI process does not have, so nobody ever read it.
    /// </param>
    /// <param name="backup">
    /// Runs after the pragmas and before the migration, only when migrations are pending.
    /// Migrations rewrite the ledger and the schema; without a restore point a bad one is
    /// irreversible. The delegate receives the destination path and returns false on failure, in
    /// which case the migration proceeds anyway but the loss of the restore point is logged.
    /// </param>
    /// <param name="migrateOverride">
    /// Test seam for the migration step. Left null in production, where the real migration runs.
    /// </param>
    /// <returns>The integrity verdict, so the caller can surface it instead of only logging it.</returns>
    public static async Task<Services.IntegrityReport> InitializeAsync(
        DaftarContext context,
        string adminPasswordHash,
        CancellationToken ct = default,
        ILogger? logger = null,
        Func<string, CancellationToken, Task<bool>>? backup = null,
        Func<Task>? migrateOverride = null,
        Func<Task<Services.IntegrityReport>>? integrityOverride = null)
    {
        logger ??= NullLogger.Instance;

        // Outside a transaction, so this actually takes effect. WAL is persistent once set.
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", ct);

        if (backup is not null && await HasPendingMigrationsAsync(context, ct))
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var path = System.IO.Path.Combine(
                DaftarPathsCompat.BackupDirectory, $"pre-migrate_{stamp}.db");

            try
            {
                if (await backup(path, ct))
                {
                    logger.LogInformation("Took a pre-migration backup at {Path}", path);
                }
                else
                {
                    logger.LogWarning(
                        "Pre-migration backup failed; applying migrations with no restore point.");
                }
            }
            catch (Exception ex)
            {
                // A failed backup must not itself be the thing that stops the shop opening.
                logger.LogError(ex, "Pre-migration backup threw; applying migrations anyway.");
            }
        }

        try
        {
            if (migrateOverride is null)
                await context.Database.MigrateAsync(ct);
            else
                await migrateOverride();
        }
        catch (Exception ex)
        {
            // Surfaced, not swallowed: the caller shows this to the user. Startup continuing
            // without the schema is worse than stopping with an explanation.
            logger.LogCritical(ex, "Database migration failed.");
            return Services.IntegrityReport.Failed("migration", ex.Message);
        }

        await Seed.SeedAsync(context, adminPasswordHash, ct);

        // The integrity scan walks whole tables and grows with the ledger, so it can throw or run
        // out of memory. It is a diagnostic, not a gate: letting it fail must not cost the user
        // their till, and losing it silently must not hide a corrupted book either.
        Services.IntegrityReport report;
        try
        {
            report = integrityOverride is null
                ? await new Services.IntegrityChecker(context).RunAllAsync(ct)
                : await integrityOverride();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Integrity check could not run; the ledger was not verified.");
            return Services.IntegrityReport.Skipped();
        }

        if (!report.IsClean)
            logger.LogCritical("INTEGRITY ALERT: {Report}", report.ToString());
        else
            logger.LogDebug("Integrity OK: {Report}", report.ToString());

        return report;
    }

    private static async Task<bool> HasPendingMigrationsAsync(DaftarContext context, CancellationToken ct)
        => (await context.Database.GetPendingMigrationsAsync(ct)).Any();
}

/// <summary>
/// Desktop-owned path helper, mirrored here so the Infrastructure layer does not take a
/// dependency on the Desktop project. Both must agree or backups land in the wrong place.
/// </summary>
internal static class DaftarPathsCompat
{
    /// <summary>Creates the directory on first access, so a snapshot path is always usable.</summary>
    internal static string BackupDirectory
    {
        get
        {
            var path = System.IO.Path.Combine(AppDataDirectory, "Backups");
            System.IO.Directory.CreateDirectory(path);
            return path;
        }
    }

    private static string AppDataDirectory
    {
        get
        {
            var root = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                  ?? System.IO.Path.Combine(
                      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                      ".local", "share");

            return System.IO.Path.Combine(root, "Daftar");
        }
    }
}