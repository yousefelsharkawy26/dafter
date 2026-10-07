using System;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Domain.Catalog;
using Daftar.Domain.Finance;
using Daftar.Domain.Parties;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Sales;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Services;
using Daftar.Infrastructure.Security;
using Daftar.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Daftar.Domain.Tests;

/// <summary>
/// The rule the whole ledger rests on, end to end:
///   - a receipt leaves US owing the supplier
///   - a sale leaves the customer owing us
///   - paying moves the balance back toward zero, whichever side it was on
/// </summary>
public sealed class ReceivablePayableDirectionTests : IAsyncLifetime, IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"daftar-rp-{Guid.NewGuid():N}.db");
    private ServiceProviderFixture _fixture = null!;

    public ReceivablePayableDirectionTests(ITestOutputHelper output) => _out = output;

    private static readonly DateOnly DocDate = new(2026, 10, 7);

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
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private DaftarContext NewContext() => _fixture.Services.CreateScope()
        .ServiceProvider.GetRequiredService<DaftarContext>();

    private static async Task<decimal> BalanceAsync(DaftarContext ctx, int partyId)
    {
        var rows = await ctx.PartyAccountTransactions
            .Where(t => t.PartyId == partyId)
            .ToListAsync();

        return rows.Sum(r => r.Amount);
    }

    private async Task<(DaftarContext Ctx, Product Product, ProductUnit Unit, int Location)> ArrangeAsync()
    {
        var ctx = NewContext();
        var product = new Product("P1", "صنف");
        product.Units.Add(new ProductUnit { Name = "قطعة", Factor = 1m, IsBase = true });
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();
        product.Units[0].ProductId = product.Id;

        var priceList = await ctx.PriceLists.FirstAsync();
        ctx.ProductPrices.Add(new ProductPrice
        {
            PriceListId = priceList.Id,
            ProductUnitId = product.Units[0].Id,
            Price = 50m,
            EffectiveFrom = DocDate,
        });

        var location = await ctx.StockLocations.Select(l => l.Id).FirstAsync();
        await ctx.SaveChangesAsync();

        return (ctx, product, product.Units[0], location);
    }

    /// <summary>Buys stock so there is something to sell.</summary>
    private static async Task<int> ReceiveStockAsync(
        DaftarContext ctx, Product product, ProductUnit unit, int location)
    {
        var supplier = new Party { Code = "S-SUP", Name = "مورد", IsSupplier = true };
        ctx.Parties.Add(supplier);
        await ctx.SaveChangesAsync();

        var purchase = new Purchase
        {
            SupplierId = supplier.Id,
            LocationId = location,
            DocDate = DocDate,
        };

        purchase.AddItem(product, unit, quantity: 100m, unitPrice: 50m);
        ctx.Purchases.Add(purchase);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(purchase.Id, 1, DocDate);

        return supplier.Id;
    }

    [Fact]
    public async Task Receiving_stock_leaves_US_Owing_The_Supplier()
    {
        var (ctx, product, unit, location) = await ArrangeAsync();
        var supplier = new Party { Code = "S1", Name = "مورد", IsSupplier = true };
        ctx.Parties.Add(supplier);
        await ctx.SaveChangesAsync();

        var purchase = new Purchase
        {
            SupplierId = supplier.Id,
            LocationId = location,
            DocDate = DocDate,
        };

        purchase.AddItem(product, unit, quantity: 10m, unitPrice: 50m);
        ctx.Purchases.Add(purchase);
        await ctx.SaveChangesAsync();

        await new PurchasePostingService(ctx).PostAsync(purchase.Id, 1, DocDate);

        var balance = await BalanceAsync(ctx, supplier.Id);

        _out.WriteLine($"supplier balance after a 500 purchase = {balance}");

        Assert.Equal(-500m, balance);
        Assert.True(balance < 0m, "a payable is negative on our side of the ledger");
    }

    [Fact]
    public async Task Selling_leaves_The_Customer_Owing_Us()
    {
        var (ctx, product, unit, location) = await ArrangeAsync();
        var customer = new Party
        {
            Code = "C1",
            Name = "عميل",
            IsCustomer = true,
            CustomerType = CustomerType.Retail,
        };

        ctx.Parties.Add(customer);
        await ctx.SaveChangesAsync();

        await ReceiveStockAsync(ctx, product, unit, location);

        var sale = new Sale
        {
            Type = SaleType.Invoice,
            PartyId = customer.Id,
            LocationId = location,
            PriceListId = (await ctx.PriceLists.FirstAsync()).Id,
            DocDate = DocDate,
        };

        sale.AddItem(product, unit, quantity: 4m, listPrice: 50m, unitPrice: 50m);
        ctx.Sales.Add(sale);
        await ctx.SaveChangesAsync();

        await new SalePostingService(ctx).PostAsync(sale.Id, 1, DocDate);

        var balance = await BalanceAsync(ctx, customer.Id);

        _out.WriteLine($"customer balance after a 200 sale = {balance}");

        Assert.Equal(200m, balance);
        Assert.True(balance > 0m, "a receivable is positive on our side of the ledger");
    }

    [Fact]
    public async Task Paying_A_Supplier_In_Full_Leaves_Nothing_Owed()
    {
        var (ctx, product, unit, location) = await ArrangeAsync();
        var supplier = new Party { Code = "S2", Name = "مورد", IsSupplier = true };
        ctx.Parties.Add(supplier);
        await ctx.SaveChangesAsync();

        var purchase = new Purchase
        {
            SupplierId = supplier.Id,
            LocationId = location,
            DocDate = DocDate,
        };

        purchase.AddItem(product, unit, quantity: 10m, unitPrice: 50m);
        ctx.Purchases.Add(purchase);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(purchase.Id, 1, DocDate);

        Assert.Equal(-500m, await BalanceAsync(ctx, supplier.Id));

        var cash = await ctx.CashAccounts.FirstAsync();

        var payment = new Payment
        {
            Direction = PaymentDirection.Out,
            PartyId = supplier.Id,
            CashAccountId = cash.Id,
            Method = PaymentMethod.Wallet,
            Amount = 500m,
            DocDate = DocDate,
            PurchaseId = purchase.Id,
        };

        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();
        await new PaymentPostingService(ctx).PostAsync(payment.Id, 1, DocDate);

        var balance = await BalanceAsync(ctx, supplier.Id);
        _out.WriteLine($"supplier balance after paying in full = {balance}");

        Assert.Equal(0m, balance);
    }

    [Fact]
    public async Task Paying_A_Customer_In_Full_Leaves_Nothing_Receivable()
    {
        var (ctx, product, unit, location) = await ArrangeAsync();
        var customer = new Party
        {
            Code = "C2",
            Name = "عميل",
            IsCustomer = true,
            CustomerType = CustomerType.Retail,
        };

        ctx.Parties.Add(customer);
        await ctx.SaveChangesAsync();

        await ReceiveStockAsync(ctx, product, unit, location);

        var sale = new Sale
        {
            Type = SaleType.Invoice,
            PartyId = customer.Id,
            LocationId = location,
            PriceListId = (await ctx.PriceLists.FirstAsync()).Id,
            DocDate = DocDate,
        };

        sale.AddItem(product, unit, quantity: 4m, listPrice: 50m, unitPrice: 50m);
        ctx.Sales.Add(sale);
        await ctx.SaveChangesAsync();
        await new SalePostingService(ctx).PostAsync(sale.Id, 1, DocDate);

        Assert.Equal(200m, await BalanceAsync(ctx, customer.Id));

        var cash = await ctx.CashAccounts.FirstAsync();

        var payment = new Payment
        {
            Direction = PaymentDirection.In,
            PartyId = customer.Id,
            CashAccountId = cash.Id,
            Method = PaymentMethod.Card,
            Amount = 200m,
            DocDate = DocDate,
            SaleId = sale.Id,
        };

        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();
        await new PaymentPostingService(ctx).PostAsync(payment.Id, 1, DocDate);

        Assert.Equal(0m, await BalanceAsync(ctx, customer.Id));
    }

    [Fact]
    public async Task Part_Paying_A_Supplier_Leaves_The_Remainder_Owed()
    {
        var (ctx, product, unit, location) = await ArrangeAsync();
        var supplier = new Party { Code = "S3", Name = "مورد", IsSupplier = true };
        ctx.Parties.Add(supplier);
        await ctx.SaveChangesAsync();

        var purchase = new Purchase
        {
            SupplierId = supplier.Id,
            LocationId = location,
            DocDate = DocDate,
        };

        purchase.AddItem(product, unit, quantity: 10m, unitPrice: 50m);
        ctx.Purchases.Add(purchase);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(purchase.Id, 1, DocDate);

        var cash = await ctx.CashAccounts.FirstAsync();

        var payment = new Payment
        {
            Direction = PaymentDirection.Out,
            PartyId = supplier.Id,
            CashAccountId = cash.Id,
            Method = PaymentMethod.Cash,
            Amount = 200m,
            DocDate = DocDate,
        };

        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();
        await new PaymentPostingService(ctx).PostAsync(payment.Id, 1, DocDate);

        var balance = await BalanceAsync(ctx, supplier.Id);
        _out.WriteLine($"supplier balance after paying 200 of 500 = {balance}");

        // Still 300 short, and still a payable.
        Assert.Equal(-300m, balance);
    }
}

