using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Domain.Finance;
using Daftar.Domain.Parties;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// البيع with payment terms. Selling always makes the customer owe us; the terms only decide
/// whether a receipt voucher is written alongside it, and how much of the sale is left open.
/// </summary>
public sealed class SellSettlementTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"daftar-sell-{Guid.NewGuid():N}.db");
    private ServiceProviderFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = await ServiceProviderFixture.CreateAsync(_dbPath);

        using var scope = _fixture.Services.CreateScope();
        await DaftarDatabase.InitializeAsync(
            scope.ServiceProvider.GetRequiredService<DaftarContext>(),
            PasswordHasher.Hash("admin"));

        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        ctx.Parties.Add(new Party
        {
            Code = "C-SELL",
            Name = "عميل بيع",
            IsCustomer = true,
            CustomerType = CustomerType.Retail,
        });

        var product = new Domain.Catalog.Product("SELL-1", "صنف بيع");
        product.Units.Add(new Domain.Catalog.ProductUnit
        {
            Name = "قطعة",
            Factor = 1m,
            IsBase = true,
            IsDefaultPurchase = true,
        });

        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        // The till prices from the price list, so an unpriced product would never reach the cart.
        var list = await ctx.PriceLists.FirstAsync();

        ctx.ProductPrices.Add(new Domain.Sales.ProductPrice
        {
            PriceListId = list.Id,
            ProductUnitId = product.Units[0].Id,
            Price = 25m,
            EffectiveFrom = new DateOnly(2026, 1, 1),
        });

        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync()
    { _fixture.Dispose(); return Task.CompletedTask; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Brings stock in through الاستلام, the same path the app uses, so the till has something real
    /// to sell rather than a balance invented for the test.
    /// </summary>
    private async Task ReceiveStockAsync(decimal quantity)
    {
        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();

        var supplier = new Party { Code = "S-SUP", Name = "مورد بيع", IsSupplier = true };
        ctx.Parties.Add(supplier);
        await ctx.SaveChangesAsync();

        var receive = new ReceiveViewModel(_fixture.Services);
        await receive.LoadAsync();

        receive.SelectedSupplier = receive.Suppliers.First(s => s.Name == "مورد بيع");
        receive.SelectedProduct = receive.Products.First(p => p.Sku == "SELL-1");
        receive.SelectedUnit = receive.Units.First();
        receive.Quantity = quantity.ToString(CultureInfo.InvariantCulture);
        receive.UnitPrice = "15";

        await receive.AddLineCommand.ExecuteAsync(null);
        await receive.PostCommand.ExecuteAsync(null);
    }

    private async Task<SellViewModel> ArrangeAsync()
    {
        await ReceiveStockAsync(100m);

        var vm = new SellViewModel(_fixture.Services);
        await vm.LoadAsync();

        vm.SelectedProduct = vm.Products.First(p => p.Sku == "SELL-1");
        vm.Quantity = 4m;

        vm.AddToCartCommand.Execute(null);

        vm.SelectedCustomer = vm.Customers.First(c => c.Name == "عميل بيع");

        return vm;
    }

    private static async Task WaitForAsync(Func<bool> until)
    {
        for (var i = 0; i < 40 && !until(); i++)
            await Task.Delay(25);
    }

    private async Task<decimal> BalanceAsync()
    {
        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();
        var party = await ctx.Parties.SingleAsync(p => p.Code == "C-SELL");

        return (await ctx.PartyAccountTransactions.Where(t => t.PartyId == party.Id).ToListAsync())
            .Sum(t => t.Amount);
    }

    [Fact]
    public async Task A_Sale_Always_Makes_The_Customer_Owe_Us_Even_When_Paid_In_Full()
    {
        var vm = await ArrangeAsync();
        Assert.Equal(100m, vm.Total);

        vm.Settlement.Choice = vm.Settlement.Choices
            .First(c => c.Value == SettlementChoice.Full);

        await vm.PostCommand.ExecuteAsync(null);

        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();

        // The sale and the receipt cancel out; the customer owes nothing afterwards.
        Assert.Equal(0m, await BalanceAsync());

        var payment = Assert.Single(await ctx.Payments.ToListAsync());
        Assert.Equal(PaymentDirection.In, payment.Direction);
        Assert.Equal(100m, payment.Amount);

        var cash = Assert.Single(await ctx.CashTransactions.ToListAsync());
        Assert.Equal(100m, cash.Amount);
    }

    [Fact]
    public async Task An_Open_Sale_Leaves_The_Customer_Owing_The_Whole_Amount()
    {
        var vm = await ArrangeAsync();

        vm.Settlement.Choice = vm.Settlement.Choices
            .First(c => c.Value == SettlementChoice.Deferred);

        await vm.PostCommand.ExecuteAsync(null);

        Assert.Equal(100m, await BalanceAsync());

        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();
        Assert.Empty(await ctx.Payments.ToListAsync());
        Assert.Empty(await ctx.CashTransactions.ToListAsync());
    }

    [Fact]
    public async Task Part_Paying_A_Sale_Leaves_The_Remainder_Receivable()
    {
        var vm = await ArrangeAsync();

        vm.Settlement.Choice = vm.Settlement.Choices
            .First(c => c.Value == SettlementChoice.Partial);
        vm.Settlement.PartialAmount = "40";

        await vm.PostCommand.ExecuteAsync(null);

        Assert.Equal(60m, await BalanceAsync());

        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();
        Assert.Equal(40m, Assert.Single(await ctx.Payments.ToListAsync()).Amount);
    }

    [Fact]
    public async Task The_Amount_Due_Follows_The_Terms_Rather_Than_Typed_Text()
    {
        var vm = await ArrangeAsync();

        vm.Settlement.Choice = vm.Settlement.Choices
            .First(c => c.Value == SettlementChoice.Partial);
        vm.Settlement.PartialAmount = "40";

        Assert.Equal(60m, vm.AmountDue);
    }

    [Fact]
    public async Task A_Wallet_Sale_Can_Only_Be_Banked_Into_A_Wallet_Account()
    {
        var vm = await ArrangeAsync();

        vm.Settlement.Choice = vm.Settlement.Choices
            .First(c => c.Value == SettlementChoice.Full);
        vm.Settlement.Method = vm.Settlement.Methods
            .First(m => m.Value == PaymentMethod.Wallet);

        await WaitForAsync(() => vm.Settlement.Accounts.Count > 0);

        Assert.NotEmpty(vm.Settlement.Accounts);
        Assert.All(vm.Settlement.Accounts, a => Assert.Equal(CashAccountType.Wallet, a.Type));
    }

    [Fact]
    public async Task Paying_More_Than_The_Sale_Is_Capped_RatherThan_Banked_As_A_Credit()
    {
        var vm = await ArrangeAsync();

        vm.Settlement.Choice = vm.Settlement.Choices
            .First(c => c.Value == SettlementChoice.Partial);
        vm.Settlement.PartialAmount = "9999";

        Assert.Equal(100m, vm.Settlement.AmountFor(vm.Total));
    }

    [Fact]
    public async Task Clearing_The_Cart_Also_Clears_The_Terms()
    {
        var vm = await ArrangeAsync();

        vm.Settlement.Choice = vm.Settlement.Choices
            .First(c => c.Value == SettlementChoice.Partial);
        vm.Settlement.PartialAmount = "40";

        vm.ClearCommand.Execute(null);

        Assert.Equal(SettlementChoice.Deferred, vm.Settlement.Settlement);
        Assert.Equal(string.Empty, vm.Settlement.PartialAmount);
        Assert.Equal(0m, vm.Total);
    }
}
