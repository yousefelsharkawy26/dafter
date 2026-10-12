using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Domain.Catalog;
using Daftar.Domain.Parties;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Sales;
using Daftar.Domain.Security;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// Every posting service in the app was called with a literal <c>userId: 1</c>, sixteen times over,
/// across six view models. The audit interceptor had it right all along -- it stamps
/// <c>CreatedBy</c> from the signed-in user -- but the document's own "posted by" stamp came from
/// the argument, so every sale, purchase, payment, transfer and count in the shop records that
/// the seeded admin posted it.
///
/// That is worse than a missing column. A blank one says nobody knows; a filled one with the
/// wrong name in it says something false with the authority of a number.
/// </summary>
public sealed class PostingAttributionTests : IAsyncLifetime, IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 7);

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"daftar-who-{Guid.NewGuid():N}.db");

    private ServiceProvider _services = null!;

    private int _locationId;
    private int _productId;
    private int _unitId;
    private int _listId;
    private bool _stocked;

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
        // Sign out first: the identity is AsyncLocal, and leaving it set would leak into whatever
        // runs next in this process.
        (_services.GetService(typeof(ICurrentUser)) as CurrentUser)?.SignOut();
        SqliteConnection.ClearAllPools();
        (_services as IDisposable)?.Dispose();
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(p))
                File.Delete(p);
        GC.SuppressFinalize(this);
    }

    private DaftarContext NewContext() => _services.CreateScope()
        .ServiceProvider.GetRequiredService<DaftarContext>();

    private void SignInAs(int userId)
        => ((CurrentUser)_services.GetRequiredService<ICurrentUser>()).SignIn(userId);

    /// <summary>A second user, so "who posted" has somewhere real to point.</summary>
    private async Task<int> MakeCashierAsync()
    {
        var ctx = NewContext();
        var role = ctx.Roles.First(r => r.Name == "Admin");

        var user = new User
        {
            Username = "cashier",
            FullName = "Cashier",
            PasswordHash = PasswordHasher.Hash("secret"),
            RoleId = role.Id,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = 1,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private async Task EnsureStockAsync()
    {
        if (_stocked) return;
        _stocked = true;

        var ctx = NewContext();

        var supplier = new Party { Code = "SUP-W", Name = "Supplier", IsSupplier = true };
        var product = new Product("WHO-1", "Sold");
        var unit = new ProductUnit { Name = "piece", Factor = 1m, IsBase = true };
        product.Units.Add(unit);
        ctx.Parties.Add(supplier);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        _productId = product.Id;
        _unitId = unit.Id;
        _listId = (await ctx.PriceLists.SingleAsync(p => p.IsDefault)).Id;
        _locationId = await ctx.StockLocations.Select(l => l.Id).SingleAsync();

        var purchase = new Purchase { SupplierId = supplier.Id, LocationId = _locationId, DocDate = Today };
        ctx.Purchases.Add(purchase);
        await ctx.SaveChangesAsync();
        purchase.AddItem(product, unit, 100m, 5m);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(purchase.Id, 1, Today);
    }

    private async Task<Sale> SellAsync()
    {
        await EnsureStockAsync();
        var ctx = NewContext();

        var customer = new Party
        {
            Code = "C-WHO",
            Name = "Buyer",
            IsCustomer = true,
            CustomerType = CustomerType.Retail,
            CreditLimit = 100_000m,
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
        sale.AddItem(product, unit, 2m, 10m, 10m);
        sale.Recalculate();
        await ctx.SaveChangesAsync();

        await new SalePostingService(ctx).PostAsync(sale.Id, 1, Today);
        return sale;
    }

    // ---------- the rule ----------

    [Fact]
    public async Task A_Sale_Records_Who_Actually_Posted_It()
    {
        // The call still says 1, exactly as all sixteen of them do.
        var cashierId = await MakeCashierAsync();
        SignInAs(cashierId);

        var sale = await SellAsync();

        var postedBy = await NewContext().Sales
            .Where(s => s.Id == sale.Id)
            .Select(s => s.PostedBy)
            .FirstAsync();

        Assert.Equal(cashierId, postedBy);
    }

    [Fact]
    public async Task The_Audit_Trail_And_The_Document_Say_The_Same_Thing()
    {
        // These were correct and wrong at the same time, which is how it went unnoticed: the log
        // named the cashier while the document named the admin.
        var cashierId = await MakeCashierAsync();
        SignInAs(cashierId);

        var sale = await SellAsync();
        var ctx = NewContext();

        var postedBy = await ctx.Sales.Where(s => s.Id == sale.Id).Select(s => s.PostedBy).FirstAsync();
        var createdBy = await ctx.Sales.Where(s => s.Id == sale.Id).Select(s => s.CreatedBy).FirstAsync();

        Assert.Equal(postedBy, createdBy);
    }

    [Fact]
    public async Task With_Nobody_Signed_In_The_Caller_Still_Stands()
    {
        // Seeding, migrations and the tests all post with nobody signed in. Falling back to what
        // the caller passed keeps those working and keeps 0 meaning "unknown", the same word the
        // interceptor already uses.
        var sale = await SellAsync();

        var postedBy = await NewContext().Sales
            .Where(s => s.Id == sale.Id)
            .Select(s => s.PostedBy)
            .FirstAsync();

        Assert.Equal(1, postedBy);
    }
}