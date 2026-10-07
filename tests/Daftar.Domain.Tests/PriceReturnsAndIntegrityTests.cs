using Daftar.Domain.Catalog;
using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Sales;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

public sealed class PriceReturnsAndIntegrityTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"daftar-ext-{Guid.NewGuid():N}.db");

    private ServiceProviderFixture _fixture = null!;

    private static readonly DateOnly Today = new(2026, 10, 5);

    public async Task InitializeAsync()
    {
        _fixture = await ServiceProviderFixture.CreateAsync(_dbPath);
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

    // ---------- price resolution ----------

    [Fact]
    public async Task PriceInForce_UsesTheLatestEffectiveDateNotAfterTheDocument()
    {
        var ctx = NewContext();
        var list = await ctx.PriceLists.SingleAsync(p => p.IsDefault);

        var product = new Product("P-1", "Priced thing");
        var unit = new ProductUnit { Name = "piece", Factor = 1m, IsBase = true };
        product.Units.Add(unit);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();
        unit.ProductId = product.Id;

        // Three price rows: 10 from January, 12 from June, 15 from December.
        ctx.ProductPrices.AddRange(
            new ProductPrice { PriceListId = list.Id, ProductUnitId = unit.Id, Price = 10m, EffectiveFrom = new DateOnly(2026, 1, 1) },
            new ProductPrice { PriceListId = list.Id, ProductUnitId = unit.Id, Price = 12m, EffectiveFrom = new DateOnly(2026, 6, 1) },
            new ProductPrice { PriceListId = list.Id, ProductUnitId = unit.Id, Price = 15m, EffectiveFrom = new DateOnly(2026, 12, 1) });
        await ctx.SaveChangesAsync();

        var resolver = new PriceResolutionService(ctx);

        Assert.Equal(10m, (await resolver.ResolveAsync(list.Id, unit.Id, new DateOnly(2026, 3, 1))).Price);
        Assert.Equal(12m, (await resolver.ResolveAsync(list.Id, unit.Id, Today)).Price);

        // The December price is not in force yet.
        Assert.Equal(12m, (await resolver.ResolveAsync(list.Id, unit.Id, new DateOnly(2026, 11, 30))).Price);
        Assert.Equal(15m, (await resolver.ResolveAsync(list.Id, unit.Id, new DateOnly(2026, 12, 1))).Price);
    }

    [Fact]
    public async Task MissingUnit_FallsBackToTheDefaultList()
    {
        var ctx = NewContext();
        var @default = await ctx.PriceLists.SingleAsync(p => p.IsDefault);

        var wholesale = new PriceList { Name = "Wholesale", IsDefault = false };
        ctx.PriceLists.Add(wholesale);

        var product = new Product("P-2", "Two units");
        var cheap = new ProductUnit { Name = "piece", Factor = 1m, IsBase = true };
        var carton = new ProductUnit { Name = "carton", Factor = 12m };
        product.Units.Add(cheap);
        product.Units.Add(carton);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();
        cheap.ProductId = product.Id;
        carton.ProductId = product.Id;

        // Wholesale prices only the piece. The default list prices both.
        ctx.ProductPrices.AddRange(
            new ProductPrice { PriceListId = wholesale.Id, ProductUnitId = cheap.Id, Price = 8m, EffectiveFrom = Today },
            new ProductPrice { PriceListId = @default.Id, ProductUnitId = cheap.Id, Price = 10m, EffectiveFrom = Today },
            new ProductPrice { PriceListId = @default.Id, ProductUnitId = carton.Id, Price = 100m, EffectiveFrom = Today });
        await ctx.SaveChangesAsync();

        var resolver = new PriceResolutionService(ctx);

        // The piece comes from the wholesale list.
        var piece = await resolver.ResolveAsync(wholesale.Id, cheap.Id, Today);
        Assert.Equal(8m, piece.Price);
        Assert.False(piece.FellBackToDefault);

        // The carton is absent there, so the default list steps in.
        var cartonPrice = await resolver.ResolveAsync(wholesale.Id, carton.Id, Today);
        Assert.Equal(100m, cartonPrice.Price);
        Assert.True(cartonPrice.FellBackToDefault);
        Assert.Equal(@default.Id, cartonPrice.PriceListId);
    }

    [Fact]
    public async Task UnpricedUnit_IsRefusedRatherThanGuessed()
    {
        var ctx = NewContext();
        var @default = await ctx.PriceLists.SingleAsync(p => p.IsDefault);

        var product = new Product("P-3", "No price");
        var unit = new ProductUnit { Name = "piece", Factor = 1m, IsBase = true };
        product.Units.Add(unit);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();
        unit.ProductId = product.Id;

        await Assert.ThrowsAsync<DomainException>(
            () => new PriceResolutionService(ctx).ResolveAsync(@default.Id, unit.Id, Today));
    }

    // ---------- returns ----------

    private async Task<(int SaleId, int LotId, Product Product, ProductUnit Unit, int CustomerId, int LocationId, PriceList List)>
        ArrangeSoldAsync(decimal quantity, decimal unitPrice)
    {
        var ctx = NewContext();

        var supplier = new Party { Code = $"S-{Guid.NewGuid():N}"[..12], Name = "Supplier", IsSupplier = true };
        var customer = new Party
        {
            Code = $"C-{Guid.NewGuid():N}"[..12], Name = "Buyer", IsCustomer = true,
            CustomerType = CustomerType.Retail,
        };
        ctx.Parties.AddRange(supplier, customer);

        var list = await ctx.PriceLists.SingleAsync(p => p.IsDefault);
        var product = new Product($"SKU-{Guid.NewGuid():N}"[..10], "Returnable");
        var unit = new ProductUnit { Name = "piece", Factor = 1m, IsBase = true };
        product.Units.Add(unit);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();
        unit.ProductId = product.Id;

        var location = await ctx.StockLocations.Select(l => l.Id).SingleAsync();

        var purchase = new Purchase { SupplierId = supplier.Id, LocationId = location, DocDate = Today };
        ctx.Purchases.Add(purchase);
        await ctx.SaveChangesAsync();
        purchase.AddItem(product, unit, 100m, 10m);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(purchase.Id, 1, Today);

        var lotId = await ctx.Lots.Select(l => l.Id).SingleAsync();

        var sale = new Sale
        {
            Type = SaleType.Invoice, PartyId = customer.Id,
            LocationId = location, PriceListId = list.Id, DocDate = Today,
        };
        ctx.Sales.Add(sale);
        await ctx.SaveChangesAsync();
        sale.AddItem(product, unit, quantity, unitPrice, unitPrice);
        await ctx.SaveChangesAsync();
        await new SalePostingService(ctx).PostAsync(sale.Id, 1, Today);

        return (sale.Id, lotId, product, unit, customer.Id, location, list);
    }

    [Fact]
    public async Task ReturnRestocksTheSameLotAndCreditsTheCustomer()
    {
        var sold = await ArrangeSoldAsync(quantity: 10m, unitPrice: 20m);
        var ctx = NewContext();

        var returns = new SaleReturnService(ctx);
        var draft = await returns.BeginAsync(sold.SaleId, Today, userId: 1);

        Assert.Single(draft.Lines);
        Assert.Equal(sold.LotId, draft.Lines[0].LotId);
        Assert.Equal(10m, draft.Lines[0].Quantity);

        // Credit the customer at what they paid, not at list.
        var returnSale = await ctx.Sales
            .Include(s => s.Items)
            .SingleAsync(s => s.Id == draft.SaleId);
        Assert.Equal(200m, returnSale.Total);

        // Stock went back into the same lot.
        var balanceBefore = await ctx.StockBalances
            .SingleAsync(b => b.LotId == sold.LotId).ContinueWith(t => t.Result.Quantity);

        await returns.PostAsync(draft.SaleId, 1);

        var balanceAfter = await ctx.StockBalances
            .SingleAsync(b => b.LotId == sold.LotId).ContinueWith(t => t.Result.Quantity);

        Assert.Equal(balanceBefore + 10m, balanceAfter);

        // The receivable goes back down.
        var customerRows = await ctx.PartyAccountTransactions
            .Where(t => t.PartyId == sold.CustomerId)
            .ToListAsync();
        Assert.Equal(0m, customerRows.Sum(t => t.Amount));
    }

    [Fact]
    public async Task PartialReturn_LeavesTheRestOutstanding()
    {
        var sold = await ArrangeSoldAsync(quantity: 10m, unitPrice: 20m);
        var ctx = NewContext();
        var returns = new SaleReturnService(ctx);

        var draft = await returns.BeginAsync(sold.SaleId, Today, userId: 1);
        // Return only 4 of the 10.
        var returnSale = await ctx.Sales.Include(s => s.Items).SingleAsync(s => s.Id == draft.SaleId);
        foreach (var item in returnSale.Items)
        {
            item.Lots[0].BaseQuantity = 4m;
            item.Quantity = 4m; // the credit follows the quantity actually returned
            item.Recalculate();
        }
        returnSale.Recalculate();
        await ctx.SaveChangesAsync();

        await returns.PostAsync(draft.SaleId, 1);

        var customerRows = await ctx.PartyAccountTransactions
            .Where(t => t.PartyId == sold.CustomerId)
            .ToListAsync();

        // 200 sold, 80 credited back: 120 still owed.
        Assert.Equal(120m, customerRows.Sum(t => t.Amount));
    }

    [Fact]
    public async Task SecondReturn_IsCappedByWhatIsStillOut()
    {
        var sold = await ArrangeSoldAsync(quantity: 10m, unitPrice: 20m);
        var ctx = NewContext();
        var returns = new SaleReturnService(ctx);

        // First return takes 6.
        var first = await returns.BeginAsync(sold.SaleId, Today, userId: 1);
        var firstSale = await ctx.Sales.Include(s => s.Items).SingleAsync(s => s.Id == first.SaleId);
        foreach (var item in firstSale.Items)
        {
            item.Lots[0].BaseQuantity = 6m;
            item.Quantity = 6m;
            item.Recalculate();
        }
        firstSale.Recalculate();
        await ctx.SaveChangesAsync();
        await returns.PostAsync(first.SaleId, 1);

        // Second return may only take the remaining 4.
        var second = await returns.BeginAsync(sold.SaleId, Today, userId: 1);
        Assert.Single(second.Lines);
        Assert.Equal(4m, second.Lines[0].Quantity);
    }

    [Fact]
    public async Task FullyReturnedSale_CannotBeReturnedAgain()
    {
        var sold = await ArrangeSoldAsync(quantity: 10m, unitPrice: 20m);
        var ctx = NewContext();
        var returns = new SaleReturnService(ctx);

        var draft = await returns.BeginAsync(sold.SaleId, Today, userId: 1);
        await returns.PostAsync(draft.SaleId, 1);

        await Assert.ThrowsAsync<DomainException>(
            () => returns.BeginAsync(sold.SaleId, Today, userId: 1));
    }

    [Fact]
    public async Task ScrapWritesOffUnsellableReturnedStockWithAReason()
    {
        var sold = await ArrangeSoldAsync(quantity: 10m, unitPrice: 20m);
        var ctx = NewContext();
        var returns = new SaleReturnService(ctx);

        var before = await ctx.StockBalances
            .SingleAsync(b => b.LotId == sold.LotId).ContinueWith(t => t.Result.Quantity);

        await returns.ScrapRestockedAsync(
            sold.LotId, sold.LocationId, 3m, ReasonCode.Damaged, userId: 1, Today, "broke in transit");

        var after = await ctx.StockBalances
            .SingleAsync(b => b.LotId == sold.LotId).ContinueWith(t => t.Result.Quantity);
        Assert.Equal(before - 3m, after);

        var scrap = await ctx.InventoryMovements.SingleAsync(m => m.Kind == InventoryMovementKind.Scrap);
        Assert.Equal(-3m, scrap.Quantity);
        Assert.Equal(ReasonCode.Damaged, scrap.ReasonCode);
    }

    [Fact]
    public async Task ScrapBeyondStock_IsRefused()
    {
        var sold = await ArrangeSoldAsync(quantity: 10m, unitPrice: 20m);
        var ctx = NewContext();
        var returns = new SaleReturnService(ctx);

        await Assert.ThrowsAsync<InsufficientStockException>(() => returns.ScrapRestockedAsync(
            sold.LotId, sold.LocationId, 9999m, ReasonCode.Damaged, 1, Today));
    }

    // ---------- integrity ----------

    [Fact]
    public async Task IntegrityChecker_FindsNothingWrongInACleanDatabase()
    {
        await ArrangeSoldAsync(quantity: 10m, unitPrice: 20m);
        var ctx = NewContext();

        var report = await new IntegrityChecker(ctx).RunAllAsync();

        Assert.True(report.IsClean, report.ToString());
    }

    [Fact]
    public async Task IntegrityChecker_SpotsADriftedBalance()
    {
        await ArrangeSoldAsync(quantity: 10m, unitPrice: 20m);
        var ctx = NewContext();

        // Rewrite a balance directly. The append-only trigger guards the ledger, not the cache —
        // which is exactly the drift this check exists to catch. The lot id is resolved first
        // because SQLite will not take a subquery in an UPDATE ... SET.
        var lotId = await ctx.Lots.Select(l => l.Id).FirstAsync();
        await ctx.Database.ExecuteSqlRawAsync(
            "UPDATE StockBalances SET Quantity = '999' WHERE LotId = {0}", lotId);

        var report = await new IntegrityChecker(ctx).RunAllAsync();

        Assert.False(report.IsClean);
        Assert.Contains(report.Problems, p => p.Check == "balance-vs-movements");
    }
}