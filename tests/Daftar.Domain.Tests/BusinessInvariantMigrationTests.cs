using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The three business invariants this migration re-declares in the model.
///
/// They were enforced only by the hand-written initial migration, so the model and the database
/// disagreed: the next <c>dotnet ef migrations add</c> would have emitted a DropIndex for all
/// three and removed them without a word. These tests run the real migration against a real
/// database so a regression is caught by the suite rather than by a customer's till.
/// </summary>
public sealed class BusinessInvariantMigrationTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"daftar-inv-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(p))
                File.Delete(p);
        GC.SuppressFinalize(this);
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var services = new ServiceCollection();
        services.AddDaftarPersistence(_dbPath);
        using var scope = services.BuildServiceProvider().CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        // Runs every migration including the one under test, against a real file.
        await ctx.Database.MigrateAsync();

        var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<long> ScalarAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task The_Model_Declares_The_Three_Invariants_As_Unique_Indexes()
    {
        // The point of the migration: the model now agrees with the database, so scaffolding a
        // future change no longer drops these.
        var services = new ServiceCollection();
        services.AddDaftarPersistence(_dbPath);
        using var scope = services.BuildServiceProvider().CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var productUnits = ctx.Model.FindEntityType(typeof(Daftar.Domain.Catalog.ProductUnit))!;
        var baseUnitIndex = productUnits.GetIndexes()
            .Single(i => i.GetFilter() is not null && i.GetFilter()!.Contains("IsBase"));
        Assert.True(baseUnitIndex.IsUnique);

        var sessions = ctx.Model.FindEntityType(typeof(Daftar.Domain.Distributors.DistributorSession))!;
        var openSessionIndex = sessions.GetIndexes()
            .Single(i => i.GetFilter() is not null && i.GetFilter()!.Contains("Open"));
        Assert.True(openSessionIndex.IsUnique);

        var counts = ctx.Model.FindEntityType(typeof(Daftar.Domain.Inventory.StockCount))!;
        var countIndex = counts.GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == "DistributorSessionId");
        Assert.True(countIndex.IsUnique);
    }

    [Fact]
    public async Task The_Dead_Shadow_Column_Is_Gone_And_StockCountId_Is_Constrained()
    {
        await using var conn = await OpenAsync();

        // The shadow FK EF invented when both ends declared the relationship.
        Assert.Equal(0, await ScalarAsync(conn,
            "SELECT COUNT(*) FROM pragma_table_info('DistributorSessions') WHERE name = 'StockCountId1';"));

        // The real column is now enforced rather than written blind.
        Assert.Equal(1, await ScalarAsync(conn,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('DistributorSessions') WHERE \"from\" = 'StockCountId';"));
    }

    [Fact]
    public async Task Two_Base_Units_For_One_Product_Are_Rejected()
    {
        var services = new ServiceCollection();
        services.AddDaftarPersistence(_dbPath);
        using var scope = services.BuildServiceProvider().CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        await ctx.Database.MigrateAsync();

        var product = new Daftar.Domain.Catalog.Product("INV-1", "صنف");
        product.Units.Add(new Daftar.Domain.Catalog.ProductUnit
        {
            Name = "قطعة",
            Factor = 1m,
            IsBase = true,
        });

        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        // A second base unit for the same product. Two of them would each divide the same stock
        // by a different UnitFactor, so BaseQuantity = Quantity x Factor stops agreeing across
        // the app.
        var second = new Daftar.Domain.Catalog.ProductUnit
        {
            Name = "كرتونة",
            Factor = 6m,
            IsBase = true,
            ProductId = product.Id,
        };
        ctx.ProductUnits.Add(second);

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        Assert.Contains("UNIQUE", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_Open_Sessions_For_One_Distributor_Are_Rejected()
    {
        var services = new ServiceCollection();
        services.AddDaftarPersistence(_dbPath);
        using var scope = services.BuildServiceProvider().CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        await ctx.Database.MigrateAsync();

        var location = new Daftar.Domain.Inventory.StockLocation
        {
            Name = "سيارة 1",
            Type = Daftar.Domain.Inventory.StockLocationType.Vehicle,
        };
        var cash = new Daftar.Domain.Finance.CashAccount
        {
            Name = "عهدة",
            Type = Daftar.Domain.Finance.CashAccountType.DistributorCustody,
        };
        ctx.Add(location);
        ctx.Add(cash);
        await ctx.SaveChangesAsync();

        var distributor = new Daftar.Domain.Distributors.Distributor
        {
            Name = "موزع",
            CommissionRate = 10m,
            StockLocationId = location.Id,
            CashAccountId = cash.Id,
        };
        ctx.Distributors.Add(distributor);
        await ctx.SaveChangesAsync();

        void OpenSession()
        {
            ctx.DistributorSessions.Add(new Daftar.Domain.Distributors.DistributorSession
            {
                DistributorId = distributor.Id,
                OpenedAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                OpenedBy = 1,
            });
        }

        OpenSession();
        await ctx.SaveChangesAsync();

        // Two open sessions make ExpectedCash count the same period twice and double-charge the
        // commission, so this is the invariant that was about to be dropped.
        OpenSession();

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        Assert.Contains("UNIQUE", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}