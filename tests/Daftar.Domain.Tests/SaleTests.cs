using Daftar.Domain.Catalog;
using Daftar.Domain.Finance;
using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Domain.Sales;
using Xunit;

namespace Daftar.Domain.Tests;

public class SaleTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static Product Product(string sku = "PASTE-1")
    {
        var product = new Product(sku, $"Product {sku}");
        product.Units.Add(new ProductUnit { Name = "piece", Factor = 1m, IsBase = true });
        product.Units.Add(new ProductUnit { Name = "case", Factor = 24m });
        return product;
    }

    /// <summary>A delivery made <paramref name="daysAgo"/> days ago: older stock draws first.</summary>
    private static StockBalance LotBalance(int lotId, int productId, decimal qty, int daysAgo, decimal unitCost = 5m)
    {
        var lot = Lot.Create(productId, Today.AddDays(-daysAgo), null, unitCost);

        var balance = new StockBalance { LotId = lotId, LocationId = 1, Lot = lot };
        balance.Apply(qty, DateTime.UtcNow);
        return balance;
    }

    /// <summary>A quarantined delivery: present, but never sellable.</summary>
    private static StockBalance BlockedBalance(int lotId, int productId, decimal qty, int daysAgo)
    {
        var lot = Lot.Create(productId, Today.AddDays(-daysAgo), null, 5m);
        lot.Block("Recall");

        var balance = new StockBalance { LotId = lotId, LocationId = 1, Lot = lot };
        balance.Apply(qty, DateTime.UtcNow);
        return balance;
    }

    private static Sale DraftSale(Party customer, PriceList list)
        => new()
        {
            Type = SaleType.Invoice,
            PartyId = customer.Id,
            LocationId = 1,
            PriceListId = list.Id,
            DocDate = Today,
        };

    [Fact]
    public void AddItem_SnapshotsUnitAndStoresListPriceSeparately()
    {
        var product = Product();
        var sale = DraftSale(new Party { Code = "C1", Name = "Shop", IsCustomer = true, CustomerType = CustomerType.Retail },
            new PriceList { Name = "Standard" });

        // 2 cases at a list price of 100, actually charged 90 (an override).
        var item = sale.AddItem(product, product.Units[1], quantity: 2m, listPrice: 100m, unitPrice: 90m);

        Assert.Equal("case", item.UnitName);
        Assert.Equal(24m, item.UnitFactor);
        Assert.Equal(48m, item.BaseQuantity);
        Assert.Equal(180m, item.LineTotal);
        Assert.True(item.IsOverride);
    }

    [Fact]
    public void AllocateFrom_SplitsALineAcrossBatchesOldestFirst()
    {
        var product = Product();
        var sale = DraftSale(new Party { Code = "C1", Name = "Shop", IsCustomer = true, CustomerType = CustomerType.Retail },
            new PriceList { Name = "Standard" });
        var item = sale.AddItem(product, product.Units[0], quantity: 6m, listPrice: 10m, unitPrice: 10m);

        var balances = new[]
        {
            LotBalance(2, product.Id, 4m, daysAgo: 30),
            LotBalance(1, product.Id, 4m, daysAgo: 5),   // newer, so drawn second
        };

        item.AllocateFrom(balances);

        // 4 from the older delivery (batch 2), then 2 from the newer one.
        Assert.Equal(2, item.Lots.Count);
        Assert.Equal(2, item.Lots[0].LotId);
        Assert.Equal(4m, item.Lots[0].BaseQuantity);
        Assert.Equal(1, item.Lots[1].LotId);
        Assert.Equal(2m, item.Lots[1].BaseQuantity);
        Assert.Equal(6m, item.Lots.Sum(l => l.BaseQuantity));
    }

    [Fact]
    public void AllocateFrom_SkipsBlockedBatches()
    {
        var product = Product();
        var sale = DraftSale(new Party { Code = "C1", Name = "Shop", IsCustomer = true, CustomerType = CustomerType.Retail },
            new PriceList { Name = "Standard" });
        var item = sale.AddItem(product, product.Units[0], quantity: 5m, listPrice: 10m, unitPrice: 10m);

        var balances = new[]
        {
            BlockedBalance(1, product.Id, 10m, daysAgo: 30),   // blocked, so never drawn
            LotBalance(2, product.Id, 10m, daysAgo: 20),
        };

        item.AllocateFrom(balances);

        Assert.Single(item.Lots);
        Assert.Equal(2, item.Lots[0].LotId);
    }

    [Fact]
    public void AllocateFrom_RejectsWhenStockIsShort()
    {
        var product = Product();
        var sale = DraftSale(new Party { Code = "C1", Name = "Shop", IsCustomer = true, CustomerType = CustomerType.Retail },
            new PriceList { Name = "Standard" });
        var item = sale.AddItem(product, product.Units[0], quantity: 10m, listPrice: 10m, unitPrice: 10m);

        Assert.Throws<InsufficientStockException>(() =>
            item.AllocateFrom([LotBalance(1, product.Id, 4m, daysAgo: 10)]));
    }

    [Fact]
    public void AllocateFrom_RefusesToAllocateTwice()
    {
        var product = Product();
        var sale = DraftSale(new Party { Code = "C1", Name = "Shop", IsCustomer = true, CustomerType = CustomerType.Retail },
            new PriceList { Name = "Standard" });
        var item = sale.AddItem(product, product.Units[0], quantity: 5m, listPrice: 10m, unitPrice: 10m);
        var balances = new[] { LotBalance(1, product.Id, 10m, daysAgo: 10) };

        item.AllocateFrom(balances);

        Assert.Throws<Shared.DomainException>(() => item.AllocateFrom(balances));
    }

    [Fact]
    public void MarginUsesLotCosts()
    {
        var product = Product();
        var sale = DraftSale(new Party { Code = "C1", Name = "Shop", IsCustomer = true, CustomerType = CustomerType.Retail },
            new PriceList { Name = "Standard" });
        var item = sale.AddItem(product, product.Units[0], quantity: 10m, listPrice: 10m, unitPrice: 10m);

        item.AddLot(lotId: 1, baseQuantity: 10m, unitCost: 6m);

        Assert.Equal(60m, item.CostTotal);
        Assert.Equal(40m, sale.Margin);
    }

    [Fact]
    public void ReturnRequiresTheOriginalSale()
    {
        var sale = new Sale { Type = SaleType.Return, PartyId = 1, LocationId = 1, PriceListId = 1, DocDate = Today };
        sale.AddItem(Product(), Product().Units[0], 1m, 10m, 10m);

        Assert.Throws<Shared.DomainException>(() => sale.Post("SO-000001", 1, DateTime.UtcNow));
    }

    [Fact]
    public void RequiresOverridePermission_WhenPriceDiffersOrDiscountTooDeep()
    {
        var product = Product();
        var customer = new Party { Code = "C1", Name = "Shop", IsCustomer = true, CustomerType = CustomerType.Retail };
        var sale = DraftSale(customer, new PriceList { Name = "Standard" });

        // Priced at list: no override needed.
        sale.AddItem(product, product.Units[0], 5m, listPrice: 10m, unitPrice: 10m);
        var strict = new Security.Role { Name = "Cashier", MaxDiscountPercent = 10m };
        Assert.False(sale.RequiresOverridePermission(strict));

        // A 20% discount is deeper than this role may give.
        sale.AddItem(product, product.Units[1], 1m, listPrice: 100m, unitPrice: 100m, discountAmount: 20m);
        Assert.True(sale.RequiresOverridePermission(strict));

        // A generous role may discount, but under-pricing still needs the permission.
        var manager = new Security.Role { Name = "Manager", MaxDiscountPercent = 50m };
        Assert.False(sale.RequiresOverridePermission(manager));

        sale.AddItem(product, product.Units[0], 1m, listPrice: 100m, unitPrice: 80m);
        Assert.True(sale.RequiresOverridePermission(manager));
    }

    [Fact]
    public void PostedSale_CannotBeCancelled()
    {
        var product = Product();
        var customer = new Party { Id = 42, Code = "C1", Name = "Shop", IsCustomer = true, CustomerType = CustomerType.Retail };
        var sale = DraftSale(customer, new PriceList { Name = "Standard" });
        var item = sale.AddItem(product, product.Units[0], 5m, 10m, 10m);
        item.AddLot(1, 5m, 4m);
        sale.Post("SO-000001", 1, DateTime.UtcNow);

        Assert.Throws<Shared.DomainException>(() => sale.Cancel("mistake", 1, DateTime.UtcNow));
    }
}