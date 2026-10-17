using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The unit picked in the product grid is the unit that reaches the cart.
///
/// <para>
/// The grid renders <see cref="ProductTileItem"/> rather than the screen's own product object, so
/// the tile holds a copy of the units. A copy is fine until the cashier changes the unit: the cart
/// reads the original object, which still has the unit the product was built with. Nothing about
/// that failure looks like a failure. The tile says "كرتونة", the cart line says "قطعة", and the
/// sale posts at the wrong price.
/// </para>
///
/// <para>
/// So the tile calls back into the screen when the unit changes. This is that callback, tested
/// through the real ViewModel and a real database, because the alternative is to test the tile's
/// constructor and learn nothing about whether the screen wires it.
/// </para>
///
/// <para>
/// The assertion is on the cart line's unit, not on a property that happens to be nearby. The
/// intermediate step could be honest while the thing the cashier is charged for is wrong.
/// </para>
/// </summary>
public sealed class ProductTileUnitBindingTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"daftar-tileunit-{Guid.NewGuid():N}.db");

    private ServiceProviderFixture _fixture = null!;
    private int _pieceUnitId;
    private int _cartonUnitId;

    public async Task InitializeAsync()
    {
        _fixture = await ServiceProviderFixture.CreateAsync(_dbPath);

        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        await DaftarDatabase.InitializeAsync(ctx, PasswordHasher.Hash("admin"));

        ctx.Parties.Add(new Domain.Parties.Party
        {
            Code = "C-TILE",
            Name = "عميل بلاطة",
            IsCustomer = true,
            CustomerType = Domain.Parties.CustomerType.Retail,
            CreditLimit = 10_000m,
        });

        var product = new Domain.Catalog.Product("TILE-1", "صنف وحدتين");
        product.Units.Add(new Domain.Catalog.ProductUnit
        {
            Name = "قطعة",
            Factor = 1m,
            IsBase = true,
            IsDefaultSale = true,
            IsDefaultPurchase = true,
        });
        product.Units.Add(new Domain.Catalog.ProductUnit
        {
            Name = "كرتونة",
            Factor = 10m,
            IsBase = false,
            IsDefaultSale = false,
            IsDefaultPurchase = false,
        });

        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        _pieceUnitId = product.Units[0].Id;
        _cartonUnitId = product.Units[1].Id;

        // Both units need a price, or the carton never reaches the cart at all and this test would
        // pass for the wrong reason.
        var list = await ctx.PriceLists.FirstAsync();
        foreach (var (unitId, price) in new[] { (_pieceUnitId, 25m), (_cartonUnitId, 220m) })
        {
            ctx.ProductPrices.Add(new Domain.Sales.ProductPrice
            {
                PriceListId = list.Id,
                ProductUnitId = unitId,
                Price = price,
                EffectiveFrom = new DateOnly(2026, 1, 1),
            });
        }

        await ctx.SaveChangesAsync();

        await ReceiveStockAsync(100m);
    }

    public Task DisposeAsync()
    {
        _fixture.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(p))
                File.Delete(p);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Brings stock in through الاستلام, the same path the app uses, so the till has real stock
    /// rather than a balance invented for the test.
    /// </summary>
    private async Task ReceiveStockAsync(decimal quantity)
    {
        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();

        var supplier = new Domain.Parties.Party
        {
            Code = "S-TILE",
            Name = "مورد بلاطة",
            IsSupplier = true,
        };
        ctx.Parties.Add(supplier);
        await ctx.SaveChangesAsync();

        var receive = new ReceiveViewModel(_fixture.Services);
        await receive.LoadAsync();

        receive.SelectedSupplier = receive.Suppliers.First(s => s.Name == "مورد بلاطة");
        receive.SelectedProduct = receive.Products.First(p => p.Sku == "TILE-1");
        receive.SelectedUnit = receive.Units.First();
        receive.Quantity = quantity.ToString(CultureInfo.InvariantCulture);
        receive.UnitPrice = "15";

        await receive.AddLineCommand.ExecuteAsync(null);
        await receive.PostCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Picking_A_Unit_In_The_Grid_Puts_That_Unit_In_The_Cart()
    {
        var vm = new SellViewModel(_fixture.Services);
        await vm.LoadAsync();

        var tile = vm.FilteredProducts.Single(t => t.Sku == "TILE-1");

        // The base unit is the default, so this is the state the cashier starts from.
        Assert.Equal(_pieceUnitId, tile.SelectedUnit?.ProductUnitId);

        tile.SelectedUnit = tile.Units.Single(u => u.ProductUnitId == _cartonUnitId);
        // The unit's NAME, not just its id. A screenshot showed an empty unit picker and an
        // empty "/ unit" suffix while the price looked perfectly right: the id survived the copy
        // and the name did not.
        Assert.Equal("كرتونة", tile.SelectedUnit!.UnitName);
        Assert.Equal("كرتونة", tile.UnitName);

        tile.SelectCommand.Execute(tile);

        var line = Assert.Single(vm.Cart);
        Assert.Equal(_cartonUnitId, line.ProductUnitId);
    }

    [Fact]
    public async Task The_Grid_And_The_Screen_Do_Not_Drift_Apart_On_The_Unit()
    {
        var vm = new SellViewModel(_fixture.Services);
        await vm.LoadAsync();

        var tile = vm.FilteredProducts.Single(t => t.Sku == "TILE-1");
        var option = vm.Products.Single(p => p.Sku == "TILE-1");

        tile.SelectedUnit = tile.Units.Single(u => u.ProductUnitId == _cartonUnitId);

        // The tile is a copy; this is what stops the copy and the original from disagreeing.
        Assert.Equal(_cartonUnitId, option.SelectedUnit?.ProductUnitId);

        // And the numbers the tile displays have to be the ones for the unit it is now showing.
        Assert.Equal(220m, tile.SelectedUnit!.Price);
        Assert.Contains("220", tile.PriceText);
    }

    [Fact]
    public async Task Picking_A_Unit_In_The_Receive_Grid_Reaches_The_Invoice_Line()
    {
        // Same copy, same risk, other screen: the receive grid builds its tiles the same way, so
        // without its own handshake a carton picked on the grid is received as a piece and the
        // stock balance moves by a tenth of what the cashier entered.
        var receive = new ReceiveViewModel(_fixture.Services);
        await receive.LoadAsync();

        var tile = receive.FilteredProducts.Single(t => t.Sku == "TILE-1");
        var option = receive.Products.Single(p => p.Sku == "TILE-1");

        tile.SelectedUnit = tile.Units.Single(u => u.ProductUnitId == _cartonUnitId);
        tile.SelectCommand.Execute(tile);

        Assert.Equal(_cartonUnitId, option.SelectedUnit?.ProductUnitId);

        var line = Assert.Single(receive.Lines);
        Assert.Equal(_cartonUnitId, line.ProductUnitId);
    }

    [Fact]
    public async Task Filtering_Reuses_The_Same_Tile_Rather_Than_A_Fresh_One()
    {
        var vm = new SellViewModel(_fixture.Services);
        await vm.LoadAsync();

        var before = vm.FilteredProducts.Single(t => t.Sku == "TILE-1");

        vm.ProductSearchText = "TILE-1";
        var after = vm.FilteredProducts.Single(t => t.Sku == "TILE-1");

        vm.ProductSearchText = "لا-يوجد";
        Assert.Empty(vm.FilteredProducts);

        vm.ProductSearchText = "TILE-1";
        var again = vm.FilteredProducts.Single(t => t.Sku == "TILE-1");

        // Rebuilding the tiles on every keystroke would drop the unit the cashier had already
        // chosen, and rebuild them per product per keystroke would be an allocation storm.
        Assert.Same(before, after);
        Assert.Same(before, again);
    }
}