using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Domain.Catalog;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

public sealed class ProductsScreenTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"daftar-products-test-{Guid.NewGuid():N}.db");

    private ServiceProviderFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = await ServiceProviderFixture.CreateAsync(_dbPath);

        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        await DaftarDatabase.InitializeAsync(ctx, PasswordHasher.Hash("admin"));
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

    [Fact]
    public async Task AddProduct_with_initial_prices_creates_product_base_unit_and_price_entry()
    {
        var vm = new ProductsViewModel(_fixture.Services);
        await vm.LoadAsync();

        vm.StartAddProduct();
        vm.NewSku = "MILK-1";
        vm.NewName = "حليب كامل الدسم";
        vm.NewBaseUnit = "علبة";
        vm.NewPurchasePrice = "25.00";
        vm.NewSalePrice = "32.00";
        vm.NewBarcode = "6221000123456";
        vm.NewReorderLevel = "15";

        await vm.AddProductCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.NotNull(vm.SelectedProduct);
        Assert.Equal("MILK-1", vm.SelectedProduct.Sku);
        Assert.Single(vm.Units);

        var baseUnit = vm.Units[0];
        Assert.True(baseUnit.IsBase);
        Assert.Equal(25.00m, baseUnit.PurchasePrice);
        Assert.Equal(32.00m, baseUnit.SalePrice);
        Assert.Equal(7.00m, baseUnit.Profit);
        Assert.Equal(28.0m, baseUnit.ProfitMarginPercent);
        Assert.Equal("6221000123456", baseUnit.Barcode);

        Assert.Single(vm.Prices);
        Assert.Equal(32.00m, vm.Prices[0].Price);
    }

    [Fact]
    public async Task Search_and_filters_accurately_filter_by_sku_name_and_barcode()
    {
        var vm = new ProductsViewModel(_fixture.Services);
        await vm.LoadAsync();

        vm.NewSku = "TEA-1";
        vm.NewName = "شاي أسود ناعم";
        vm.NewBaseUnit = "باكت";
        vm.NewBarcode = "99887766";
        await vm.AddProductCommand.ExecuteAsync(null);

        vm.NewSku = "COFFEE-1";
        vm.NewName = "قهوة محوجة";
        vm.NewBaseUnit = "كيس";
        await vm.AddProductCommand.ExecuteAsync(null);

        // Search by Name
        vm.SearchText = "شاي";
        Assert.Single(vm.FilteredProducts);
        Assert.Equal("TEA-1", vm.FilteredProducts[0].Sku);

        // Search by SKU
        vm.SearchText = "COFFEE";
        Assert.Single(vm.FilteredProducts);
        Assert.Equal("COFFEE-1", vm.FilteredProducts[0].Sku);

        // Search by Barcode
        vm.SearchText = "99887766";
        Assert.Single(vm.FilteredProducts);
        Assert.Equal("TEA-1", vm.FilteredProducts[0].Sku);

        // Filter inactive
        vm.SearchText = string.Empty;
        await vm.ToggleActiveCommand.ExecuteAsync(vm.FilteredProducts.First(p => p.Sku == "TEA-1"));

        vm.SetFilter(3); // Inactive
        Assert.Single(vm.FilteredProducts);
        Assert.Equal("TEA-1", vm.FilteredProducts[0].Sku);

        vm.SetFilter(1); // Active
        Assert.Single(vm.FilteredProducts);
        Assert.Equal("COFFEE-1", vm.FilteredProducts[0].Sku);
    }

    [Fact]
    public async Task EditProduct_saves_changes_and_updates_state()
    {
        var vm = new ProductsViewModel(_fixture.Services);
        await vm.LoadAsync();

        vm.NewSku = "SOAP-1";
        vm.NewName = "صابون غسيل";
        vm.NewBaseUnit = "قطعة";
        await vm.AddProductCommand.ExecuteAsync(null);

        vm.EditName = "صابون تواليت فاخر";
        vm.EditReorderLevel = "50";
        await vm.SaveProductEditCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.Equal("صابون تواليت فاخر", vm.SelectedProduct!.Name);
        Assert.Equal(50m, vm.SelectedProduct.ReorderLevel);
    }

    [Fact]
    public async Task UnitManagement_allows_adding_editing_and_setting_defaults()
    {
        var vm = new ProductsViewModel(_fixture.Services);
        await vm.LoadAsync();

        vm.NewSku = "JUICE-1";
        vm.NewName = "عصير برتقال";
        vm.NewBaseUnit = "عبوة";
        vm.NewPurchasePrice = "10.00";
        vm.NewSalePrice = "15.00";
        await vm.AddProductCommand.ExecuteAsync(null);

        // Add carton unit (12 units)
        vm.UnitName = "كرتونة";
        vm.UnitFactor = "12";
        vm.UnitPurchasePrice = "110.00";
        vm.UnitSalePrice = "160.00";
        vm.UnitBarcode = "123456789";
        await vm.AddUnitCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Units.Count);
        var carton = vm.Units.First(u => u.Name == "كرتونة");
        Assert.Equal(12m, carton.Factor);
        Assert.Equal(110.00m, carton.PurchasePrice);
        Assert.Equal(160.00m, carton.SalePrice);

        // Set carton as default sale unit
        await vm.SetDefaultSaleUnitCommand.ExecuteAsync(carton);
        Assert.True(vm.Units.First(u => u.Name == "كرتونة").IsDefaultSale);
        Assert.False(vm.Units.First(u => u.Name == "عبوة").IsDefaultSale);

        // Edit carton unit price
        vm.EditUnit(carton);
        Assert.True(vm.IsEditingUnit);
        vm.UnitSalePrice = "170.00";
        await vm.SaveUnitCommand.ExecuteAsync(null);

        var updatedCarton = vm.Units.First(u => u.Name == "كرتونة");
        Assert.Equal(170.00m, updatedCarton.SalePrice);

        // Delete carton unit
        await vm.DeleteUnitCommand.ExecuteAsync(updatedCarton);
        Assert.Single(vm.Units);
        Assert.Equal("عبوة", vm.Units[0].Name);
    }

    [Theory]
    [InlineData("زيت زيتون 1 لتر", "زيت-1")]
    [InlineData("أرز مصري 5 كجم", "أرز-5")]
    [InlineData("شاي ليبتون 100 فتلة", "شاي-100")]
    [InlineData("تونة قطع 140 جرام", "تونة-140")]
    [InlineData("حليب كامل الدسم", "حليب-كامل")]
    [InlineData("سكر", "سكر-1")]
    public void GenerateSkuFromName_extracts_first_word_and_number(string name, string expected)
    {
        var sku = ProductsViewModel.GenerateSkuFromName(name);
        Assert.Equal(expected, sku);
    }

    [Fact]
    public async Task AddProduct_without_explicit_sku_auto_generates_sku_from_name()
    {
        var vm = new ProductsViewModel(_fixture.Services);
        await vm.LoadAsync();

        vm.StartAddProduct();
        vm.NewName = "زيت زيتون 1 لتر";
        Assert.Equal("زيت-1", vm.NewSku);

        await vm.AddProductCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.NotNull(vm.SelectedProduct);
        Assert.Equal("زيت-1", vm.SelectedProduct.Sku);
        Assert.Equal("زيت زيتون 1 لتر", vm.SelectedProduct.Name);
    }
}

