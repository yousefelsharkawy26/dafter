using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Domain.Catalog;
using Daftar.Domain.Finance;
using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Sales;
using Daftar.Domain.Security;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// <c>Party.CreditLimit</c> was stored, edited behind <c>PartiesEditCredit</c>, shown on the
/// parties screen and read by nobody. A repo-wide search found <c>CanTakeCredit</c> defined and
/// never called, so a customer with a 500 limit could be invoiced for anything and the number on
/// screen was a note rather than a rule.
///
/// The check runs after the voucher, because the invoice is posted first and the voucher
/// references it. Mid-transaction the balance is at its peak, and a cash sale would be refused
/// for headroom the customer never used.
/// </summary>
public sealed class CreditLimitTests : IAsyncLifetime, IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 7);

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"daftar-credit-{Guid.NewGuid():N}.db");

    private ServiceProvider _services = null!;

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

    // ---------- arrangement ----------

    private int _locationId;
    private int _productId;
    private int _unitId;
    private int _listId;
    private int _cashId;
    private bool _stocked;

    private async Task EnsureStockAsync()
    {
        if (_stocked) return;
        _stocked = true;

        var ctx = NewContext();

        var supplier = new Party { Code = "SUP-C", Name = "Supplier", IsSupplier = true };
        ctx.Parties.Add(supplier);

        var product = new Product("CRED-1", "Creditable");
        var unit = new ProductUnit { Name = "piece", Factor = 1m, IsBase = true };
        product.Units.Add(unit);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        _productId = product.Id;
        _unitId = unit.Id;
        _listId = await ctx.PriceLists.SingleAsync(p => p.IsDefault).ContinueWith(t => t.Result.Id);
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
        purchase.AddItem(product, unit, 500m, 5m);
        await ctx.SaveChangesAsync();

        await new PurchasePostingService(ctx).PostAsync(purchase.Id, 1, Today);
    }

    private async Task<Party> MakeCustomerAsync(decimal creditLimit, decimal quantity, decimal unitPrice)
    {
        await EnsureStockAsync();
        var ctx = NewContext();

        var customer = new Party
        {
            Code = $"C-{Guid.NewGuid():N}"[..10],
            Name = "Buyer",
            IsCustomer = true,
            CustomerType = CustomerType.Retail,
            CreditLimit = creditLimit,
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
        sale.AddItem(product, unit, quantity, unitPrice, unitPrice);
        sale.Recalculate();
        await ctx.SaveChangesAsync();

        await new SalePostingService(ctx).PostAsync(sale.Id, 1, Today);
        return customer;
    }

    private async Task PayAsync(int partyId, decimal amount, int saleId)
    {
        var ctx = NewContext();
        var payment = new Payment
        {
            Direction = PaymentDirection.In,
            PartyId = partyId,
            CashAccountId = _cashId,
            Amount = amount,
            SaleId = saleId,
            DocDate = Today,
        };
        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();
        await new PaymentPostingService(ctx).PostAsync(payment.Id, 1, Today);
    }

    private async Task<(int SaleId, Sale Sale)> LastSaleForAsync(int partyId)
    {
        var ctx = NewContext();
        var sale = await ctx.Sales
            .Where(s => s.PartyId == partyId && s.Status == DocumentStatus.Posted)
            .OrderByDescending(s => s.Id)
            .FirstAsync();
        return (sale.Id, sale);
    }

    // ---------- the rule ----------

    [Fact]
    public async Task A_Customer_Past_His_Limit_Is_Refused()
    {
        var customer = await MakeCustomerAsync(creditLimit: 100m, quantity: 5m, unitPrice: 40m);
        var (saleId, sale) = await LastSaleForAsync(customer.Id);

        Assert.Equal(200m, sale.Total);

        var ctx = NewContext();
        var ex = await Assert.ThrowsAsync<Domain.Shared.DomainException>(() =>
            new SalePostingService(ctx).EnsureCreditAllowsAsync(saleId));

        Assert.Contains("Buyer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Cash_Sale_Is_Allowed_Whatever_The_Limit_Is()
    {
        // The whole reason the check sits after the voucher. A cash-only customer paying cash owes
        // nothing, so refusing them for headroom they never used would break the till.
        var customer = await MakeCustomerAsync(creditLimit: 0m, quantity: 5m, unitPrice: 40m);
        var (saleId, _) = await LastSaleForAsync(customer.Id);

        await PayAsync(customer.Id, 200m, saleId);

        var ctx = NewContext();
        await new SalePostingService(ctx).EnsureCreditAllowsAsync(saleId);

        var balance = await ctx.PartyAccountTransactions
            .Where(t => t.PartyId == customer.Id)
            .SumAsync(t => (decimal?)t.Amount);

        Assert.Equal(0m, balance);
    }

    [Fact]
    public async Task An_Existing_Balance_Counts_Towards_The_Limit()
    {
        // The rule is about headroom, not about the size of this one invoice. Comparing the sale
        // alone would let a customer already at his limit keep buying.
        var customer = await MakeCustomerAsync(creditLimit: 500m, quantity: 5m, unitPrice: 40m);
        Assert.Equal(200m, await LedgerBalanceAsync(customer.Id));

        // He owes 200. A second sale of 400 would put him at 600.
        var ctx = NewContext();
        var product = await ctx.Products.Include(p => p.Units).FirstAsync(p => p.Id == _productId);
        var unit = product.Units.First(u => u.Id == _unitId);

        var second = new Sale
        {
            Type = SaleType.Invoice,
            PartyId = customer.Id,
            LocationId = _locationId,
            PriceListId = _listId,
            DocDate = Today,
        };
        ctx.Sales.Add(second);
        await ctx.SaveChangesAsync();
        second.AddItem(product, unit, 10m, 40m, 40m);
        second.Recalculate();
        await ctx.SaveChangesAsync();
        await new SalePostingService(ctx).PostAsync(second.Id, 1, Today);

        var ex = await Assert.ThrowsAsync<Domain.Shared.DomainException>(() =>
            new SalePostingService(ctx).EnsureCreditAllowsAsync(second.Id));

        Assert.Contains("600", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Partial_Payment_Leaves_Only_The_Remaining_Balance_Counting()
    {
        // 500 of goods, 200 paid: 300 left owing, inside a 400 limit. Judging the invoice total
        // instead would have refused a sale the customer had largely paid for.
        var customer = await MakeCustomerAsync(creditLimit: 400m, quantity: 5m, unitPrice: 40m);
        var (saleId, _) = await LastSaleForAsync(customer.Id);

        await PayAsync(customer.Id, 200m, saleId);

        var ctx = NewContext();
        await new SalePostingService(ctx).EnsureCreditAllowsAsync(saleId);
    }

    [Fact]
    public async Task A_Blocked_Customer_Cannot_Buy_At_All()
    {
        var customer = await MakeCustomerAsync(creditLimit: 1_000_000m, quantity: 1m, unitPrice: 10m);
        var (saleId, _) = await LastSaleForAsync(customer.Id);

        var ctx = NewContext();
        var tracked = await ctx.Parties.FirstAsync(p => p.Id == customer.Id);
        tracked.Status = PartyStatus.Blocked;
        await ctx.SaveChangesAsync();

        var fresh = NewContext();
        var ex = await Assert.ThrowsAsync<Domain.Shared.DomainException>(() =>
            new SalePostingService(fresh).EnsureCreditAllowsAsync(saleId));

        Assert.Contains("مش نشط", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing owing is not a licence to trade with an account the business has stopped trading with,
    /// which is why the status gate is judged before the balance and not after it.
    /// </summary>
    [Fact]
    public async Task A_Blocked_Customer_Cannot_Buy_For_Cash_Either()
    {
        var customer = await MakeCustomerAsync(creditLimit: 0m, quantity: 1m, unitPrice: 10m);
        var (saleId, _) = await LastSaleForAsync(customer.Id);

        // Settled in full, so the credit limit is not in question and the balance is zero.
        await PayAsync(customer.Id, 10m, saleId);

        var ctx = NewContext();
        var tracked = await ctx.Parties.FirstAsync(p => p.Id == customer.Id);
        tracked.Status = PartyStatus.Blocked;
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<Domain.Shared.DomainException>(() =>
            new SalePostingService(NewContext()).EnsureCreditAllowsAsync(saleId));

        Assert.Contains("مش نشط", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pending means "cash only until the manager approves". The balance is what decides which of the
    /// two a sale is: owing anything is the unapproved credit, owing nothing is the cash sale the
    /// status allows. Before this, every pending account was refused for any amount, which made the
    /// customers the settle wizard creates in the field ones it could never invoice.
    /// </summary>
    [Fact]
    public async Task A_Pending_Customer_May_Buy_For_Cash_But_Not_On_Credit()
    {
        var customer = await MakeCustomerAsync(creditLimit: 100m, quantity: 1m, unitPrice: 10m);
        var (saleId, _) = await LastSaleForAsync(customer.Id);

        var ctx = NewContext();
        var tracked = await ctx.Parties.FirstAsync(p => p.Id == customer.Id);
        tracked.Status = PartyStatus.Pending;
        await ctx.SaveChangesAsync();

        // The 10 on the ledger is credit the manager has not approved yet.
        var credit = await Assert.ThrowsAsync<Domain.Shared.DomainException>(() =>
            new SalePostingService(NewContext()).EnsureCreditAllowsAsync(saleId));

        Assert.Contains("موافقة المدير", credit.Message, StringComparison.Ordinal);

        // Paying it off leaves nothing owing, and a pending account may do that.
        await PayAsync(customer.Id, 10m, saleId);
        await new SalePostingService(NewContext()).EnsureCreditAllowsAsync(saleId);
    }

    private async Task<decimal> LedgerBalanceAsync(int partyId)
    {
        var ctx = NewContext();
        return await ctx.PartyAccountTransactions
            .Where(t => t.PartyId == partyId)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
    }
}
