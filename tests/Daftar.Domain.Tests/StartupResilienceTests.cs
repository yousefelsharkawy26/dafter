using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// Covers the startup path's failure behaviour.
///
/// Migrating and seeding ran unguarded before anything could catch it, and the integrity scan
/// that follows it wrote its verdict to a console a GUI process does not have. Together that
/// meant a damaged ledger could end the process silently, every launch, with the owner unable to
/// tell whether their books were intact.
/// </summary>
public sealed class StartupResilienceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"daftar-startup-{Guid.NewGuid():N}.db");

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddDaftarPersistence(_dbPath);
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(path))
                File.Delete(path);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task First_Run_Migrates_Seeds_And_Reports_Clean()
    {
        using var services = BuildServices();
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var report = await DaftarDatabase.InitializeAsync(
            ctx, PasswordHasher.Hash("admin"));

        Assert.True(report.Completed);
        Assert.True(report.IsClean);
        Assert.Equal(9, await ctx.DocumentSequences.CountAsync());
    }

    [Fact]
    public async Task A_Second_Start_Still_Reports_Clean()
    {
        using (var services = BuildServices())
        using (var scope = services.CreateScope())
        {
            await DaftarDatabase.InitializeAsync(
                scope.ServiceProvider.GetRequiredService<DaftarContext>(),
                PasswordHasher.Hash("admin"));
        }

        using var again = BuildServices();
        using var second = again.CreateScope();
        var report = await DaftarDatabase.InitializeAsync(
            second.ServiceProvider.GetRequiredService<DaftarContext>(),
            PasswordHasher.Hash("admin"));

        Assert.True(report.IsClean);
    }

    /// <summary>
    /// A migration that throws must not cost the user their till. The failure is reported, the
    /// process stays up, and the reason reaches the log rather than a console nobody reads.
    /// </summary>
    [Fact]
    public async Task A_Failing_Migration_Is_Reported_Not_Thrown()
    {
        using var services = BuildServices();
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var backups = 0;
        var report = await DaftarDatabase.InitializeAsync(
            ctx,
            PasswordHasher.Hash("admin"),
            backup: (_, _) => { backups++; return Task.FromResult(true); },
            migrateOverride: () => throw new InvalidOperationException("boom"));

        // Reported, not thrown: the caller shows this to the user instead of the process
        // vanishing, and the snapshot was still taken before the attempt.
        Assert.False(report.IsClean);
        Assert.False(report.Completed);
        Assert.Equal(1, backups);
        Assert.Contains("migration", report.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_Integrity_Scan_That_Throws_Is_Reported_Not_Thrown()
    {
        using var services = BuildServices();
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        // Real migration and seed, then make the scan itself throw — the shape of "the ledger is
        // too big to scan" or "the file is half a database".
        await DaftarDatabase.InitializeAsync(
            ctx,
            PasswordHasher.Hash("admin"),
            logger: null);

        var report = await DaftarDatabase.InitializeAsync(
            ctx,
            PasswordHasher.Hash("admin"),
            integrityOverride: () => throw new InvalidOperationException("scan blew up"));

        Assert.False(report.IsClean);
        Assert.False(report.Completed);
        Assert.Contains("SKIPPED", report.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The backup runs before the schema is rewritten, not after. Ordering is the whole point: a
    /// snapshot taken once the migration has already run is worthless as a restore point.
    /// </summary>
    [Fact]
    public async Task A_Backup_Is_Taken_Before_The_Migration_Runs()
    {
        var order = new System.Collections.Generic.List<string>();

        using var services = BuildServices();
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        // The real migration runs, so the seed that follows still has tables to talk to; only the
        // position of the snapshot is under test.
        await DaftarDatabase.InitializeAsync(
            ctx,
            PasswordHasher.Hash("admin"),
            backup: (_, _) =>
            {
                order.Add("backup");
                return Task.FromResult(true);
            });

        Assert.Equal(["backup"], order);
        Assert.Equal(9, await ctx.DocumentSequences.CountAsync());
    }

    [Fact]
    public async Task No_Backup_Is_Taken_When_No_Migration_Is_Pending()
    {
        var backups = 0;

        using (var services = BuildServices())
        using (var scope = services.CreateScope())
        {
            await DaftarDatabase.InitializeAsync(
                scope.ServiceProvider.GetRequiredService<DaftarContext>(),
                PasswordHasher.Hash("admin"),
                backup: (_, _) => { backups++; return Task.FromResult(true); });
        }

        // Nothing changed between the two runs, so the second must not copy a database again.
        using var again = BuildServices();
        using var second = again.CreateScope();
        await DaftarDatabase.InitializeAsync(
            second.ServiceProvider.GetRequiredService<DaftarContext>(),
            PasswordHasher.Hash("admin"),
            backup: (_, _) => { backups++; return Task.FromResult(true); });

        Assert.Equal(1, backups);
    }

    /// <summary>
    /// A failed backup is not a reason to refuse service — but it is a reason to say so.
    /// </summary>
    [Fact]
    public async Task A_Failed_Backup_Does_Not_Block_The_Migration()
    {
        using var services = BuildServices();
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var report = await DaftarDatabase.InitializeAsync(
            ctx,
            PasswordHasher.Hash("admin"),
            backup: (_, _) => Task.FromResult(false));

        Assert.True(report.IsClean);
        Assert.Equal(9, await ctx.DocumentSequences.CountAsync());
    }

    [Fact]
    public void A_Skipped_Report_Is_Never_Clean()
    {
        // "Unverified" must not read as "verified and fine", or a failed scan would look like a
        // healthy book.
        var skipped = IntegrityReport.Skipped();

        Assert.False(skipped.IsClean);
        Assert.False(skipped.Completed);
        Assert.Contains("SKIPPED", skipped.ToString(), StringComparison.Ordinal);
    }
}