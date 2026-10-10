using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Domain.Catalog;
using Daftar.Domain.Parties;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Sales;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.Infrastructure.Services;
using Daftar.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

public sealed class ReturnsScreenTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"daftar-returns-test-{Guid.NewGuid():N}.db");

    private ServiceProviderFixture _fixture = null!;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

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

    private async Task<(int SaleId, string DocNo, int CustomerId)> SeedInvoiceWithTwoItemsAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var supplier = new Party { Code = "SUP-RET", Name = "مورد رئيسي", IsSupplier = true };
        var customer = new Party
        {
            Code = "CUST-RET",
            Name = "عميل المرتجع",
            Phone = "01012345678",
            IsCustomer = true,
            CustomerType = CustomerType.Retail
        };
        ctx.Parties.AddRange(supplier, customer);

        var list = await ctx.PriceLists.SingleAsync(p => p.IsDefault);
        var location = await ctx.StockLocations.Select(l => l.Id).FirstAsync();

        var product1 = new Product("RET-PROD-1", "منتج مرتجع 1");
        var unit1 = new ProductUnit { Name = "قطعة", Factor = 1m, IsBase = true, PurchasePrice = 10m };
        product1.Units.Add(unit1);

        var product2 = new Product("RET-PROD-2", "منتج مرتجع 2");
        var unit2 = new ProductUnit { Name = "علبة", Factor = 1m, IsBase = true, PurchasePrice = 20m };
        product2.Units.Add(unit2);

        ctx.Products.AddRange(product1, product2);
        await ctx.SaveChangesAsync();

        unit1.ProductId = product1.Id;
        unit2.ProductId = product2.Id;

        // Purchase & receive stock
        var purchase = new Purchase { SupplierId = supplier.Id, LocationId = location, DocDate = Today };
        ctx.Purchases.Add(purchase);
        await ctx.SaveChangesAsync();

        purchase.AddItem(product1, unit1, 50m, 10m);
        purchase.AddItem(product2, unit2, 50m, 20m);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(purchase.Id, 1, Today);

        // Create sale invoice
        var sale = new Sale
        {
            Type = SaleType.Invoice,
            PartyId = customer.Id,
            LocationId = location,
            PriceListId = list.Id,
            DocDate = Today
        };
        ctx.Sales.Add(sale);
        await ctx.SaveChangesAsync();

        sale.AddItem(product1, unit1, 10m, 15m, 15m);
        sale.AddItem(product2, unit2, 5m, 30m, 30m);
        await ctx.SaveChangesAsync();

        var postResult = await new SalePostingService(ctx).PostAsync(sale.Id, 1, Today);
        return (sale.Id, postResult.DocNo, customer.Id);
    }

    [Fact]
    public async Task StartCreateReturn_finds_invoices_and_populates_candidate_lines()
    {
        var seeded = await SeedInvoiceWithTwoItemsAsync();
        var vm = new ReturnsViewModel(_fixture.Services);
        await vm.InitializeAsync();

        await vm.StartCreateReturnCommand.ExecuteAsync(null);
        Assert.True(vm.IsCreatingReturn);
        Assert.NotEmpty(vm.CandidateInvoices);

        var candidate = vm.CandidateInvoices.First(c => c.SaleId == seeded.SaleId);
        Assert.Equal(seeded.DocNo, candidate.DocNo);
        Assert.Equal("عميل المرتجع", candidate.PartyName);

        // Select the invoice
        await vm.SelectInvoiceCommand.ExecuteAsync(candidate);
        Assert.NotNull(vm.SelectedInvoice);
        Assert.Equal(2, vm.ReturnCandidateLines.Count);

        var line1 = vm.ReturnCandidateLines.First(l => l.ProductName == "منتج مرتجع 1");
        Assert.Equal(10m, line1.OriginalQuantity);
        Assert.Equal(10m, line1.MaxReturnableQuantity);
        Assert.Equal(15m, line1.UnitPrice);

        var line2 = vm.ReturnCandidateLines.First(l => l.ProductName == "منتج مرتجع 2");
        Assert.Equal(5m, line2.OriginalQuantity);
        Assert.Equal(5m, line2.MaxReturnableQuantity);
        Assert.Equal(30m, line2.UnitPrice);

        // Initial refund amount for both full items: (10*15) + (5*30) = 150 + 150 = 300
        Assert.Equal(300m, vm.TotalRefundAmount);
        Assert.True(vm.CanConfirmReturn);
    }

    [Fact]
    public async Task Partial_quantity_return_creates_return_and_updates_history()
    {
        var seeded = await SeedInvoiceWithTwoItemsAsync();
        var vm = new ReturnsViewModel(_fixture.Services);
        await vm.InitializeAsync();

        await vm.StartCreateReturnCommand.ExecuteAsync(null);
        var candidate = vm.CandidateInvoices.First(c => c.SaleId == seeded.SaleId);
        await vm.SelectInvoiceCommand.ExecuteAsync(candidate);

        // Return only 2 pieces of line 1, and 0 pieces of line 2
        var line1 = vm.ReturnCandidateLines.First(l => l.ProductName == "منتج مرتجع 1");
        line1.ReturnQuantity = 2m;

        var line2 = vm.ReturnCandidateLines.First(l => l.ProductName == "منتج مرتجع 2");
        line2.IsSelected = false; // Deselect line 2

        // Expected refund amount: 2 * 15 = 30
        Assert.Equal(30m, vm.TotalRefundAmount);
        Assert.True(vm.CanConfirmReturn);

        vm.ReturnNotes = "إرجاع قطعتين فقط لوجود زيادة بالطلب";
        await vm.ConfirmAndPostReturnCommand.ExecuteAsync(null);

        Assert.False(vm.IsCreatingReturn);
        Assert.NotEmpty(vm.Rows);

        var returnRow = vm.Rows.First(r => r.OriginalDocNo == seeded.DocNo);
        Assert.Equal(30m, returnRow.Total);

        // Inspect details
        await vm.SelectReturnRowCommand.ExecuteAsync(returnRow);
        Assert.True(vm.IsDetailsPanelOpen);
        Assert.NotNull(vm.SelectedReturnDetails);
        Assert.Single(vm.SelectedReturnDetails.Items);
        Assert.Equal(2m, vm.SelectedReturnDetails.Items[0].Quantity);
        Assert.Equal(30m, vm.SelectedReturnDetails.Total);
    }
}

