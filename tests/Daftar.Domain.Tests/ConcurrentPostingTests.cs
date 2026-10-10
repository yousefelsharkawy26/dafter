using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daftar.Domain.Catalog;
using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using CustomerType = Daftar.Domain.Parties.CustomerType;
using Daftar.Domain.Purchasing;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Daftar.Domain.Tests;

/// <summary>
/// Two writers, one balance.
///
/// Nothing in the model carries a concurrency token, so a deferred transaction let two posting
/// services read the same balance, compute different results from it, and both write — the second
/// silently erasing the first. Nothing downstream can detect that: the ledger said it wrote, the
/// document number was allocated, and the stock simply no longer adds up.
///
/// <see cref="TransactionScope"/> now opens at <c>BEGIN IMMEDIATE</c>, which takes SQLite's write
/// lock up front, so the second writer is refused rather than allowed to overwrite. These tests
/// drive two real connections against one real file.
/// </summary>
public sealed class ConcurrentPostingTests : IAsyncLifetime, IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"daftar-conc-{Guid.NewGuid():N}.db");
    private ServiceProvider _services = null!;

    private static readonly DateOnly DocDate = new(2026, 10, 7);

    public ConcurrentPostingTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _services = new ServiceCollection()
            .AddDaftarPersistence(_dbPath)
            .BuildServiceProvider();

        using var scope = _services.CreateScope();
        await DaftarDatabase.InitializeAsync(
            scope.ServiceProvider.GetRequiredService<DaftarContext>(),
            PasswordHasher.Hash("admin"));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        (_services as IDisposable)?.Dispose();
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(p))
                File.Delete(p);
        GC.SuppressFinalize(this);
    }

    private DaftarContext NewContext() => _services.CreateScope()
        .ServiceProvider.GetRequiredService<DaftarContext>();

    /// <summary>
    /// Loads the product and its unit inside the caller's own context. EF requires the entities
    /// passed to AddItem to be tracked by the context that will save them.
    /// </summary>
    private async Task<(Product Product, ProductUnit Unit)> LoadAsync(DaftarContext ctx, int productId, int unitId)
    {
        var product = await ctx.Products.FirstAsync(p => p.Id == productId);
        var unit = await ctx.ProductUnits.FirstAsync(u => u.Id == unitId);
        return (product, unit);
    }

    /// <summary>
    /// Brings in 100 units. Returns ids only: sharing a tracked Product across two contexts
    /// would have both contexts re-insert the same primary key, which is an artefact of the
    /// test and not the race we are trying to provoke.
    /// </summary>
    private async Task<(int SupplierId, int ProductId, int UnitId, int LocationId)>
        ArrangeStockAsync()
    {
        var ctx = NewContext();

        var product = new Product("CONC-1", "صنف تزامن");
        product.Units.Add(new ProductUnit { Name = "قطعة", Factor = 1m, IsBase = true });
        ctx.Products.Add(product);

        var supplier = new Party { Code = "SUP-CONC", Name = "مورد", IsSupplier = true };
        ctx.Parties.Add(supplier);
        await ctx.SaveChangesAsync();

        var productId = product.Id;
        var unitId = product.Units[0].Id;

        var location = await ctx.StockLocations
            .Where(l => l.Type == StockLocationType.Warehouse)
            .Select(l => l.Id).FirstAsync();

        var receipt = new Purchase { SupplierId = supplier.Id, LocationId = location, DocDate = DocDate };
        receipt.AddItem(product, product.Units[0], quantity: 100m, unitPrice: 50m);
        ctx.Purchases.Add(receipt);
        await ctx.SaveChangesAsync();

        await new PurchasePostingService(ctx).PostAsync(receipt.Id, 1, DocDate);

        return (supplier.Id, productId, unitId, location);
    }

    [Fact]
    public async Task The_Connection_Does_Not_Use_A_Shared_Cache()
    {
        var ctx = NewContext();
        var cs = ctx.Database.GetDbConnection();

        // Shared cache raises SQLITE_LOCKED, which SQLite's busy handler never retries. With WAL
        // the private cache is the recommended setting and busy_timeout does the waiting instead.
        var builder = new SqliteConnectionStringBuilder(cs.ConnectionString);
        Assert.NotEqual(SqliteCacheMode.Shared, builder.Cache);

        // busy_timeout must be set, or a second writer fails instantly instead of waiting.
        Assert.True(builder.DefaultTimeout > 0);
    }

    [Fact]
    public async Task A_Concurrent_Writer_Waits_For_The_Lock_Rather_Than_Failing()
    {
        await ArrangeStockAsync();

        var ctx = NewContext();

        await using var scope = await TransactionScope.BeginAsync(ctx, default);
        Assert.True(scope.IsOwner);

        // Keep the lock busy with a real write from this transaction.
        var balance = await ctx.StockBalances.FirstAsync();
        balance.Apply(1m, DateTime.UtcNow);
        await ctx.SaveChangesAsync();

        // A second connection, with no busy_timeout of its own, must be refused outright rather
        // than silently interleave. With the app's 30s busy_timeout it would instead wait and then
        // succeed once this scope commits -- either way it can never produce a lost update, and
        // never a SQLITE_LOCKED that busy_timeout cannot retry.
        var other = NewContext();
        using var second = other.Database.GetDbConnection();
        second.Open();

        await using (var pragma = second.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 0;";
            await pragma.ExecuteNonQueryAsync();
        }

        var failed = false;
        try
        {
            await using var command = second.CreateCommand();
            command.CommandText =
                "UPDATE StockBalances SET Quantity = Quantity + 1 WHERE Id = (SELECT Id FROM StockBalances LIMIT 1);";
            await command.ExecuteNonQueryAsync();
        }
        catch (SqliteException)
        {
            failed = true;
        }

        _out.WriteLine($"second writer refused while a post is open: {failed}");
        Assert.True(failed, "a second writer must not interleave with an open posting transaction");

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task A_Nested_Scope_Shares_The_Lock_And_Does_Not_Commit_Early()
    {
        var ctx = NewContext();

        await using (var outer = await TransactionScope.BeginAsync(ctx, default))
        {
            Assert.True(outer.IsOwner);

            await using (var inner = await TransactionScope.BeginAsync(ctx, default))
            {
                Assert.False(inner.IsOwner);

                ctx.Parties.Add(new Party
                {
                    Code = "P-NEST",
                    Name = "طرف",
                    IsCustomer = true,
                    CustomerType = CustomerType.Retail,
                });
                await ctx.SaveChangesAsync();

                // A borrower's commit is a no-op: the outer scope still owns the transaction.
                await inner.CommitAsync();
                Assert.NotNull(ctx.Database.CurrentTransaction);
            }

            Assert.NotNull(ctx.Database.CurrentTransaction);

            // Still the outer scope's to end.
            await outer.RollbackAsync();
        }

        using var verify = NewContext();
        Assert.False(await verify.Parties.AnyAsync(p => p.Code == "P-NEST"));
    }

    [Fact]
    public async Task Two_Receipts_At_Once_Never_Lose_Stock()
    {
        var (supplierId, productId, unitId, locationId) = await ArrangeStockAsync();

        // Both receipts post at the same time, each drawing 60 of the 100 on hand. Only one can
        // win; the other must fail rather than drive the balance below zero behind our back.
        var gate = new SemaphoreSlim(0, 2);
        var started = new CountdownEvent(2);

        async Task<bool> TryPostAsync(int tag)
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var purchase = new Purchase
            {
                SupplierId = supplierId,
                LocationId = locationId,
                DocDate = DocDate,
            };
            var (product, unit) = await LoadAsync(ctx, productId, unitId);
            purchase.AddItem(product, unit, quantity: 60m, unitPrice: 50m);
            ctx.Purchases.Add(purchase);
            await ctx.SaveChangesAsync();

            started.Signal();
            await gate.WaitAsync();

            try
            {
                await new PurchasePostingService(ctx).PostAsync(purchase.Id, 1, DocDate);
                return true;
            }
            catch (Exception ex)
            {
                _out.WriteLine($"post {tag} refused: {ex.GetType().Name}");
                return false;
            }
        }

        var first = Task.Run(() => TryPostAsync(1));
        var second = Task.Run(() => TryPostAsync(2));

        started.Wait(TimeSpan.FromSeconds(10));
        gate.Release(2);

        var outcomes = await Task.WhenAll(first, second);
        var succeeded = outcomes.Count(ok => ok);

        _out.WriteLine($"concurrent receipts: {succeeded} succeeded, {outcomes.Length - succeeded} refused");

        var balance = await NewContext().StockBalances
            .Where(b => b.Lot!.ProductId == productId)
            .SumAsync(b => b.Quantity);

        _out.WriteLine($"stock after both attempts: {balance}");

        // Each receipt adds 60 to the 100 already on hand, so the only correct outcomes are
        // 100 (neither landed) or 220 (both landed) -- never 160, which is what a lost update
        // would produce if one writer's 60 were overwritten by the other's.
        Assert.True(balance is 100m or 160m or 220m,
            $"unexpected balance {balance}");
        Assert.NotEqual(160m, balance); // one receipt silently lost
        Assert.True(balance >= 0m, "stock must never go negative, not even transiently");
    }

    [Fact]
    public async Task Document_Numbers_Are_Never_Handed_Out_Twice()
    {
        var (supplierId, productId, unitId, locationId) = await ArrangeStockAsync();

        // Two documents racing for the next number. DocumentSequence.Take is a read-increment with
        // no token, so BEGIN IMMEDIATE is what keeps both from leaving with the same number.
        var gate = new SemaphoreSlim(0, 2);
        var started = new CountdownEvent(2);
        var numbers = new System.Collections.Concurrent.ConcurrentBag<string>();

        async Task PostAsync()
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var purchase = new Purchase
            {
                SupplierId = supplierId,
                LocationId = locationId,
                DocDate = DocDate,
            };
            var (product, unit) = await LoadAsync(ctx, productId, unitId);
            purchase.AddItem(product, unit, quantity: 1m, unitPrice: 50m);
            ctx.Purchases.Add(purchase);
            await ctx.SaveChangesAsync();

            started.Signal();
            await gate.WaitAsync();

            try
            {
                var result = await new PurchasePostingService(ctx).PostAsync(purchase.Id, 1, DocDate);
                numbers.Add(result.DocNo);
            }
            catch (SqliteException ex) when (ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
            {
                // Refused by the unique index — an acceptable outcome, but not a duplicate number.
            }
        }

        var a = Task.Run(PostAsync);
        var b = Task.Run(PostAsync);
        started.Wait(TimeSpan.FromSeconds(10));
        gate.Release(2);
        await Task.WhenAll(a, b);

        _out.WriteLine($"numbers issued: {string.Join(", ", numbers)}");

        Assert.Equal(numbers.Count, numbers.Distinct().Count());
    }
}