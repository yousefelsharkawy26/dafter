using Daftar.Domain.Catalog;
using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// Exercises the real SQLite database: schema, seed, triggers and the posting transaction.
/// Each test gets its own file so they do not interfere.
/// </summary>
public sealed class DatabaseIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"daftar-test-{Guid.NewGuid():N}.db");

    private ServiceProviderFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = await ServiceProviderFixture.CreateAsync(_dbPath);

        // Schema + seed.
        using var scope = _fixture.Services.CreateScope();
        await DaftarDatabase.InitializeAsync(
            scope.ServiceProvider.GetRequiredService<DaftarContext>(),
            Infrastructure.Security.PasswordHasher.Hash("admin"));
    }

    public Task DisposeAsync()
    {
        _fixture.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);

        GC.SuppressFinalize(this);
    }

    private DaftarContext NewContext() => _fixture.Services.CreateScope().ServiceProvider
        .GetRequiredService<DaftarContext>();

    [Fact]
    public async Task Seed_CreatesAdminLocationAndWalkInCustomer()
    {
        var ctx = NewContext();

        // P1 14 + P2 9 + P3 6 = 29 domain tables, plus sqlite_sequence.
        Assert.Equal(30, await ctx.Database.SqlQueryRaw<int>(
            "SELECT count(*) AS Value FROM sqlite_master WHERE type='table' AND name NOT LIKE '__EF%'")
            .SingleAsync());

        Assert.Equal("admin", await ctx.Users.Select(u => u.Username).SingleAsync());
        Assert.True(await ctx.Users.Select(u => u.MustChangePassword).SingleAsync());
        Assert.Equal("Main Warehouse", await ctx.StockLocations.Select(l => l.Name).SingleAsync());
        Assert.Equal(9, await ctx.DocumentSequences.CountAsync());
        Assert.True(await ctx.Parties.AnyAsync(p => p.Code == "WALKIN"));
    }

    [Fact]
    public async Task Admin_PasswordIsHashed_NotPlain()
    {
        var hash = await NewContext().Users.Select(u => u.PasswordHash).SingleAsync();

        Assert.StartsWith("pbkdf2-sha256$", hash, StringComparison.Ordinal);
        Assert.DoesNotContain("admin", hash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JournalMode_IsWal()
    {
        var ctx = NewContext();

        // PRAGMA is not composable SQL, so read it over ADO instead of through a LINQ query.
        await ctx.Database.OpenConnectionAsync();
        await using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        var mode = (string?)await command.ExecuteScalarAsync();
        await ctx.Database.CloseConnectionAsync();

        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public async Task AppendOnlyTrigger_BlocksUpdateOnInventoryMovements()
    {
        var ctx = NewContext();

        var product = new Product("TRG-1", "Trigger test product");
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        var lot = Lot.Create(product.Id, new DateOnly(2026, 10, 5), null, 5m);
        lot.Freeze();
        ctx.Lots.Add(lot);
        await ctx.SaveChangesAsync();

        ctx.InventoryMovements.Add(new InventoryMovement
        {
            OccurredAt = DateTime.UtcNow,
            BusinessDate = new DateOnly(2026, 10, 5),
            ProductId = product.Id,
            LotId = lot.Id,
            LocationId = await ctx.StockLocations.Select(l => l.Id).SingleAsync(),
            Kind = InventoryMovementKind.Receipt,
            Quantity = 10m,
            SourceType = MovementSourceType.Opening,
            CreatedBy = 1,
        });
        await ctx.SaveChangesAsync();

        // Rewriting history must be refused by the database itself.
        var blocked = await Assert.ThrowsAsync<SqliteException>(() =>
            ctx.Database.ExecuteSqlRawAsync(
                "UPDATE InventoryMovements SET Quantity = 999 WHERE LotId = {0}", lot.Id));

        Assert.Contains("append-only", blocked.Message, StringComparison.Ordinal);

        // And the row is untouched.
        var quantity = await ctx.InventoryMovements
            .Where(m => m.LotId == lot.Id)
            .Select(m => m.Quantity)
            .SingleAsync();

        Assert.Equal(10m, quantity);
    }

    [Fact]
    public async Task PostingAPurchase_AtomicallyCreatesHeaderLotMovementAndBalance()
    {
        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var supplier = new Party
        {
            Code = "SUP-1", Name = "Factory", IsSupplier = true,
        };
        ctx.Parties.Add(supplier);

        var product = new Product("PASTE-1", "Tomato paste");
        var piece = new ProductUnit { Name = "piece", Factor = 1m, IsBase = true };
        var @case = new ProductUnit { Name = "case", Factor = 24m };
        product.Units.Add(piece);
        product.Units.Add(@case);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        piece.ProductId = product.Id;
        @case.ProductId = product.Id;

        var location = await ctx.StockLocations.Select(l => l.Id).SingleAsync();

        var purchase = new Purchase
        {
            Type = PurchaseType.Invoice,
            SupplierId = supplier.Id,
            LocationId = location,
            DocDate = new DateOnly(2026, 10, 5),
        };
        ctx.Purchases.Add(purchase);
        await ctx.SaveChangesAsync();

        purchase.AddItem(product, @case, quantity: 2m, unitPrice: 100m);
        await ctx.SaveChangesAsync();

        var poster = new PurchasePostingService(ctx);
        var result = await poster.PostAsync(purchase.Id, userId: 1);

        Assert.Equal("PO-000001", result.DocNo);
        Assert.Single(result.LotIds);

        // Header
        Assert.Equal(DocumentStatus.Posted, purchase.Status);
        Assert.NotNull(purchase.PostedAt);

        // Lot, from the line: 2 cases x 24 pieces = 48 base units, unit cost 200/48
        var lot = await ctx.Lots.Include(l => l.Product).SingleAsync();
        Assert.Equal(supplier.Id, lot.SupplierId);
        Assert.Equal(48m, await ctx.PurchaseItems.Select(i => i.BaseQuantity).SingleAsync());
        Assert.Equal(4.1667m, lot.UnitCost);

        // Movement, signed positive for a receipt
        var movement = await ctx.InventoryMovements.SingleAsync();
        Assert.Equal(InventoryMovementKind.Receipt, movement.Kind);
        Assert.Equal(48m, movement.Quantity);
        Assert.Equal(MovementSourceType.Purchase, movement.SourceType);
        Assert.Equal(purchase.Id, movement.SourceId);

        // Balance equals the movement
        var balance = await ctx.StockBalances.SingleAsync();
        Assert.Equal(48m, balance.Quantity);

        // Line points at the lot it created
        Assert.Equal(lot.Id, (await ctx.PurchaseItems.SingleAsync()).LotId);

        // Rule 11: the catalog is frozen once stock exists
        var reloaded = await ctx.Products.Include(p => p.Units).SingleAsync();
        Assert.True(reloaded.IsFrozen);
    }

    [Fact]
    public async Task PostingTwice_TakesTheNextNumberWithNoGap()
    {
        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        var poster = new PurchasePostingService(ctx);

        var supplier = new Party { Code = "SUP-G", Name = "Supplier G", IsSupplier = true };
        ctx.Parties.Add(supplier);
        var product = new Product("RICE-1", "Rice");
        product.Units.Add(new ProductUnit { Name = "kg", Factor = 1m, IsBase = true });
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();
        product.Units[0].ProductId = product.Id;
        var location = await ctx.StockLocations.Select(l => l.Id).SingleAsync();

        var numbers = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var p = new Purchase
            {
                SupplierId = supplier.Id, LocationId = location, DocDate = new DateOnly(2026, 10, 5),
            };
            ctx.Purchases.Add(p);
            await ctx.SaveChangesAsync();
            p.AddItem(product, product.Units[0], 5m, 10m);
            await ctx.SaveChangesAsync();

            numbers.Add((await poster.PostAsync(p.Id, 1)).DocNo);
        }

        Assert.Equal(["PO-000001", "PO-000002"], numbers);
        Assert.Equal(3, await ctx.DocumentSequences
            .Where(s => s.DocType == DocumentType.Purchase).Select(s => s.NextNumber).SingleAsync());
    }

    [Fact]
    public async Task FIFO_FromDatabase_DrawsTheOldestDeliveryFirst()
    {
        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredContext();

        var product = new Product("MILK-1", "Milk");
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        var location = await ctx.StockLocations.Select(l => l.Id).SingleAsync();
        var today = new DateOnly(2026, 10, 5);

        // Three deliveries, each a different age, oldest first.
        foreach (var (daysAgo, qty) in new[] { (30, 10m), (5, 10m), (1, 10m) })
        {
            var lot = Lot.Create(product.Id, today.AddDays(-daysAgo), null, 5m);
            lot.Freeze();
            ctx.Lots.Add(lot);
            await ctx.SaveChangesAsync();

            ctx.StockBalances.Add(StockBalance.Open(lot.Id, location, DateTime.UtcNow));
            await ctx.SaveChangesAsync();
            ctx.StockBalances.Local.Single(b => b.LotId == lot.Id).Apply(qty, DateTime.UtcNow);
            await ctx.SaveChangesAsync();
        }

        var balances = await ctx.StockBalances.Include(b => b.Lot).ToListAsync();

        // Asking for more than the oldest batch alone must continue into the next one, in order.
        var allocation = Fifo.Allocate(balances, 15m);

        Assert.Equal(2, allocation.Count);
        Assert.Equal(-30, allocation[0].Balance.Lot!.ReceivedOn.DayNumber - today.DayNumber);
        Assert.Equal(-5, allocation[1].Balance.Lot!.ReceivedOn.DayNumber - today.DayNumber);
    }
}
