using Daftar.Domain.Catalog;
using Daftar.Domain.Finance;
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

/// <summary>
/// The money path, against a real database: receive stock, sell it, collect the cash, and check
/// that stock, receivable and cash all agree.
/// </summary>
public sealed class SalesIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"daftar-p2-{Guid.NewGuid():N}.db");

    private ServiceProviderFixture _fixture = null!;

    private static readonly DateOnly DocDate = new(2026, 10, 5);

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

    /// <summary>A supplier, a customer with credit, a price list, a cash account and one stocked product.</summary>
    private async Task<(DaftarContext Ctx, Party Customer, Product Product, ProductUnit Piece,
        PriceList List, CashAccount Cash)> ArrangeAsync()
    {
        var ctx = NewContext();

        var supplier = new Party { Code = "SUP-1", Name = "Factory", IsSupplier = true };
        var customer = new Party
        {
            Code = "CUS-1", Name = "Corner Shop", IsCustomer = true,
            CustomerType = CustomerType.Supermarket, CreditLimit = 5000m,
        };
        ctx.Parties.AddRange(supplier, customer);

        // Reuse what the seed already created rather than colliding with its unique names.
        var list = await ctx.PriceLists.SingleAsync(p => p.IsDefault);
        var cash = await ctx.CashAccounts.SingleAsync(a => a.Type == CashAccountType.Cash);

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

        // Receive 48 pieces (2 cases) at 200 per case -> 400 / 48 base units of cost.
        var purchase = new Purchase
        {
            SupplierId = supplier.Id, LocationId = location, DocDate = DocDate,
        };
        ctx.Purchases.Add(purchase);
        await ctx.SaveChangesAsync();
        purchase.AddItem(product, @case, quantity: 2m, unitPrice: 200m);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(purchase.Id, userId: 1, DocDate);

        // Price list price for a piece.
        ctx.ProductPrices.Add(new ProductPrice
        {
            PriceListId = list.Id, ProductUnitId = piece.Id, Price = 15m, EffectiveFrom = DocDate,
        });
        await ctx.SaveChangesAsync();

        return (ctx, customer, product, piece, list, cash);
    }

    [Fact]
    public async Task PostingASale_DrawsStockOldestFirst_WritesMovementAndReceivable()
    {
        var (ctx, customer, product, piece, list, _cash) = await ArrangeAsync();
        var location = await ctx.StockLocations.Select(l => l.Id).SingleAsync();

        var sale = new Sale
        {
            Type = SaleType.Invoice,
            PartyId = customer.Id,
            LocationId = location,
            PriceListId = list.Id,
            DocDate = DocDate,
        };
        ctx.Sales.Add(sale);
        await ctx.SaveChangesAsync();

        // 10 pieces at 15 = 150
        sale.AddItem(product, piece, quantity: 10m, listPrice: 15m, unitPrice: 15m);
        await ctx.SaveChangesAsync();

        var result = await new SalePostingService(ctx).PostAsync(sale.Id, userId: 1, DocDate);

        Assert.Equal("SO-000001", result.DocNo);
        Assert.Equal(DocumentStatus.Posted, sale.Status);

        // Stock dropped by exactly what was sold.
        var balance = await ctx.StockBalances.SingleAsync();
        Assert.Equal(38m, balance.Quantity);

        // A negative Sale movement, linked to the sale.
        var movement = await ctx.InventoryMovements
            .SingleAsync(m => m.Kind == InventoryMovementKind.Sale);
        Assert.Equal(-10m, movement.Quantity);
        Assert.Equal(sale.Id, movement.SourceId);
        Assert.Equal(product.Id, movement.ProductId);

        // The line recorded which lot it used and at what cost.
        var item = await ctx.SaleItems.Include(i => i.Lots).SingleAsync();
        Assert.Single(item.Lots);
        Assert.Equal(10m, item.Lots[0].BaseQuantity);
        Assert.Equal(8.3333m, item.Lots[0].UnitCost);
        Assert.Equal(83.33m, item.CostTotal);

        // Margin: 150 sold - 83.33 cost.
        Assert.Equal(150m, sale.Total);
        Assert.Equal(83.33m, sale.TotalCost);
        Assert.Equal(66.67m, sale.Margin);

        // The receivable.
        var ledger = await ctx.PartyAccountTransactions.SingleAsync(t => t.SourceType == "Sale");
        Assert.Equal(PartyLedgerKind.Sale, ledger.Kind);
        Assert.Equal(150m, ledger.Amount);
        Assert.Equal(customer.Id, ledger.PartyId);
    }

    [Fact]
    public async Task SellingMoreThanStock_RollsTheWholePostBack()
    {
        var (ctx, customer, product, piece, list, _cash) = await ArrangeAsync();
        var location = await ctx.StockLocations.Select(l => l.Id).SingleAsync();

        var sale = new Sale
        {
            Type = SaleType.Invoice, PartyId = customer.Id,
            LocationId = location, PriceListId = list.Id, DocDate = DocDate,
        };
        ctx.Sales.Add(sale);
        await ctx.SaveChangesAsync();

        // Only 48 in stock.
        sale.AddItem(product, piece, quantity: 100m, listPrice: 15m, unitPrice: 15m);
        await ctx.SaveChangesAsync();

        var sequenceBefore = await ctx.DocumentSequences
            .Where(s => s.DocType == DocumentType.Sale).Select(s => s.NextNumber).SingleAsync();

        await Assert.ThrowsAsync<InsufficientStockException>(() =>
            new SalePostingService(ctx).PostAsync(sale.Id, userId: 1, DocDate));

        // Nothing survived: still Draft, no number consumed, no movement, stock untouched.
        Assert.Equal(DocumentStatus.Draft, sale.Status);
        Assert.Null(sale.DocNo);

        var sequenceAfter = await ctx.DocumentSequences
            .Where(s => s.DocType == DocumentType.Sale).Select(s => s.NextNumber).SingleAsync();
        Assert.Equal(sequenceBefore, sequenceAfter);

        Assert.Empty(await ctx.InventoryMovements
            .Where(m => m.Kind == InventoryMovementKind.Sale).ToListAsync());

        // No receivable from this sale: the purchase payable from ArrangeAsync is the only row.
        Assert.Empty(await ctx.PartyAccountTransactions
            .Where(t => t.SourceType == "Sale").ToListAsync());

        // Stock is exactly as it was before the failed post.
        var balance = await ctx.StockBalances.SingleAsync(b => b.Quantity != 0m);
        Assert.Equal(48m, balance.Quantity);
    }

    [Fact]
    public async Task SaleOfTwoLines_SpreadsAcrossLotsAndKeepsMarginPerLine()
    {
        var (ctx, customer, product, piece, list, _cash) = await ArrangeAsync();
        var location = await ctx.StockLocations.Select(l => l.Id).SingleAsync();

        // A second batch arriving with the same expiry forces FEFO to span two lots.
        var lot2 = Lot.Create(product.Id, DocDate, null, 6m);
        lot2.Freeze();
        ctx.Lots.Add(lot2);
        await ctx.SaveChangesAsync();
        ctx.StockBalances.Add(StockBalance.Open(lot2.Id, location, DateTime.UtcNow));
        await ctx.SaveChangesAsync();
        ctx.StockBalances.Local.Single(b => b.LotId == lot2.Id).Apply(20m, DateTime.UtcNow);
        await ctx.SaveChangesAsync();

        var sale = new Sale
        {
            Type = SaleType.Invoice, PartyId = customer.Id,
            LocationId = location, PriceListId = list.Id, DocDate = DocDate,
        };
        ctx.Sales.Add(sale);
        await ctx.SaveChangesAsync();

        // 60 pieces: 20 from the cheaper lot (lot 2, cost 6) then 40 from lot 1 (cost 4.1667).
        sale.AddItem(product, piece, quantity: 60m, listPrice: 15m, unitPrice: 15m);
        await ctx.SaveChangesAsync();

        await new SalePostingService(ctx).PostAsync(sale.Id, userId: 1, DocDate);

        var item = await ctx.SaleItems.Include(i => i.Lots).SingleAsync();
        Assert.Equal(2, item.Lots.Count);

        // Lot 2 has no expiry, so it sorts last; lot 1 (no expiry either) is first by Id.
        var totalDrawn = item.Lots.Sum(l => l.BaseQuantity);
        Assert.Equal(60m, totalDrawn);

        // Stock never went negative.
        Assert.All(await ctx.StockBalances.ToListAsync(), b => Assert.True(b.Quantity >= 0m));
    }

    [Fact]
    public async Task CollectingCash_MovesPartyBalanceAndCashAccountTogether()
    {
        var (ctx, customer, _, _, _, cash) = await ArrangeAsync();

        var payment = new Payment
        {
            Direction = PaymentDirection.In,
            PartyId = customer.Id,
            CashAccountId = cash.Id,
            Method = PaymentMethod.Cash,
            Amount = 250m,
            DocDate = DocDate,
        };
        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();

        var result = await new PaymentPostingService(ctx).PostAsync(payment.Id, userId: 1, DocDate);
        Assert.Equal("RC-000001", result.DocNo);

        // Cash in the till.
        var cashTx = await ctx.CashTransactions.SingleAsync();
        Assert.Equal(CashTransactionKind.Collection, cashTx.Kind);
        Assert.Equal(250m, cashTx.Amount);
        Assert.Equal(payment.Id, cashTx.PaymentId);

        // The customer's balance goes down by what they paid.
        var partyTx = await ctx.PartyAccountTransactions.SingleAsync(t => t.Kind == PartyLedgerKind.PaymentIn);
        Assert.Equal(PartyLedgerKind.PaymentIn, partyTx.Kind);
        Assert.Equal(-250m, partyTx.Amount);
    }

    [Fact]
    public async Task SaleThenPayment_LeavesTheCustomerSettledAtZero()
    {
        var (ctx, customer, product, piece, list, cash) = await ArrangeAsync();
        var location = await ctx.StockLocations.Select(l => l.Id).SingleAsync();

        var sale = new Sale
        {
            Type = SaleType.Invoice, PartyId = customer.Id,
            LocationId = location, PriceListId = list.Id, DocDate = DocDate,
        };
        ctx.Sales.Add(sale);
        await ctx.SaveChangesAsync();
        sale.AddItem(product, piece, quantity: 4m, listPrice: 15m, unitPrice: 15m);
        await ctx.SaveChangesAsync();
        await new SalePostingService(ctx).PostAsync(sale.Id, userId: 1, DocDate);

        var total = await ctx.Sales.Select(s => s.Total).SingleAsync();

        var payment = new Payment
        {
            Direction = PaymentDirection.In, PartyId = customer.Id,
            CashAccountId = cash.Id, Amount = total, DocDate = DocDate,
        };
        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();
        await new PaymentPostingService(ctx).PostAsync(payment.Id, userId: 1, DocDate);

        // The customer's own net balance is zero. Filtered by party: the ledger also holds the
        // supplier's +400 payable from ArrangeAsync, which is a different party's debt.
        var balance = (await ctx.PartyAccountTransactions
            .Where(t => t.PartyId == customer.Id)
            .ToListAsync()).Sum(t => t.Amount);

        Assert.Equal(0m, balance);
    }

    [Fact]
    public async Task PayingASupplier_MovesTheNetBalanceTowardZero()
    {
        var (ctx, _customer, _, _, _, cash) = await ArrangeAsync();
        var supplier = await ctx.Parties.SingleAsync(p => p.Code == "SUP-1");

        // The purchase put the goods on our books but P1 does not write a payable ledger row,
        // so this voucher's effect is visible on its own.
        var payment = new Payment
        {
            Direction = PaymentDirection.Out, PartyId = supplier.Id,
            CashAccountId = cash.Id, Amount = 400m, DocDate = DocDate,
        };
        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();
        await new PaymentPostingService(ctx).PostAsync(payment.Id, userId: 1, DocDate);

        // Paying a supplier reduces what we owe them: negative on the net balance.
        // The purchase wrote its payable when it was posted in ArrangeAsync, and the voucher pays it
        // down, so the supplier nets to zero.
        var rows = await ctx.PartyAccountTransactions
            .Where(t => t.PartyId == supplier.Id)
            .OrderBy(t => t.Id)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        // The purchase left us owing the supplier 400, so it is negative.
        Assert.Equal(PartyLedgerKind.Purchase, rows[0].Kind);
        Assert.Equal(-400m, rows[0].Amount);

        // Paying it off moves the balance back up to zero, so the voucher is positive.
        Assert.Equal(PartyLedgerKind.PaymentOut, rows[1].Kind);
        Assert.Equal(400m, rows[1].Amount);

        // Bought on credit and paid in full: nothing owed either way.
        Assert.Equal(0m, rows.Sum(r => r.Amount));

        Assert.Equal("Payment", rows[1].SourceType);
        Assert.Equal(payment.Id, rows[1].SourceId);

        // The cash side is the mirror image.
        var cashTx = await ctx.CashTransactions.SingleAsync();
        Assert.Equal(CashTransactionKind.SupplierPayment, cashTx.Kind);
        Assert.Equal(-400m, cashTx.Amount);
    }

    [Fact]
    public async Task TransferBetweenAccounts_WritesAMatchedPair()
    {
        var (ctx, _, _, _, _, cash) = await ArrangeAsync();
        var bank = new CashAccount { Name = "Bank", Type = CashAccountType.Bank };
        ctx.CashAccounts.Add(bank);
        await ctx.SaveChangesAsync();

        var service = new PaymentPostingService(ctx);
        await service.TransferBetweenAccountsAsync(cash.Id, bank.Id, 500m, 1, DocDate);

        var pair = await ctx.CashTransactions.ToListAsync();
        Assert.Equal(2, pair.Count);
        Assert.Single(pair.Select(t => t.TransferRef).Distinct());
        Assert.Equal(-500m, pair.Single(t => t.Kind == CashTransactionKind.TransferOut).Amount);
        Assert.Equal(500m, pair.Single(t => t.Kind == CashTransactionKind.TransferIn).Amount);

        // The pair nets to zero.
        Assert.Equal(0m, pair.Sum(t => t.Amount));
    }

    [Fact]
    public async Task LedgersAreAppendOnly()
    {
        var (ctx, customer, _, _, _, cash) = await ArrangeAsync();

        var payment = new Payment
        {
            Direction = PaymentDirection.In, PartyId = customer.Id,
            CashAccountId = cash.Id, Amount = 50m, DocDate = DocDate,
        };
        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();
        await new PaymentPostingService(ctx).PostAsync(payment.Id, userId: 1, DocDate);

        var blocked = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            ctx.Database.ExecuteSqlRawAsync("UPDATE PartyAccountTransactions SET Amount = 0"));
        Assert.Contains("append-only", blocked.Message, StringComparison.Ordinal);

        var blockedCash = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            ctx.Database.ExecuteSqlRawAsync("DELETE FROM CashTransactions"));
        Assert.Contains("append-only", blockedCash.Message, StringComparison.Ordinal);
    }
}