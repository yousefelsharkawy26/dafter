using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Domain.Catalog;
using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

public sealed class ReceivePriceSyncTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"daftar-pricesync-{Guid.NewGuid():N}.db");

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

    private async Task<(Party Supplier, Product ProductA, Product ProductB)> SeedDataAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var supplier = new Party { Code = "SUP-1", Name = "مورد رئيسي", IsSupplier = true };
        ctx.Parties.Add(supplier);

        // Product A with 2 units: Piece (ref: 50.00), Box of 12 (ref: 550.00)
        var prodA = new Product("SKU-A", "منتج أ");
        var unitPieceA = new ProductUnit
        {
            Name = "قطعة",
            Factor = 1m,
            IsBase = true,
            IsDefaultPurchase = true,
            PurchasePrice = 50.00m,
        };
        var unitBoxA = new ProductUnit
        {
            Name = "كرتونة",
            Factor = 12m,
            IsBase = false,
            IsDefaultPurchase = false,
            PurchasePrice = 550.00m,
        };
        prodA.Units.Add(unitPieceA);
        prodA.Units.Add(unitBoxA);
        ctx.Products.Add(prodA);

        // Product B with Piece (ref: 70.00)
        var prodB = new Product("SKU-B", "منتج ب");
        var unitPieceB = new ProductUnit
        {
            Name = "قطعة",
            Factor = 1m,
            IsBase = true,
            IsDefaultPurchase = true,
            PurchasePrice = 70.00m,
        };
        prodB.Units.Add(unitPieceB);
        ctx.Products.Add(prodB);

        await ctx.SaveChangesAsync();
        unitPieceA.ProductId = prodA.Id;
        unitBoxA.ProductId = prodA.Id;
        unitPieceB.ProductId = prodB.Id;
        await ctx.SaveChangesAsync();

        return (supplier, prodA, prodB);
    }

    // =========================================================================
    // 1. Scanned-Row State Management Tests
    // =========================================================================

    [Fact]
    public void Changing_product_refreshes_the_reference_price()
    {
        var prodA = new ProductRow(1, "SKU-A", "منتج أ");
        var unitA = new UnitRow(10, "قطعة", 1m, true, PurchasePrice: 50m, ProductId: 1);

        var prodB = new ProductRow(2, "SKU-B", "منتج ب");
        var unitB = new UnitRow(20, "قطعة", 1m, true, PurchasePrice: 70m, ProductId: 2);

        var unitsByProduct = new Dictionary<int, ObservableCollection<UnitRow>>
        {
            [1] = [unitA],
            [2] = [unitB],
        };

        var row = new ScannedRow
        {
            UnitsByProduct = unitsByProduct,
            Choices = [prodA, prodB],
            ScannedUnitPrice = 50m,
        };

        row.Product = prodA;
        row.Unit = unitA;
        row.SetPriceInternal(50m, PriceSource.Ocr);

        Assert.Equal(50m, row.UnitPrice);

        // User changes to Product B (reference price: 70)
        row.Product = prodB;

        Assert.Equal(prodB, row.Product);
        Assert.Equal(unitB, row.Unit);
        Assert.Equal(70m, row.UnitPrice);
        Assert.Equal(PriceSource.Reference, row.PriceSource);
    }

    [Fact]
    public void Changing_unit_refreshes_the_reference_price_for_that_exact_unit()
    {
        var prodA = new ProductRow(1, "SKU-A", "منتج أ");
        var unitPiece = new UnitRow(10, "قطعة", 1m, true, PurchasePrice: 50m, ProductId: 1);
        var unitBox = new UnitRow(11, "كرتونة", 12m, false, PurchasePrice: 550m, ProductId: 1);

        var unitsByProduct = new Dictionary<int, ObservableCollection<UnitRow>>
        {
            [1] = [unitPiece, unitBox],
        };

        var row = new ScannedRow
        {
            UnitsByProduct = unitsByProduct,
            Choices = [prodA],
        };

        row.Product = prodA;
        Assert.Equal(unitPiece, row.Unit);
        Assert.Equal(50m, row.UnitPrice);

        // Switch to Box
        row.Unit = unitBox;
        Assert.Equal(550m, row.UnitPrice);
        Assert.Equal(PriceSource.Reference, row.PriceSource);

        // Switch back to Piece
        row.Unit = unitPiece;
        Assert.Equal(50m, row.UnitPrice);
    }

    [Fact]
    public void Manual_price_edits_survive_unrelated_notifications()
    {
        var prodA = new ProductRow(1, "SKU-A", "منتج أ");
        var unitA = new UnitRow(10, "قطعة", 1m, true, PurchasePrice: 50m, ProductId: 1);

        var row = new ScannedRow
        {
            UnitsByProduct = new Dictionary<int, ObservableCollection<UnitRow>> { [1] = [unitA] },
            Choices = [prodA],
        };

        row.Product = prodA;
        Assert.Equal(50m, row.UnitPrice);

        // User manually changes price to 60
        row.UnitPrice = 60m;
        Assert.Equal(PriceSource.Manual, row.PriceSource);
        Assert.Equal(60m, row.UnitPrice);

        // Unrelated notifications (quantity change, etc.)
        row.Quantity = 5m;
        Assert.Equal(60m, row.UnitPrice);
        Assert.Equal(PriceSource.Manual, row.PriceSource);
    }

    [Fact]
    public void Manual_override_does_not_leak_to_different_product_or_unit()
    {
        var prodA = new ProductRow(1, "SKU-A", "منتج أ");
        var unitA = new UnitRow(10, "قطعة", 1m, true, PurchasePrice: 50m, ProductId: 1);

        var prodB = new ProductRow(2, "SKU-B", "منتج ب");
        var unitB = new UnitRow(20, "قطعة", 1m, true, PurchasePrice: 70m, ProductId: 2);

        var row = new ScannedRow
        {
            UnitsByProduct = new Dictionary<int, ObservableCollection<UnitRow>>
            {
                [1] = [unitA],
                [2] = [unitB],
            },
            Choices = [prodA, prodB],
        };

        row.Product = prodA;
        // User manually overrides product A's price to 60
        row.UnitPrice = 60m;
        Assert.Equal(60m, row.UnitPrice);

        // Switch to Product B: price must become 70 (B's ref price), NOT 60!
        row.Product = prodB;
        Assert.Equal(70m, row.UnitPrice);
        Assert.Equal(PriceSource.Reference, row.PriceSource);
    }

    [Fact]
    public void Internal_assignments_do_not_trigger_false_manual_edit_state()
    {
        var row = new ScannedRow();
        row.SetPriceInternal(55m, PriceSource.Ocr);

        Assert.Equal(PriceSource.Ocr, row.PriceSource);
        Assert.Null(row.ManualOverridePrice);
    }

    [Fact]
    public void Invalid_product_unit_combination_is_rejected_safely()
    {
        var prodA = new ProductRow(1, "SKU-A", "منتج أ");
        var unitB = new UnitRow(20, "قطعة", 1m, true, PurchasePrice: 70m, ProductId: 2); // belongs to product 2!

        var row = new ScannedRow
        {
            Choices = [prodA],
        };

        row.Product = prodA;
        // Assigning a unit that belongs to a different product ID
        row.Unit = unitB;

        Assert.Null(row.Unit);
        Assert.Equal(0m, row.UnitPrice);
    }

    [Fact]
    public void Ocr_price_and_final_invoice_price_remain_distinct()
    {
        var prodA = new ProductRow(1, "SKU-A", "منتج أ");
        var unitA = new UnitRow(10, "قطعة", 1m, true, PurchasePrice: 50m, ProductId: 1);

        var row = new ScannedRow
        {
            ScannedUnitPrice = 51.50m,
            Choices = [prodA],
            UnitsByProduct = new Dictionary<int, ObservableCollection<UnitRow>> { [1] = [unitA] },
        };

        row.Product = prodA;
        row.SetPriceInternal(51.50m, PriceSource.Ocr);

        Assert.Equal(51.50m, row.ScannedUnitPrice);
        Assert.Equal(51.50m, row.UnitPrice);
        Assert.Equal(50.00m, row.ReferencePurchasePrice);
        Assert.True(row.HasPriceDiscrepancy);
    }

    // =========================================================================
    // 2. Posting Behavior and Atomic Reference Price Updates Tests
    // =========================================================================

    [Fact]
    public async Task Differing_invoice_price_does_not_update_reference_price_without_explicit_approval()
    {
        var (supplier, prodA, _) = await SeedDataAsync();

        var receive = new ReceiveViewModel(_fixture.Services);
        await receive.LoadAsync();

        receive.SelectedSupplier = receive.Suppliers.Single(s => s.Id == supplier.Id);

        var pOpt = receive.Products.Single(p => p.ProductId == prodA.Id);
        receive.SelectProductCommand.Execute(pOpt);

        var line = Assert.Single(receive.Lines);
        Assert.Equal(50.00m, line.UnitPrice);
        Assert.False(line.UpdateProductPurchasePrice);

        // Price in invoice is 51.00m, but update approval is FALSE
        line.UnitPrice = 51.00m;
        line.UpdateProductPurchasePrice = false;

        await receive.PostCommand.ExecuteAsync(null);

        // Verify in database that ProductUnit.PurchasePrice was NOT changed
        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        var unitInDb = await ctx.ProductUnits.FirstAsync(u => u.Id == line.ProductUnitId);

        Assert.Equal(50.00m, unitInDb.PurchasePrice);
    }

    [Fact]
    public async Task Approved_update_changes_correct_ProductUnit_PurchasePrice()
    {
        var (supplier, prodA, _) = await SeedDataAsync();

        var receive = new ReceiveViewModel(_fixture.Services);
        await receive.LoadAsync();

        receive.SelectedSupplier = receive.Suppliers.Single(s => s.Id == supplier.Id);

        var pOpt = receive.Products.Single(p => p.ProductId == prodA.Id);
        receive.SelectProductCommand.Execute(pOpt);

        var line = Assert.Single(receive.Lines);
        line.UnitPrice = 51.00m;
        line.UpdateProductPurchasePrice = true; // explicitly approved!

        await receive.PostCommand.ExecuteAsync(null);

        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        var unitInDb = await ctx.ProductUnits.FirstAsync(u => u.Id == line.ProductUnitId);

        Assert.Equal(51.00m, unitInDb.PurchasePrice);
    }

    [Fact]
    public async Task Update_affects_only_selected_product_unit_not_other_units_of_same_product()
    {
        var (supplier, prodA, _) = await SeedDataAsync();

        var receive = new ReceiveViewModel(_fixture.Services);
        await receive.LoadAsync();
        receive.SelectedSupplier = receive.Suppliers.Single(s => s.Id == supplier.Id);

        var pOpt = receive.Products.Single(p => p.ProductId == prodA.Id);
        receive.SelectProductCommand.Execute(pOpt);

        var line = Assert.Single(receive.Lines);
        // Only piece unit is approved for update from 50.00 to 52.00
        line.UnitPrice = 52.00m;
        line.UpdateProductPurchasePrice = true;

        await receive.PostCommand.ExecuteAsync(null);

        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var pieceInDb = await ctx.ProductUnits.FirstAsync(u => u.ProductId == prodA.Id && u.Name == "قطعة");
        var boxInDb = await ctx.ProductUnits.FirstAsync(u => u.ProductId == prodA.Id && u.Name == "كرتونة");

        Assert.Equal(52.00m, pieceInDb.PurchasePrice);
        Assert.Equal(550.00m, boxInDb.PurchasePrice); // Box remains 550.00!
    }

    [Fact]
    public async Task Multiple_identical_approved_updates_for_same_unit_are_consolidated()
    {
        var (supplier, prodA, _) = await SeedDataAsync();

        var receive = new ReceiveViewModel(_fixture.Services);
        await receive.LoadAsync();
        receive.SelectedSupplier = receive.Suppliers.Single(s => s.Id == supplier.Id);

        // Add 2 lines for same unit with identical price 53.00
        var unitPiece = prodA.Units.First(u => u.Name == "قطعة");
        var line1 = new ReceiveLine
        {
            ProductId = prodA.Id,
            ProductUnitId = unitPiece.Id,
            ProductName = prodA.Name,
            UnitName = unitPiece.Name,
            Quantity = 2m,
            UnitPrice = 53.00m,
            UpdateProductPurchasePrice = true,
        };
        var line2 = new ReceiveLine
        {
            ProductId = prodA.Id,
            ProductUnitId = unitPiece.Id,
            ProductName = prodA.Name,
            UnitName = unitPiece.Name,
            Quantity = 3m,
            UnitPrice = 53.00m,
            UpdateProductPurchasePrice = true,
        };
        receive.Lines.Add(line1);
        receive.Lines.Add(line2);

        await receive.PostCommand.ExecuteAsync(null);

        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        var unitInDb = await ctx.ProductUnits.FirstAsync(u => u.Id == unitPiece.Id);

        Assert.Equal(53.00m, unitInDb.PurchasePrice);
    }

    [Fact]
    public async Task Conflicting_approved_prices_for_same_unit_are_rejected()
    {
        var (supplier, prodA, _) = await SeedDataAsync();

        var receive = new ReceiveViewModel(_fixture.Services);
        await receive.LoadAsync();
        receive.SelectedSupplier = receive.Suppliers.Single(s => s.Id == supplier.Id);

        var unitPiece = prodA.Units.First(u => u.Name == "قطعة");
        // Line 1 says 52.00, Line 2 says 54.00, both approved to update reference price!
        var line1 = new ReceiveLine
        {
            ProductId = prodA.Id,
            ProductUnitId = unitPiece.Id,
            ProductName = prodA.Name,
            UnitName = unitPiece.Name,
            Quantity = 2m,
            UnitPrice = 52.00m,
            UpdateProductPurchasePrice = true,
        };
        var line2 = new ReceiveLine
        {
            ProductId = prodA.Id,
            ProductUnitId = unitPiece.Id,
            ProductName = prodA.Name,
            UnitName = unitPiece.Name,
            Quantity = 3m,
            UnitPrice = 54.00m,
            UpdateProductPurchasePrice = true,
        };
        receive.Lines.Add(line1);
        receive.Lines.Add(line2);

        await receive.PostCommand.ExecuteAsync(null);

        Assert.Contains("تعارض في تحديث سعر الشراء", receive.Status);
        Assert.NotEmpty(receive.Lines); // Post was prevented!

        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        var unitInDb = await ctx.ProductUnits.FirstAsync(u => u.Id == unitPiece.Id);

        Assert.Equal(50.00m, unitInDb.PurchasePrice); // Unchanged!
    }

    [Fact]
    public async Task Historical_lot_costs_remain_unchanged_when_reference_price_updates()
    {
        var (supplier, prodA, _) = await SeedDataAsync();

        // 1. Post first invoice at 50.00
        var receive1 = new ReceiveViewModel(_fixture.Services);
        await receive1.LoadAsync();
        receive1.SelectedSupplier = receive1.Suppliers.Single(s => s.Id == supplier.Id);
        var pOpt = receive1.Products.Single(p => p.ProductId == prodA.Id);
        receive1.SelectProductCommand.Execute(pOpt);
        await receive1.PostCommand.ExecuteAsync(null);

        using (var scope1 = _fixture.Services.CreateScope())
        {
            var ctx1 = scope1.ServiceProvider.GetRequiredService<DaftarContext>();
            var lot1 = await ctx1.Lots.SingleAsync();
            Assert.Equal(50.00m, lot1.UnitCost);
        }

        // 2. Post second invoice at 55.00 with approved reference price update
        var receive2 = new ReceiveViewModel(_fixture.Services);
        await receive2.LoadAsync();
        receive2.SelectedSupplier = receive2.Suppliers.Single(s => s.Id == supplier.Id);
        var pOpt2 = receive2.Products.Single(p => p.ProductId == prodA.Id);
        receive2.SelectProductCommand.Execute(pOpt2);

        var line2 = receive2.Lines.Single();
        line2.UnitPrice = 55.00m;
        line2.UpdateProductPurchasePrice = true;
        await receive2.PostCommand.ExecuteAsync(null);

        // 3. Verify in database:
        using (var scope2 = _fixture.Services.CreateScope())
        {
            var ctx2 = scope2.ServiceProvider.GetRequiredService<DaftarContext>();
            var lots = await ctx2.Lots.OrderBy(l => l.Id).ToListAsync();

            Assert.Equal(2, lots.Count);
            Assert.Equal(50.00m, lots[0].UnitCost); // Lot 1 cost is unchanged!
            Assert.Equal(55.00m, lots[1].UnitCost); // Lot 2 cost is 55.00

            var unitInDb = await ctx2.ProductUnits.FirstAsync(u => u.Id == line2.ProductUnitId);
            Assert.Equal(55.00m, unitInDb.PurchasePrice); // Reference price is updated to 55.00
        }
    }
}

