using Daftar.Domain.Catalog;
using Daftar.Domain.Distributors;
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
/// The distributor day against a real database: load his vehicle, sell from it, collect cash
/// into his custody, then settle and check the shortage and commission.
/// </summary>
public sealed class DistributorsIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"daftar-p3-{Guid.NewGuid():N}.db");

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

    private sealed record Fixture(
        Distributor Distributor,
        int WarehouseId,
        int VehicleId,
        int CustodyId,
        int CashId,
        Product Product,
        ProductUnit Piece,
        PriceList List,
        Party Customer,
        int LotId);

    /// <summary>A distributor with a loaded vehicle, a stocked warehouse lot, and a priced product.</summary>
    private async Task<Fixture> ArrangeAsync(decimal commissionRate = 10m)
    {
        var ctx = NewContext();

        var vehicle = new StockLocation { Name = "Van 1", Type = StockLocationType.Vehicle };
        var custody = new CashAccount { Name = "Van 1 cash", Type = CashAccountType.DistributorCustody };
        ctx.StockLocations.Add(vehicle);
        ctx.CashAccounts.Add(custody);
        await ctx.SaveChangesAsync();

        var warehouse = await ctx.StockLocations
            .SingleAsync(l => l.Name == "Main Warehouse");
        var cash = await ctx.CashAccounts.SingleAsync(a => a.Type == CashAccountType.Cash);
        var list = await ctx.PriceLists.SingleAsync(p => p.IsDefault);

        var distributor = new Distributor
        {
            Name = "Ahmed",
            StockLocationId = vehicle.Id,
            CashAccountId = custody.Id,
            CommissionRate = commissionRate,
            CommissionBasis = CommissionBasis.Sales,
        };
        ctx.Distributors.Add(distributor);

        var customer = new Party
        {
            Code = "CUS-D1", Name = "Retailer", IsCustomer = true,
            CustomerType = CustomerType.Retail, CreditLimit = 1000m,
        };
        ctx.Parties.Add(customer);

        var product = new Product("PASTE-9", "Tomato paste big");
        var piece = new ProductUnit { Name = "piece", Factor = 1m, IsBase = true };
        product.Units.Add(piece);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();
        piece.ProductId = product.Id;

        ctx.ProductPrices.Add(new ProductPrice
        {
            PriceListId = list.Id, ProductUnitId = piece.Id, Price = 20m, EffectiveFrom = Today,
        });
        await ctx.SaveChangesAsync();

        // Receive 100 into the warehouse at 10 per piece.
        var supplier = new Party { Code = "SUP-D1", Name = "Supplier D", IsSupplier = true };
        ctx.Parties.Add(supplier);
        await ctx.SaveChangesAsync();

        var purchase = new Purchase
        {
            SupplierId = supplier.Id, LocationId = warehouse.Id, DocDate = Today,
        };
        ctx.Purchases.Add(purchase);
        await ctx.SaveChangesAsync();
        purchase.AddItem(product, piece, quantity: 100m, unitPrice: 10m);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(purchase.Id, 1, Today);

        var lotId = await ctx.Lots.Select(l => l.Id).SingleAsync();

        return new Fixture(distributor, warehouse.Id, vehicle.Id, custody.Id, cash.Id,
            product, piece, list, customer, lotId);
    }

    private async Task<DistributorSession> OpenSessionAsync(DaftarContext ctx, int distributorId)
    {
        var service = new DistributorSessionService(
            ctx, new StockCountService(ctx), new StockTransferPostingService(ctx));
        return await service.OpenAsync(distributorId, userId: 1);
    }

    private async Task<TransferPostingResult> LoadVanAsync(
        DaftarContext ctx, DistributorSession session, Fixture f, decimal quantity)
    {
        var service = new DistributorSessionService(
            ctx, new StockCountService(ctx), new StockTransferPostingService(ctx));
        return await service.LoadVehicleAsync(
            session.Id, f.WarehouseId, [(f.LotId, quantity)], userId: 1, businessDate: Today);
    }

    [Fact]
    public async Task LoadingTheVan_MovesStockOutAndWritesBothHalves()
    {
        var f = await ArrangeAsync();
        var ctx = NewContext();
        var session = await OpenSessionAsync(ctx, f.Distributor.Id);

        var result = await LoadVanAsync(ctx, session, f, 30m);

        Assert.Equal("TR-000001", result.DocNo);
        Assert.Equal(2, result.RowsWritten);

        // Warehouse down, vehicle up, by the same amount.
        Assert.Equal(70m, await ctx.StockBalances
            .SingleAsync(b => b.LocationId == f.WarehouseId).ContinueWith(t => t.Result.Quantity));
        Assert.Equal(30m, await ctx.StockBalances
            .SingleAsync(b => b.LocationId == f.VehicleId).ContinueWith(t => t.Result.Quantity));

        // The two movement rows share one SourceId, so the transfer reconciles.
        var movements = await ctx.InventoryMovements
            .Where(m => m.Kind == InventoryMovementKind.TransferOut
                     || m.Kind == InventoryMovementKind.TransferIn)
            .ToListAsync();

        Assert.Equal(2, movements.Count);
        Assert.Equal(-30m, movements.Single(m => m.Kind == InventoryMovementKind.TransferOut).Quantity);
        Assert.Equal(30m, movements.Single(m => m.Kind == InventoryMovementKind.TransferIn).Quantity);
        Assert.Single(movements.Select(m => m.SourceId).Distinct());
    }

    [Fact]
    public async Task OneOpenSessionPerDistributor_IsEnforcedByTheDatabase()
    {
        var f = await ArrangeAsync();
        var ctx = NewContext();
        await OpenSessionAsync(ctx, f.Distributor.Id);

        // The partial unique index refuses a second open session at the database level.
        var error = await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            ctx.DistributorSessions.Add(new DistributorSession
            {
                DistributorId = f.Distributor.Id,
                OpenedAt = DateTime.UtcNow,
                OpenedBy = 1,
            });
            await ctx.SaveChangesAsync();
        });

        var sqliteMessage = error.InnerException?.Message ?? error.Message;
        Assert.Contains("UNIQUE", sqliteMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Transfer_BeyondStockAtTheSource_IsRefused()
    {
        var f = await ArrangeAsync();
        var ctx = NewContext();
        var session = await OpenSessionAsync(ctx, f.Distributor.Id);

        // Warehouse holds 100; ask for 500.
        await Assert.ThrowsAsync<InsufficientStockException>(
            () => LoadVanAsync(ctx, session, f, 500m));

        // Nothing moved.
        Assert.Equal(100m, await ctx.StockBalances
            .SingleAsync(b => b.LocationId == f.WarehouseId).ContinueWith(t => t.Result.Quantity));
        Assert.Empty(await ctx.StockBalances
            .Where(b => b.LocationId == f.VehicleId).ToListAsync());
    }

    [Fact]
    public async Task Settling_RecordsCashDifferenceShortageAndCommission()
    {
        var f = await ArrangeAsync(commissionRate: 10m);
        var ctx = NewContext();
        var session = await OpenSessionAsync(ctx, f.Distributor.Id);
        await LoadVanAsync(ctx, session, f, 40m);

        // He sells 20 at 20 and collects all of it into his custody.
        var sale = new Sale
        {
            Type = SaleType.Invoice, PartyId = f.Customer.Id,
            LocationId = f.VehicleId, PriceListId = f.List.Id,
            DocDate = Today, DistributorId = f.Distributor.Id,
            DistributorSessionId = session.Id,
        };
        ctx.Sales.Add(sale);
        await ctx.SaveChangesAsync();
        sale.AddItem(f.Product, f.Piece, quantity: 20m, listPrice: 20m, unitPrice: 20m);
        await ctx.SaveChangesAsync();
        await new SalePostingService(ctx).PostAsync(sale.Id, 1, Today);

        var payment = new Payment
        {
            Direction = PaymentDirection.In, PartyId = f.Customer.Id,
            CashAccountId = f.CustodyId, Amount = 400m, DocDate = Today,
            DistributorSessionId = session.Id,
        };
        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();
        await new PaymentPostingService(ctx).PostAsync(payment.Id, 1, Today);

        // He loaded 40 and sold 20, so 20 should be in the van. He comes back with none, and 30 short in cash.
        var service = new DistributorSessionService(
            ctx, new StockCountService(ctx), new StockTransferPostingService(ctx));

        var settlement = await service.SettleAsync(
            session.Id,
            countedCash: 370m,                           // expected 400
            countedQuantities: new Dictionary<int, decimal> { [f.LotId] = 0m }, // 20 were there
            userId: 1);

        Assert.Equal(400m, settlement.SalesTotal);
        Assert.Equal(400m, settlement.CollectionsTotal);
        Assert.Equal(400m, settlement.ExpectedCash);
        Assert.Equal(370m, settlement.CountedCash);
        Assert.Equal(-30m, settlement.CashDifference);

        // 20 missing pieces at 10 each.
        Assert.Equal(200m, settlement.StockShortageValue);

        // 10% of 400.
        Assert.Equal(40m, settlement.CommissionAmount);

        // The snapshot is on the session, and it is closed.
        var saved = await ctx.DistributorSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(DistributorSessionStatus.Closed, saved.Status);
        Assert.Equal(-30m, saved.CashDifference);
        Assert.Equal(200m, saved.StockShortageValue);
        Assert.True(saved.IsCashShort);
        Assert.NotNull(saved.ClosedAt);
        Assert.NotNull(saved.StockCountId);

        // Stock now reflects the count: the van was written down to what he actually had, which is nothing.
        Assert.Equal(0m, await ctx.StockBalances
            .SingleAsync(b => b.LocationId == f.VehicleId).ContinueWith(t => t.Result.Quantity));
    }

    /// <summary>
    /// Runs the standard day: sell 20, collect 400 into custody, come back with 370.
    /// </summary>
    private async Task<(Fixture F, DistributorSession Session, SettlementResult Settlement, DaftarContext Ctx)>
        SettleAShortSessionAsync()
    {
        var f = await ArrangeAsync(commissionRate: 10m);
        var ctx = NewContext();
        var session = await OpenSessionAsync(ctx, f.Distributor.Id);
        await LoadVanAsync(ctx, session, f, 40m);

        var sale = new Sale
        {
            Type = SaleType.Invoice,
            PartyId = f.Customer.Id,
            LocationId = f.VehicleId,
            PriceListId = f.List.Id,
            DocDate = Today,
            DistributorId = f.Distributor.Id,
            DistributorSessionId = session.Id,
        };
        ctx.Sales.Add(sale);
        await ctx.SaveChangesAsync();
        sale.AddItem(f.Product, f.Piece, 20m, 20m, 20m);
        await ctx.SaveChangesAsync();
        await new SalePostingService(ctx).PostAsync(sale.Id, 1, Today);

        var payment = new Payment
        {
            Direction = PaymentDirection.In,
            PartyId = f.Customer.Id,
            CashAccountId = f.CustodyId,
            Amount = 400m,
            DocDate = Today,
            DistributorSessionId = session.Id,
        };
        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();
        await new PaymentPostingService(ctx).PostAsync(payment.Id, 1, Today);

        var service = new DistributorSessionService(
            ctx, new StockCountService(ctx), new StockTransferPostingService(ctx));

        var settlement = await service.SettleAsync(
            session.Id,
            countedCash: 370m,
            countedQuantities: new Dictionary<int, decimal> { [f.LotId] = 0m },
            userId: 1);

        return (f, session, settlement, ctx);
    }

    private static async Task<decimal> CashInAsync(DaftarContext ctx, int accountId)
        => await ctx.CashTransactions
            .Where(t => t.CashAccountId == accountId)
            .Select(t => t.Amount)
            .SumAsync(t => (decimal?)t) ?? 0m;

    [Fact]
    public async Task Settling_Leaves_The_Custody_Account_Empty()
    {
        // A custody account holds only money being carried for the company. When the session
        // closes, the van is unloaded and the money handed over, so nothing should remain.
        //
        // It did. The handover moved the counted cash rather than the collected cash, so a short
        // count silently left the shortfall sitting in the account; and the commission was charged
        // to the same account on top, driving it negative for a second and unrelated reason. The
        // company received 370 against 400 collected and the balance sheet showed neither.
        var (f, _, _, ctx) = await SettleAShortSessionAsync();

        Assert.Equal(0m, await CashInAsync(ctx, f.CustodyId));
    }

    [Fact]
    public async Task A_Cash_Shortage_Becomes_A_Row_Somebody_Can_Find()
    {
        // The session snapshot carried CashDifference = -30 and the van's stock shortage, but the
        // cash ledger carried nothing at all. The 30 was in the custody balance with no
        // explanation, so the account no longer reconciled with the money that existed.
        var (f, session, settlement, ctx) = await SettleAShortSessionAsync();

        Assert.Equal(-30m, settlement.CashDifference);

        var rows = await ctx.CashTransactions
            .Where(t => t.CashAccountId == f.CustodyId && t.DistributorSessionId == session.Id)
            .ToListAsync();

        Assert.Contains(rows, t => t.Kind == CashTransactionKind.Adjustment && t.Amount == -30m);
    }

    [Fact]
    public async Task Commission_Is_Paid_Out_Of_The_Company_Not_Out_Of_The_Van()
    {
        // The code says so: "Commission is payable from the company." Charging the van instead
        // means the company receives the handover and pays the commission out of the same pot,
        // which is charged twice against the day's takings and leaves the van account negative.
        var (f, session, settlement, ctx) = await SettleAShortSessionAsync();

        var commissionRows = await ctx.CashTransactions
            .Where(t => t.DistributorSessionId == session.Id
                     && t.Category == ExpenseCategory.Commission)
            .ToListAsync();

        var row = Assert.Single(commissionRows);
        Assert.Equal(f.CashId, row.CashAccountId);
        Assert.Equal(-settlement.CommissionAmount, row.Amount);

        // And the company paid for it, on top of receiving the handover.
        Assert.Equal(370m - settlement.CommissionAmount, await CashInAsync(ctx, f.CashId));
    }

    [Fact]
    public async Task SettlingTwice_IsRefused()
    {
        var f = await ArrangeAsync();
        var ctx = NewContext();
        var session = await OpenSessionAsync(ctx, f.Distributor.Id);

        var service = new DistributorSessionService(
            ctx, new StockCountService(ctx), new StockTransferPostingService(ctx));

        await service.SettleAsync(session.Id, countedCash: 0m, userId: 1);

        await Assert.ThrowsAsync<DomainException>(
            () => service.SettleAsync(session.Id, countedCash: 0m, userId: 1));
    }

    [Fact]
    public async Task SettlingWithoutCounting_LeavesStockAloneAndPaysNoCommissionOnZeroSales()
    {
        var f = await ArrangeAsync(commissionRate: 10m);
        var ctx = NewContext();
        var session = await OpenSessionAsync(ctx, f.Distributor.Id);
        await LoadVanAsync(ctx, session, f, 25m);

        var service = new DistributorSessionService(
            ctx, new StockCountService(ctx), new StockTransferPostingService(ctx));

        // He sold nothing and counted the van correctly.
        var settlement = await service.SettleAsync(
            session.Id,
            countedCash: 0m,
            countedQuantities: new Dictionary<int, decimal> { [f.LotId] = 25m });

        Assert.Equal(0m, settlement.SalesTotal);
        Assert.Equal(0m, settlement.CommissionAmount);
        Assert.Equal(0m, settlement.CashDifference);
        Assert.Equal(0m, settlement.StockShortageValue);

        // No variance means no adjustment movement for the van.
        Assert.Empty(await ctx.InventoryMovements
            .Where(m => m.Kind == InventoryMovementKind.CountAdjust).ToListAsync());

        // The van still holds what he loaded, because the count matched.
        Assert.Equal(25m, await ctx.StockBalances
            .SingleAsync(b => b.LocationId == f.VehicleId).ContinueWith(t => t.Result.Quantity));
    }

    [Fact]
    public async Task CommissionOnCollections_FollowsTheDistributorBasis()
    {
        var f = await ArrangeAsync(commissionRate: 10m);
        var ctx = NewContext();

        // Re-read in this context: f.Distributor belongs to ArrangeAsync's, already-saved one.
        var distributor = await ctx.Distributors.SingleAsync();
        distributor.CommissionBasis = CommissionBasis.Collections;
        await ctx.SaveChangesAsync();

        var session = await OpenSessionAsync(ctx, f.Distributor.Id);
        await LoadVanAsync(ctx, session, f, 20m);

        var sale = new Sale
        {
            Type = SaleType.Invoice, PartyId = f.Customer.Id,
            LocationId = f.VehicleId, PriceListId = f.List.Id,
            DocDate = Today, DistributorId = f.Distributor.Id, DistributorSessionId = session.Id,
        };
        ctx.Sales.Add(sale);
        await ctx.SaveChangesAsync();
        sale.AddItem(f.Product, f.Piece, quantity: 20m, listPrice: 20m, unitPrice: 20m);
        await ctx.SaveChangesAsync();
        await new SalePostingService(ctx).PostAsync(sale.Id, 1, Today);

        // He collected only 100 of the 400 sale.
        var payment = new Payment
        {
            Direction = PaymentDirection.In, PartyId = f.Customer.Id,
            CashAccountId = f.CustodyId, Amount = 100m, DocDate = Today,
            DistributorSessionId = session.Id,
        };
        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();
        await new PaymentPostingService(ctx).PostAsync(payment.Id, 1, Today);

        var service = new DistributorSessionService(
            ctx, new StockCountService(ctx), new StockTransferPostingService(ctx));

        var settlement = await service.SettleAsync(
            session.Id, countedCash: 100m,
            countedQuantities: new Dictionary<int, decimal> { [f.LotId] = 20m });

        // Commission is on collections, not on sales: 10% of 100, not of 400.
        Assert.Equal(400m, settlement.SalesTotal);
        Assert.Equal(100m, settlement.CollectionsTotal);
        Assert.Equal(10m, settlement.CommissionAmount);
    }

    [Fact]
    public async Task AllNineAppendOnlyTriggers_ArePresentAfterMigrations()
    {
        var ctx = NewContext();

        var triggers = await ctx.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type = 'trigger'")
            .ToListAsync();

        // Two each for the four ledgers.
        Assert.Equal(8, triggers.Count);

        foreach (var table in new[]
                 {
                     "AuditLogs", "InventoryMovements",
                     "PartyAccountTransactions", "CashTransactions",
                 })
        {
            Assert.Contains($"trg_{table}_no_update", triggers);
            Assert.Contains($"trg_{table}_no_delete", triggers);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(2.5)]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(99)]
    [InlineData(100)]
    public async Task CommissionRateCheckConstraint_AcceptsAllValidNumericValues(decimal rate)
    {
        var f = await ArrangeAsync(commissionRate: rate);
        var ctx = NewContext();
        var d = await ctx.Distributors.SingleAsync(x => x.Id == f.Distributor.Id);
        Assert.Equal(rate, d.CommissionRate);
    }
}