/// <summary>
/// The payment terms offered when posting: nothing paid, something paid, or all of it. The
/// document always creates the debt; the terms only decide whether a voucher is written too.
/// </summary>
public sealed class SettlementTermsTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"daftar-st-{Guid.NewGuid():N}.db");
    private ServiceProviderFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = await ServiceProviderFixture.CreateAsync(_dbPath);
        using var scope = _fixture.Services.CreateScope();
        await DaftarDatabase.InitializeAsync(
            scope.ServiceProvider.GetRequiredService<DaftarContext>(),
            PasswordHasher.Hash("admin"));
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

    private async Task<(ReceiveViewModel Screen, int SupplierId, int Location)> ArrangeAsync()
    {
        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();

        var supplier = new Party { Code = "S-TERM", Name = "مورد", IsSupplier = true };
        ctx.Parties.Add(supplier);

        var product = new Product("TERM-1", "صنف");
        product.Units.Add(new ProductUnit { Name = "قطعة", Factor = 1m, IsBase = true, IsDefaultPurchase = true });
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();
        product.Units[0].ProductId = product.Id;

        var location = await ctx.StockLocations.Select(l => l.Id).FirstAsync();
        await ctx.SaveChangesAsync();

        var screen = new ReceiveViewModel(_fixture.Services);
        await screen.LoadAsync();

        return (screen, supplier.Id, location);
    }

    private static void AddLine(ReceiveViewModel screen, decimal quantity, string price)
    {
        screen.SelectedProduct = screen.Products.First(p => p.Sku == "TERM-1");
        screen.SelectedUnit = screen.Units.First();
        screen.Quantity = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        screen.UnitPrice = price;
    }

    [Fact]
    public async Task Deferring_Leaves_The_Whole_Supplier_Bill_Outstanding()
    {
        var (screen, _, _) = await ArrangeAsync();

        screen.SelectedSupplier = screen.Suppliers.First(s => s.Name == "مورد");
        AddLine(screen, 10m, "50");
        await screen.AddLineCommand.ExecuteAsync(null);

        screen.Settlement.Choice = screen.Settlement.Choices
            .First(c => c.Value == SettlementChoice.Deferred);

        Assert.Equal(500m, screen.Total);

        await screen.PostCommand.ExecuteAsync(null);

        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();
        var supplier = await ctx.Parties.SingleAsync(p => p.Code == "S-TERM");

        Assert.Equal(-500m, (await ctx.PartyAccountTransactions
            .Where(t => t.PartyId == supplier.Id).ToListAsync()).Sum(t => t.Amount));

        // Nothing was handed over, so no cash moved and no voucher was written.
        Assert.Empty(await ctx.Payments.ToListAsync());
        Assert.Empty(await ctx.CashTransactions.ToListAsync());
    }

    [Fact]
    public async Task Paying_In_Full_Leaves_The_Supplier_Owed_Nothing()
    {
        var (screen, _, _) = await ArrangeAsync();

        screen.SelectedSupplier = screen.Suppliers.First(s => s.Name == "مورد");
        AddLine(screen, 10m, "50");
        await screen.AddLineCommand.ExecuteAsync(null);

        screen.Settlement.Choice = screen.Settlement.Choices
            .First(c => c.Value == SettlementChoice.Full);

        await screen.PostCommand.ExecuteAsync(null);

        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();
        var supplier = await ctx.Parties.SingleAsync(p => p.Code == "S-TERM");

        Assert.Equal(0m, (await ctx.PartyAccountTransactions
            .Where(t => t.PartyId == supplier.Id).ToListAsync()).Sum(t => t.Amount));

        var payment = Assert.Single(await ctx.Payments.ToListAsync());
        Assert.Equal(PaymentDirection.Out, payment.Direction);
        Assert.Equal(500m, payment.Amount);

        // The till really is 500 lighter.
        var cash = Assert.Single(await ctx.CashTransactions.ToListAsync());
        Assert.Equal(-500m, cash.Amount);
    }

    [Fact]
    public async Task Part_Paying_Leaves_The_Remainder_Owed()
    {
        var (screen, _, _) = await ArrangeAsync();

        screen.SelectedSupplier = screen.Suppliers.First(s => s.Name == "مورد");
        AddLine(screen, 10m, "50");
        await screen.AddLineCommand.ExecuteAsync(null);

        screen.Settlement.Choice = screen.Settlement.Choices
            .First(c => c.Value == SettlementChoice.Partial);
        screen.Settlement.PartialAmount = "200";

        await screen.PostCommand.ExecuteAsync(null);

        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();
        var supplier = await ctx.Parties.SingleAsync(p => p.Code == "S-TERM");

        Assert.Equal(-300m, (await ctx.PartyAccountTransactions
            .Where(t => t.PartyId == supplier.Id).ToListAsync()).Sum(t => t.Amount));

        var payment = Assert.Single(await ctx.Payments.ToListAsync());
        Assert.Equal(200m, payment.Amount);
    }

    [Fact]
    public async Task Paying_More_Than_Owed_Is_Capped_RatherThan_Credited()
    {
        var (screen, _, _) = await ArrangeAsync();

        screen.SelectedSupplier = screen.Suppliers.First(s => s.Name == "مورد");
        AddLine(screen, 10m, "50");
        await screen.AddLineCommand.ExecuteAsync(null);

        screen.Settlement.Choice = screen.Settlement.Choices
            .First(c => c.Value == SettlementChoice.Partial);
        screen.Settlement.PartialAmount = "5000";

        Assert.Equal(500m, screen.Settlement.AmountFor(screen.Total));
    }

    [Fact]
    public async Task A_Wallet_Transfer_Cannot_Be_Banked_Into_The_Till()
    {
        var (screen, _, _) = await ArrangeAsync();

        screen.Settlement.Method = screen.Settlement.Methods
            .First(m => m.Value == PaymentMethod.Wallet);

        // The list refreshes as a result of the change; wait for it to settle.
        for (var i = 0; i < 20 && screen.Settlement.Accounts.Count == 0; i++)
            await Task.Delay(25);

        Assert.NotEmpty(screen.Settlement.Accounts);
        Assert.All(screen.Settlement.Accounts, a => Assert.Equal(CashAccountType.Wallet, a.Type));
    }
}
