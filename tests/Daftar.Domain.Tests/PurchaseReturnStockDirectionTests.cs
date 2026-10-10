using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Domain.Catalog;
using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Daftar.Domain.Tests;

/// <summary>
/// A supplier return must take stock out of the warehouse.
///
/// It used to be classified as an inbound movement: posting one raised the balance by the returned
/// quantity AND minted a brand-new batch for it, so the original batch was left untouched and the
/// same goods were counted twice. The payable ledger moved the other way, which meant the stock
/// ledger and the money ledger openly contradicted each other while both looked plausible.
///
/// Receiving and returning are now inverse operations, so this is a round-trip test.
/// </summary>
public sealed class PurchaseReturnStockDirectionTests : IAsyncLifetime, IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"daftar-pr-{Guid.NewGuid():N}.db");
    private ServiceProviderFixture _fixture = null!;

    private static readonly DateOnly DocDate = new(2026, 10, 7);

    public PurchaseReturnStockDirectionTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _fixture = await ServiceProviderFixture.CreateAsync(_dbPath);
        using var scope = _fixture.Services.CreateScope();
        await DaftarDatabase.InitializeAsync(
            scope.ServiceProvider.GetRequiredService<DaftarContext>(),
            PasswordHasher.Hash("admin"));
    }

    public Task DisposeAsync() { _fixture.Dispose(); return Task.CompletedTask; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(p))
                File.Delete(p);
        GC.SuppressFinalize(this);
    }

    private async Task<(DaftarContext ctx, Product product, ProductUnit unit, int location, int supplier)>
        ArrangeAsync()
    {
        var ctx = _fixture.Services.CreateScope()
            .ServiceProvider.GetRequiredService<DaftarContext>();

        var product = new Product("RET-1", "صنف مرتجع");
        product.Units.Add(new ProductUnit { Name = "قطعة", Factor = 1m, IsBase = true });
        ctx.Products.Add(product);

        var supplier = new Party { Code = "SUP-RET", Name = "مورد", IsSupplier = true };
        ctx.Parties.Add(supplier);

        await ctx.SaveChangesAsync();

        var location = await ctx.StockLocations
            .Where(l => l.Type == StockLocationType.Warehouse)
            .Select(l => l.Id)
            .FirstAsync();

        return (ctx, product, product.Units[0], location, supplier.Id);
    }

    private static async Task<decimal> WarehouseStockAsync(DaftarContext ctx, int productId)
        => (await ctx.StockBalances
            .Where(b => b.Lot!.ProductId == productId)
            .ToListAsync()).Sum(b => b.Quantity);

    [Fact]
    public async Task Receiving_Then_Returning_Leaves_The_Warehouse_Where_It_Started()
    {
        var (ctx, product, unit, location, supplier) = await ArrangeAsync();

        var receipt = new Purchase
        {
            SupplierId = supplier,
            LocationId = location,
            DocDate = DocDate,
        };
        receipt.AddItem(product, unit, quantity: 100m, unitPrice: 50m);
        ctx.Purchases.Add(receipt);
        await ctx.SaveChangesAsync();

        var posted = await new PurchasePostingService(ctx).PostAsync(receipt.Id, 1, DocDate);
        _out.WriteLine($"after receiving 100: {await WarehouseStockAsync(ctx, product.Id)}");

        Assert.Equal(100m, await WarehouseStockAsync(ctx, product.Id));

        // The return names the batch the goods go back out of, exactly as the UI does when a
        // return line is built from an original invoice.
        var originalLotId = await ctx.PurchaseItems
            .Where(i => i.PurchaseId == receipt.Id)
            .Select(i => i.LotId)
            .FirstAsync();

        var returnDoc = new Purchase
        {
            SupplierId = supplier,
            LocationId = location,
            Type = PurchaseType.Return,
            DocDate = DocDate,
        };
        var returnLine = returnDoc.AddItem(product, unit, quantity: 30m, unitPrice: 50m);
        returnLine.LotId = originalLotId;

        ctx.Purchases.Add(returnDoc);
        await ctx.SaveChangesAsync();

        await new PurchasePostingService(ctx).PostAsync(returnDoc.Id, 1, DocDate);
        var after = await WarehouseStockAsync(ctx, product.Id);
        _out.WriteLine($"after returning 30: {after}");

        Assert.Equal(70m, after);
    }

    [Fact]
    public async Task A_Return_Does_Not_Create_A_Second_Batch_For_The_Same_Goods()
    {
        var (ctx, product, unit, location, supplier) = await ArrangeAsync();

        var receipt = new Purchase { SupplierId = supplier, LocationId = location, DocDate = DocDate };
        receipt.AddItem(product, unit, quantity: 100m, unitPrice: 50m);
        ctx.Purchases.Add(receipt);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(receipt.Id, 1, DocDate);

        var lotsBefore = await ctx.Lots.CountAsync(l => l.ProductId == product.Id);
        Assert.Equal(1, lotsBefore);

        var originalLotId = await ctx.PurchaseItems
            .Where(i => i.PurchaseId == receipt.Id).Select(i => i.LotId).FirstAsync();

        var returnDoc = new Purchase
        {
            SupplierId = supplier,
            LocationId = location,
            Type = PurchaseType.Return,
            DocDate = DocDate,
        };
        var line = returnDoc.AddItem(product, unit, quantity: 30m, unitPrice: 50m);
        line.LotId = originalLotId;
        ctx.Purchases.Add(returnDoc);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(returnDoc.Id, 1, DocDate);

        // The phantom batch is the real damage: the goods now exist twice.
        Assert.Equal(lotsBefore, await ctx.Lots.CountAsync(l => l.ProductId == product.Id));
    }

    [Fact]
    public async Task A_Return_Movement_Is_Recorded_As_Negative()
    {
        var (ctx, product, unit, location, supplier) = await ArrangeAsync();

        var receipt = new Purchase { SupplierId = supplier, LocationId = location, DocDate = DocDate };
        receipt.AddItem(product, unit, quantity: 100m, unitPrice: 50m);
        ctx.Purchases.Add(receipt);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(receipt.Id, 1, DocDate);

        var originalLotId = await ctx.PurchaseItems
            .Where(i => i.PurchaseId == receipt.Id).Select(i => i.LotId).FirstAsync();

        var returnDoc = new Purchase
        {
            SupplierId = supplier,
            LocationId = location,
            Type = PurchaseType.Return,
            DocDate = DocDate,
        };
        var line = returnDoc.AddItem(product, unit, quantity: 30m, unitPrice: 50m);
        line.LotId = originalLotId;
        ctx.Purchases.Add(returnDoc);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(returnDoc.Id, 1, DocDate);

        var movement = await ctx.InventoryMovements
            .Where(m => m.Kind == InventoryMovementKind.PurchaseReturn)
            .SingleAsync();

        _out.WriteLine($"return movement quantity = {movement.Quantity}");

        Assert.Equal(-30m, movement.Quantity);
    }

    [Fact]
    public async Task A_Return_That_Would_Drive_Stock_Negative_Is_Rejected()
    {
        var (ctx, product, unit, location, supplier) = await ArrangeAsync();

        var receipt = new Purchase { SupplierId = supplier, LocationId = location, DocDate = DocDate };
        receipt.AddItem(product, unit, quantity: 100m, unitPrice: 50m);
        ctx.Purchases.Add(receipt);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(receipt.Id, 1, DocDate);

        var originalLotId = await ctx.PurchaseItems
            .Where(i => i.PurchaseId == receipt.Id).Select(i => i.LotId).FirstAsync();

        var returnDoc = new Purchase
        {
            SupplierId = supplier,
            LocationId = location,
            Type = PurchaseType.Return,
            DocDate = DocDate,
        };
        var line = returnDoc.AddItem(product, unit, quantity: 500m, unitPrice: 50m);
        line.LotId = originalLotId;
        ctx.Purchases.Add(returnDoc);
        await ctx.SaveChangesAsync();

        // Sending back more than was ever received must fail the whole post, not create the goods.
        await Assert.ThrowsAnyAsync<Exception>(
            () => new PurchasePostingService(ctx).PostAsync(returnDoc.Id, 1, DocDate));
    }

    [Fact]
    public async Task A_Return_Without_A_Batch_Is_Rejected()
    {
        var (ctx, product, unit, location, supplier) = await ArrangeAsync();

        var returnDoc = new Purchase
        {
            SupplierId = supplier,
            LocationId = location,
            Type = PurchaseType.Return,
            DocDate = DocDate,
        };
        // No LotId: there is nothing to draw from, so this must not silently create a batch.
        returnDoc.AddItem(product, unit, quantity: 10m, unitPrice: 50m);
        ctx.Purchases.Add(returnDoc);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAnyAsync<DomainException>(
            () => new PurchasePostingService(ctx).PostAsync(returnDoc.Id, 1, DocDate));

        Assert.Equal(0, await ctx.Lots.CountAsync(l => l.ProductId == product.Id));
    }

    [Fact]
    public async Task The_Supplier_Balance_Moves_Both_Ways_And_Agrees_With_Stock()
    {
        var (ctx, product, unit, location, supplier) = await ArrangeAsync();

        var receipt = new Purchase { SupplierId = supplier, LocationId = location, DocDate = DocDate };
        receipt.AddItem(product, unit, quantity: 100m, unitPrice: 50m);
        ctx.Purchases.Add(receipt);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(receipt.Id, 1, DocDate);

        var afterReceipt = (await ctx.PartyAccountTransactions
            .Where(t => t.PartyId == supplier).ToListAsync()).Sum(t => t.Amount);
        Assert.Equal(-5000m, afterReceipt);

        var originalLotId = await ctx.PurchaseItems
            .Where(i => i.PurchaseId == receipt.Id).Select(i => i.LotId).FirstAsync();

        var returnDoc = new Purchase
        {
            SupplierId = supplier,
            LocationId = location,
            Type = PurchaseType.Return,
            DocDate = DocDate,
        };
        var line = returnDoc.AddItem(product, unit, quantity: 30m, unitPrice: 50m);
        line.LotId = originalLotId;
        ctx.Purchases.Add(returnDoc);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(returnDoc.Id, 1, DocDate);

        var afterReturn = (await ctx.PartyAccountTransactions
            .Where(t => t.PartyId == supplier).ToListAsync()).Sum(t => t.Amount);
        _out.WriteLine($"payable: after receipt {afterReceipt}, after return {afterReturn}");

        // 70 units x 50 = 3500 owed.
        Assert.Equal(-3500m, afterReturn);
    }
}