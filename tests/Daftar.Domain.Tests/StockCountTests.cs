using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Domain.Catalog;
using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Sales;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// A count freezes what the location held when counting started, then posts the difference between
/// that and what the counter found. The two only agree if nothing moved in between — and nothing
/// stops it moving, because the "the location is effectively locked" comment on <see cref="StockCount"/>
/// was never backed by a lock.
///
/// It is not reachable today: the only caller freezes and posts inside the distributor settlement's
/// single transaction. This is about the trap for whoever calls it next, and about the settlement
/// staying exactly as it was.
/// </summary>
public sealed class StockCountTests : IAsyncLifetime, IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 7);

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"daftar-count-{Guid.NewGuid():N}.db");

    private ServiceProvider _services = null!;

    private int _locationId;
    private int _productId;
    private int _unitId;
    private int _listId;
    private int _cashId;
    private int _lotId;
    private bool _stocked;

    public async Task InitializeAsync()
    {
        _services = new ServiceCollection().AddDaftarPersistence(_dbPath).BuildServiceProvider();

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

    private async Task EnsureStockAsync(decimal quantity)
    {
        if (_stocked) return;
        _stocked = true;

        var ctx = NewContext();

        var supplier = new Party { Code = "SUP-CNT", Name = "Supplier", IsSupplier = true };
        var product = new Product("CNT-1", "Counted");
        var unit = new ProductUnit { Name = "piece", Factor = 1m, IsBase = true };
        product.Units.Add(unit);
        ctx.Parties.Add(supplier);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        _productId = product.Id;
        _unitId = unit.Id;
        _listId = (await ctx.PriceLists.SingleAsync(p => p.IsDefault)).Id;
        _cashId = await ctx.CashAccounts.Select(c => c.Id).FirstAsync();
        _locationId = await ctx.StockLocations.Select(l => l.Id).SingleAsync();

        var purchase = new Purchase
        {
            SupplierId = supplier.Id,
            LocationId = _locationId,
            DocDate = Today,
        };
        ctx.Purchases.Add(purchase);
        await ctx.SaveChangesAsync();
        purchase.AddItem(product, unit, quantity, 5m);
        await ctx.SaveChangesAsync();

        await new PurchasePostingService(ctx).PostAsync(purchase.Id, 1, Today);

        // The lot this purchase actually created, not whichever lot the seed data happens to
        // leave first in the table.
        _lotId = (int)await ctx.PurchaseItems
            .Where(i => i.PurchaseId == purchase.Id)
            .Select(i => (int?)i.LotId)
            .FirstAsync();
    }

    private async Task<decimal> BalanceAsync()
    {
        var ctx = NewContext();
        return await ctx.StockBalances
            .Where(b => b.LocationId == _locationId && b.LotId == _lotId)
            .Select(b => b.Quantity)
            .FirstAsync();
    }

    /// <summary>Sells from the warehouse, moving stock the way the till really does.</summary>
    private async Task SellAsync(decimal quantity)
    {
        var ctx = NewContext();

        var customer = new Party
        {
            Code = $"C-{Guid.NewGuid():N}"[..10],
            Name = "Buyer",
            IsCustomer = true,
            CustomerType = CustomerType.Retail,
            CreditLimit = 100_000m,
        };
        ctx.Parties.Add(customer);
        await ctx.SaveChangesAsync();

        var product = await ctx.Products.Include(p => p.Units).FirstAsync(p => p.Id == _productId);
        var unit = product.Units.First(u => u.Id == _unitId);

        var sale = new Sale
        {
            Type = SaleType.Invoice,
            PartyId = customer.Id,
            LocationId = _locationId,
            PriceListId = _listId,
            DocDate = Today,
        };
        ctx.Sales.Add(sale);
        await ctx.SaveChangesAsync();
        sale.AddItem(product, unit, quantity, 10m, 10m);
        sale.Recalculate();
        await ctx.SaveChangesAsync();

        await new SalePostingService(ctx).PostAsync(sale.Id, 1, Today);
    }

    // ---------- the rule ----------

    [Fact]
    public async Task Stock_Moving_Between_The_Snapshot_And_The_Posting_Is_Refused()
    {
        await EnsureStockAsync(100m);

        var ctx = NewContext();
        var counts = new StockCountService(ctx);

        var count = await counts.BeginAsync(_locationId, Today, 1);
        await counts.RecordCountAsync(count.Id, _lotId, 95m);

        // The counter is still counting. Meanwhile the till sells twenty of the very lot he is
        // standing in front of.
        await SellAsync(20m);
        Assert.Equal(80m, await BalanceAsync());

        var ex = await Assert.ThrowsAsync<Domain.Shared.DomainException>(() =>
            counts.PostAsync(count.Id, 1));

        Assert.Contains("Stock moved", ex.Message, StringComparison.Ordinal);

        // The refusal is the whole point: a wrong number is worse than no number.
        Assert.Equal(80m, await BalanceAsync());
    }

    [Fact]
    public async Task A_Count_Taken_Without_Movement_In_Between_Still_Posts_Its_Variance()
    {
        await EnsureStockAsync(100m);

        var ctx = NewContext();
        var counts = new StockCountService(ctx);

        var count = await counts.BeginAsync(_locationId, Today, 1);
        await counts.RecordCountAsync(count.Id, _lotId, 95m);

        var result = await counts.PostAsync(count.Id, 1);

        Assert.Equal(95m, await BalanceAsync());
        Assert.Equal(new[] { (lotId: _lotId, difference: -5m) },
                     result.Adjustments.Select(a => (a.LotId, a.Difference)).ToArray());
    }

    [Fact]
    public async Task A_Matched_Count_Posts_Nothing_And_Leaves_The_Balance_Alone()
    {
        // No variance means no movement, so there is nothing to be stale about. The settlement
        // counts exactly like this when nobody hands it a physical number.
        await EnsureStockAsync(100m);

        var ctx = NewContext();
        var counts = new StockCountService(ctx);

        var count = await counts.BeginAsync(_locationId, Today, 1);
        await counts.RecordCountAsync(count.Id, _lotId, 100m);

        var result = await counts.PostAsync(count.Id, 1);

        Assert.Empty(result.Adjustments);
        Assert.Equal(100m, await BalanceAsync());
    }
}