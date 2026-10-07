using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The الاستلام and المنتجات screens, driven exactly as the buttons drive them: a supplier, a
/// product with a unit and a price, then a received invoice. These are the first screens that
/// create the master data everything else depends on, so a silent failure here leaves the whole
/// app unusable.
/// </summary>
public sealed class ReceiveScreenTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"daftar-screens-{Guid.NewGuid():N}.db");

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

    private async Task<Party> SeedSupplierAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var supplier = new Party { Code = "SUP-1", Name = "مصنع النيل", IsSupplier = true };
        ctx.Parties.Add(supplier);
        await ctx.SaveChangesAsync();

        return supplier;
    }

    private async Task AddProductAsync(ProductsViewModel products, string sku, string name, string unit)
    {
        products.NewSku = sku;
        products.NewName = name;
        products.NewBaseUnit = unit;
        await products.AddProductCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Receiving_an_invoice_creates_a_batch_a_movement_and_a_balance()
    {
        var supplier = await SeedSupplierAsync();

        // Master data first: without a product, unit and price there is nothing to receive.
        var products = new ProductsViewModel(_fixture.Services);
        await products.LoadAsync();
        await AddProductAsync(products, "OLIVE-1", "زيت زيتون", "زجاجة");

        var row = Assert.Single(products.Products, p => p.Sku == "OLIVE-1");
        await products.SelectProductCommand.ExecuteAsync(row);

        var piece = Assert.Single(products.Units);
        products.PriceUnit = piece;
        products.PriceAmount = "75";
        await products.AddPriceCommand.ExecuteAsync(null);

        Assert.Contains(products.Prices, p => p.Price == 75m);

        var receive = new ReceiveViewModel(_fixture.Services);
        await receive.LoadAsync();

        Assert.Contains(receive.Suppliers, s => s.Id == supplier.Id);
        receive.SelectedSupplier = receive.Suppliers.Single(s => s.Id == supplier.Id);

        receive.SelectedProduct = receive.Products.Single(p => p.Sku == "OLIVE-1");
        receive.SelectedUnit = receive.Units.Single(u => u.Name == "زجاجة");

        receive.Quantity = "3";
        receive.UnitPrice = "70";

        await receive.AddLineCommand.ExecuteAsync(null);

        var line = Assert.Single(receive.Lines);
        Assert.Equal(210m, line.LineTotal);
        Assert.Equal(3m, line.BaseQuantity); // one bottle is the base unit
        Assert.True(receive.CanPost);
        Assert.Equal(210m, receive.Total);

        await receive.PostCommand.ExecuteAsync(null);

        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var lot = Assert.Single(await ctx.Lots.ToListAsync());
        Assert.Equal(supplier.Id, lot.SupplierId);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), lot.ReceivedOn);

        var movement = Assert.Single(await ctx.InventoryMovements.ToListAsync());
        Assert.Equal(InventoryMovementKind.Receipt, movement.Kind);
        Assert.Equal(3m, movement.Quantity);

        var balance = await ctx.StockBalances.SingleAsync();
        Assert.Equal(3m, balance.Quantity);

        var purchase = await ctx.Purchases
            .Include(p => p.Items)
            .SingleAsync();

        Assert.False(string.IsNullOrWhiteSpace(purchase.DocNo), "posting must stamp a document number");
        Assert.Equal(210m, purchase.Total);

        Assert.Empty(receive.Lines);
        Assert.False(receive.CanPost, "the screen clears after a successful post");
    }

    [Fact]
    public async Task No_lot_expiry_or_production_date_is_needed_to_receive()
    {
        var supplier = await SeedSupplierAsync();

        var products = new ProductsViewModel(_fixture.Services);
        await products.LoadAsync();
        await AddProductAsync(products, "MILK-1", "لبن جهينة", "كرتون");

        var receive = new ReceiveViewModel(_fixture.Services);
        await receive.LoadAsync();
        receive.SelectedSupplier = receive.Suppliers.Single(s => s.Id == supplier.Id);
        receive.SelectedProduct = receive.Products.Single(p => p.Sku == "MILK-1");
        receive.SelectedUnit = receive.Units.Single();

        receive.Quantity = "2";
        receive.UnitPrice = "50";

        await receive.AddLineCommand.ExecuteAsync(null);

        Assert.Single(receive.Lines);
        Assert.True(receive.CanPost);

        await receive.PostCommand.ExecuteAsync(null);

        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        // Stock arrived, and the lot carries no expiry to be expired by.
        var lot = Assert.Single(await ctx.Lots.ToListAsync());
        Assert.Equal(2m, (await ctx.StockBalances.SingleAsync()).Quantity);
        Assert.Equal(LotStatus.Active, lot.Status);
    }

    [Fact]
    public async Task Two_entries_of_the_same_product_and_unit_merge_into_one_line()
    {
        var supplier = await SeedSupplierAsync();

        var products = new ProductsViewModel(_fixture.Services);
        await products.LoadAsync();
        await AddProductAsync(products, "RICE-1", "أرز مصري", "كيس");

        var receive = new ReceiveViewModel(_fixture.Services);
        await receive.LoadAsync();
        receive.SelectedSupplier = receive.Suppliers.Single(s => s.Id == supplier.Id);
        receive.SelectedProduct = receive.Products.Single(p => p.Sku == "RICE-1");
        receive.SelectedUnit = receive.Units.Single();

        receive.Quantity = "2";
        receive.UnitPrice = "40";
        await receive.AddLineCommand.ExecuteAsync(null);

        receive.Quantity = "3";
        receive.UnitPrice = "40";
        await receive.AddLineCommand.ExecuteAsync(null);

        var line = Assert.Single(receive.Lines);
        Assert.Equal(5m, line.Quantity);
        Assert.Equal(200m, line.LineTotal);
    }

    [Fact]
    public async Task A_duplicate_sku_is_rejected_with_a_message_not_an_exception()
    {
        await SeedSupplierAsync();

        var products = new ProductsViewModel(_fixture.Services);
        await products.LoadAsync();

        await AddProductAsync(products, "SUGAR-1", "سكر", "كيس");

        products.NewSku = "SUGAR-1";
        products.NewName = "سكر تاني";
        products.NewBaseUnit = "كيس";
        await products.AddProductCommand.ExecuteAsync(null);

        Assert.Single(products.Products, p => p.Sku == "SUGAR-1");
        Assert.Contains("مستخدم بالفعل", products.Status);
    }

    [Fact]
    public async Task Receiving_is_blocked_until_a_supplier_and_a_line_exist()
    {
        var supplier = await SeedSupplierAsync();

        var receive = new ReceiveViewModel(_fixture.Services);
        await receive.LoadAsync();

        Assert.False(receive.CanPost, "nothing entered yet");

        receive.SelectedSupplier = receive.Suppliers.Single(s => s.Id == supplier.Id);
        Assert.False(receive.CanPost, "a supplier alone is not enough");

        var products = new ProductsViewModel(_fixture.Services);
        await products.LoadAsync();
        await AddProductAsync(products, "TEA-1", "شاي", "علبة");

        await receive.LoadAsync();
        receive.SelectedSupplier = receive.Suppliers.Single(s => s.Id == supplier.Id);
        receive.SelectedProduct = receive.Products.Single(p => p.Sku == "TEA-1");
        receive.SelectedUnit = receive.Units.Single();
        receive.Quantity = "1";
        receive.UnitPrice = "10";
        await receive.AddLineCommand.ExecuteAsync(null);

        Assert.True(receive.CanPost);

        // Removing the only line must disable posting again.
        receive.RemoveLineCommand.Execute(receive.Lines[0]);

        Assert.Empty(receive.Lines);
        Assert.False(receive.CanPost);
    }

    [Fact]
    public async Task A_new_base_unit_demotes_the_previous_base_unit()
    {
        var products = new ProductsViewModel(_fixture.Services);
        await products.LoadAsync();

        await AddProductAsync(products, "PASTA-1", "مكرونة", "كيس");

        var row = products.Products.Single(p => p.Sku == "PASTA-1");
        await products.SelectProductCommand.ExecuteAsync(row);

        Assert.Single(products.Units, u => u.IsBase);

        products.UnitName = "كرتون";
        products.UnitFactor = "20";
        products.UnitIsBase = true;
        await products.AddUnitCommand.ExecuteAsync(null);

        Assert.Equal(2, products.Units.Count);
        Assert.Single(products.Units, u => u.IsBase);
        Assert.Equal("كرتون", products.Units.Single(u => u.IsBase).Name);
    }
}