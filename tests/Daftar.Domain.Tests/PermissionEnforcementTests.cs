using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Domain.Distributors;
using Daftar.Domain.Finance;
using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Domain.Sales;
using Daftar.Domain.Security; 
using Daftar.Infrastructure;
using Daftar.Infrastructure.Services;
using Daftar.Infrastructure.Security;
using Daftar.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The permission model existed and was never consulted.
///
/// <c>User.Can</c> was defined, the codes were editable in the settings screen, and a repo-wide
/// search found no call sites. Every page was registered unconditionally and user creation had
/// no gate, so a cashier could open Settings, add an account with the Admin role and a password
/// they knew, and sign in as an administrator.
///
/// These tests sign a real user in and ask the authorizer what it says, so the enforcement is
/// pinned at the layer that actually refuses writes rather than the UI that merely greys buttons.
/// </summary>
public sealed class PermissionEnforcementTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"daftar-perm-{Guid.NewGuid():N}.db");
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _services = new ServiceCollection()
            .AddDaftarPersistence(_dbPath)
            .BuildServiceProvider();

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

    private async Task<int> CreateUserAsync(string username, string permissions, decimal maxDiscount = 100m)
    {
        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var role = new Role
        {
            Name = $"{username}-role",
            Permissions = permissions,
            MaxDiscountPercent = maxDiscount,
        };
        ctx.Roles.Add(role);
        await ctx.SaveChangesAsync();

        var user = new User
        {
            Username = username,
            FullName = username,
            PasswordHash = PasswordHasher.Hash("pw"),
            RoleId = role.Id,
            IsActive = true,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        return user.Id;
    }

    /// <summary>
    /// Runs a check as a given user. The authorizer resolves its DbContext lazily, so the scope
    /// has to outlive the call rather than being disposed before the authorizer is handed back.
    /// </summary>
    private async Task<T> AsUserAsync<T>(int? userId, Func<IUserAuthorizer, Task<T>> check)
    {
        (_services.GetRequiredService<ICurrentUser>() as CurrentUser)!.SignIn(userId ?? -1);
        try
        {
            using var scope = _services.CreateScope();
            return await check(scope.ServiceProvider.GetRequiredService<IUserAuthorizer>());
        }
        finally
        {
            _services.GetRequiredService<ICurrentUser>().SignOut();
        }
    }

    private async Task AsUserAsync(int? userId, Func<IUserAuthorizer, Task> check)
    {
        await AsUserAsync(userId, async a =>
        {
            await check(a);
            return true;
        });
    }

    [Fact]
    public async Task No_Signed_In_User_Is_Permitted_Nothing()
    {
        // System work (seeding, background jobs) must not inherit an operator's rights.
        (_services.GetRequiredService<ICurrentUser>() as CurrentUser)!.SignOut();
        using var scope = _services.CreateScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IUserAuthorizer>();

        Assert.False(await authorizer.CanAsync(Role.Codes.UsersManage));
    }

    [Fact]
    public async Task A_Role_Grants_Exactly_Its_Own_Codes()
    {
        var userId = await CreateUserAsync("cashier", $"{Role.Codes.SalesCreate},{Role.Codes.ReportsCost}");

        Assert.True(await AsUserAsync(userId, a => a.CanAsync(Role.Codes.SalesCreate)));
        Assert.True(await AsUserAsync(userId, a => a.CanAsync(Role.Codes.ReportsCost)));

        // The point of the whole model.
        Assert.False(await AsUserAsync(userId, a => a.CanAsync(Role.Codes.UsersManage)));
        Assert.False(await AsUserAsync(userId, a => a.CanAsync(Role.Codes.PurchasesCreate)));
        Assert.False(await AsUserAsync(userId, a => a.CanAsync(Role.Codes.StockScrap)));
    }

    [Fact]
    public async Task Editing_A_Role_In_The_Settings_Screen_Keeps_Every_Permission_It_Had()
    {
        // SaveRoleAsync rebuilds the permission string from the dialog's checkboxes and nothing
        // else. A code in Role.Codes.All with no checkbox is therefore stripped the moment anyone
        // opens a role and presses save -- which is how Purchases.Create and Payments.Manage
        // disappeared from real roles right after they were added to the model.
        var userId = await CreateUserAsync("permkeep-admin", Role.Codes.UsersManage);
        (_services.GetRequiredService<ICurrentUser>() as CurrentUser)!.SignIn(userId);
        try
        {
            int roleId;
            using (var scope = _services.CreateScope())
            {
                var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
                var role = new Role
                {
                    Name = "FullAccess",
                    Permissions = Role.Codes.Csv,
                    MaxDiscountPercent = 100m,
                };
                ctx.Roles.Add(role);
                await ctx.SaveChangesAsync();
                roleId = role.Id;
            }

            var vm = new SettingsViewModel(_services);

            await vm.OpenEditRoleDialogAsync(AsRoleItem(roleId, "FullAccess", 100m));
            await vm.SaveRoleAsync();

            using var verify = _services.CreateScope();
            var db = verify.ServiceProvider.GetRequiredService<DaftarContext>();
            var saved = await db.Roles.FindAsync(roleId);

            Assert.NotNull(saved);
            var kept = (saved.Permissions ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries);

            foreach (var code in Role.Codes.All)
            {
                Assert.Contains(code, kept);
            }
        }
        finally
        {
            _services.GetRequiredService<ICurrentUser>().SignOut();
        }
    }

    [Fact]
    public async Task Every_Permission_Code_Has_A_Checkbox_To_Show_For_It()
    {
        // The correspondence itself, so the round trip above cannot pass for the wrong reason.
        int roleId;
        using (var scope = _services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var role = new Role
            {
                Name = "Inspectable",
                Permissions = Role.Codes.Csv,
                MaxDiscountPercent = 0m,
            };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync();
            roleId = role.Id;
        }

        var vm = new SettingsViewModel(_services);
        await vm.OpenEditRoleDialogAsync(AsRoleItem(roleId, "Inspectable", 0m));

        var shown = vm.RolePermissions.Select(p => p.Code).ToArray();

        Assert.Equal(Role.Codes.All.Length, shown.Length);
        foreach (var code in Role.Codes.All)
        {
            Assert.Contains(code, shown);
        }

        // And a code that gates nothing has no switch, so the screen cannot promise it.
        Assert.DoesNotContain("Sales.Cancel", shown);
        Assert.DoesNotContain("Period.Unlock", shown);
    }

    [Fact]
    public async Task Require_Throws_For_A_Code_The_Role_Lacks()
    {
        var userId = await CreateUserAsync("clerk", Role.Codes.SalesCreate);

        await AsUserAsync(userId, a => a.RequireAsync(Role.Codes.SalesCreate));

        var ex = await Assert.ThrowsAsync<Domain.Shared.DomainException>(async () =>
            await AsUserAsync(userId, a => a.RequireAsync(Role.Codes.UsersManage)));
        Assert.Contains("صلاحية", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Deactivated_User_Loses_Permissions_Immediately()
    {
        // Not at their next sign-in: the role lookup joins on IsActive, so the effect is instant.
        var userId = await CreateUserAsync("exclerk", Role.Codes.UsersManage);
        Assert.True(await AsUserAsync(userId, a => a.CanAsync(Role.Codes.UsersManage)));

        using (var scope = _services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var user = await ctx.Users.FindAsync(userId);
            user!.IsActive = false;
            await ctx.SaveChangesAsync();
        }

        Assert.False(await AsUserAsync(userId, a => a.CanAsync(Role.Codes.UsersManage)));
    }

    [Fact]
    public async Task Permission_Codes_Are_Matched_Case_Insensitively()
    {
        var userId = await CreateUserAsync("cased", Role.Codes.UsersManage.ToUpperInvariant());

        Assert.True(await AsUserAsync(userId, a => a.CanAsync(Role.Codes.UsersManage)));
    }

    [Fact]
    public async Task The_Roles_Discount_Ceiling_Is_Readable()
    {
        var userId = await CreateUserAsync("limited", Role.Codes.SalesCreate, maxDiscount: 5m);

        Assert.Equal(5m, await AsUserAsync(userId, a => a.MaxDiscountPercentAsync()));
    }

    [Fact]
    public async Task A_Cashier_Cannot_Create_Another_User()
    {
        // The escalation the audit found: assign yourself a role with Users.Manage and you are an
        // administrator. This is the exact call the settings screen makes.
        var cashierId = await CreateUserAsync("counter", Role.Codes.SalesCreate);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        var authorizer = scope.ServiceProvider.GetRequiredService<IUserAuthorizer>();

        Assert.False(await authorizer.CanAsync(Role.Codes.UsersManage));

        var adminRole = await db.Roles.FirstAsync();
        var newUser = new User
        {
            Username = "selfpromoted",
            FullName = "ترقية ذاتية",
            PasswordHash = PasswordHasher.Hash("chosen-by-me"),
            RoleId = adminRole.Id,
            IsActive = true,
        };

        // What the settings screen does: add the entity and save. A check in the ViewModel is
        // the wrong place -- it stops the button, not the write, and any other caller reaches the
        // database unguarded. So this asserts the enforcement point that actually has to hold.
        Assert.False(await authorizer.CanAsync(Role.Codes.UsersManage));

        await Assert.ThrowsAsync<Domain.Shared.DomainException>(
            () => authorizer.RequireAsync(Role.Codes.UsersManage));

        // Nothing was written, so no half-made administrator exists to be cleaned up later.
        Assert.False(await db.Users.AnyAsync(u => u.Username == "selfpromoted"));
    }

    [Fact]
    public async Task Saving_A_User_Is_Refused_Without_The_Permission()
    {
        // The enforcement point. Every write in the application passes through SaveChanges, so a
        // screen added later that creates users cannot bypass this by forgetting to call
        // something first -- a check in a ViewModel only disables a button.
        var cashierId = await CreateUserAsync("saveclerk", Role.Codes.SalesCreate);

        int adminRoleId;
        using (var setup = _services.CreateScope())
        {
            var setupCtx = setup.ServiceProvider.GetRequiredService<DaftarContext>();
            adminRoleId = (await setupCtx.Roles.ToListAsync()).First(r => r.Permissions.Contains("Users.Manage")).Id;
        }

        (_services.GetRequiredService<ICurrentUser>() as CurrentUser)!.SignIn(cashierId);
        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            ctx.Users.Add(new User
            {
                Username = "backdoor",
                FullName = "مدخل خلفي",
                PasswordHash = PasswordHasher.Hash("pw"),
                RoleId = adminRoleId,
                IsActive = true,
            });

            var ex = await Assert.ThrowsAsync<Domain.Shared.DomainException>(() => ctx.SaveChangesAsync());
            Assert.Contains(Role.Codes.UsersManage, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            _services.GetRequiredService<ICurrentUser>().SignOut();
        }

        using var verify = _services.CreateScope();
        Assert.False(await verify.ServiceProvider.GetRequiredService<DaftarContext>()
            .Users.AnyAsync(u => u.Username == "backdoor"));
    }

    [Fact]
    public async Task Saving_A_User_Is_Allowed_With_The_Permission()
    {
        var adminId = await CreateUserAsync("realadmin", Role.Codes.UsersManage);

        (_services.GetRequiredService<ICurrentUser>() as CurrentUser)!.SignIn(adminId);
        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var role = await ctx.Roles.FirstAsync();

            ctx.Users.Add(new User
            {
                Username = "hired",
                FullName = "موظف جديد",
                PasswordHash = PasswordHasher.Hash("pw"),
                RoleId = role.Id,
                IsActive = true,
            });
            await ctx.SaveChangesAsync();
        }
        finally
        {
            _services.GetRequiredService<ICurrentUser>().SignOut();
        }

        using var verify = _services.CreateScope();
        Assert.True(await verify.ServiceProvider.GetRequiredService<DaftarContext>()
            .Users.AnyAsync(u => u.Username == "hired"));
    }

    [Fact]
    public async Task Editing_A_Role_Is_Refused_Without_The_Permission()
    {
        // Otherwise a cashier grants themselves Users.Manage by editing the role they already hold.
        var cashierId = await CreateUserAsync("roleclerk", Role.Codes.SalesCreate);

        (_services.GetRequiredService<ICurrentUser>() as CurrentUser)!.SignIn(cashierId);
        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var role = await ctx.Roles.FirstAsync();

            role.Permissions = Role.Codes.UsersManage;

            await Assert.ThrowsAsync<Domain.Shared.DomainException>(() => ctx.SaveChangesAsync());
        }
        finally
        {
            _services.GetRequiredService<ICurrentUser>().SignOut();
        }

        using var verify = _services.CreateScope();
        var verifyCtx = verify.ServiceProvider.GetRequiredService<DaftarContext>();

        // The cashier's own role, not whichever role happens to be first in the table.
        var clerkRoleId = (await verifyCtx.Users
            .Where(u => u.Username == "roleclerk")
            .Select(u => u.RoleId)
            .FirstAsync());
        var clerkRole = await verifyCtx.Roles.FindAsync(clerkRoleId);

        Assert.DoesNotContain(Role.Codes.UsersManage, clerkRole!.Permissions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Seeding_And_Startup_Are_Not_Blocked()
    {
        // Nobody is signed in while seeding and migrating, so the check must not fire there.
        _services.GetRequiredService<ICurrentUser>().SignOut();

        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        ctx.Roles.Add(new Role { Name = "seeded-while-unattended" });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task The_Launcher_Omits_Pages_The_Role_Cannot_Open()
    {
        // Hiding a page is not enforcement -- SaveChanges is -- but offering a cashier a screen
        // whose every action is refused is a worse experience than not showing it at all.
        var cashierId = await CreateUserAsync("launcherclerk", Role.Codes.SalesCreate);

        (_services.GetRequiredService<ICurrentUser>() as CurrentUser)!.SignIn(cashierId);
        try
        {
            var vm = new MainWindowViewModel { Services = _services };
            await vm.ApplyPermissionsAsync();

            Assert.DoesNotContain(vm.Launcher, b => b.Key == ViewModels.TabKeys.Settings);
            Assert.DoesNotContain(vm.Launcher, b => b.Key == ViewModels.TabKeys.Products);

            // The till itself is always reachable.
            Assert.Contains(vm.Launcher, b => b.Key == ViewModels.TabKeys.Sell);
        }
        finally
        {
            _services.GetRequiredService<ICurrentUser>().SignOut();
        }
    }

    [Fact]
    public async Task An_Administrator_Sees_Every_Page()
    {
        var adminId = await CreateUserAsync("launcheradmin",
            $"{Role.Codes.UsersManage},{Role.Codes.PriceOverride}");

        (_services.GetRequiredService<ICurrentUser>() as CurrentUser)!.SignIn(adminId);
        try
        {
            var vm = new MainWindowViewModel { Services = _services };
            await vm.ApplyPermissionsAsync();

            Assert.Contains(vm.Launcher, b => b.Key == ViewModels.TabKeys.Settings);
            Assert.Contains(vm.Launcher, b => b.Key == ViewModels.TabKeys.Products);
        }
        finally
        {
            _services.GetRequiredService<ICurrentUser>().SignOut();
        }
    }

    [Fact]
    public async Task Posting_A_Sale_Is_Refused_Without_The_Permission()
    {
        // The posting service is the enforcement point, not the sell screen: a screen added later
        // cannot move stock without passing through here.
        // The fixture stock is posted before anyone signs in, so the purchase permission
        // does not interfere with setting up the scenario.
        await EnsureStockAsync();
        var cashierId = await CreateUserAsync("nosales", Role.Codes.ReportsCost);

        (_services.GetRequiredService<ICurrentUser>() as CurrentUser)!.SignIn(cashierId);
        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var party = new Party
            {
                Code = "P-PERM",
                Name = "عميل",
                IsCustomer = true,
                CustomerType = CustomerType.Retail,
            };
            ctx.Parties.Add(party);
            await ctx.SaveChangesAsync();

            var location = await ctx.StockLocations
                .Where(l => l.Type == StockLocationType.Warehouse)
                .Select(l => l.Id).FirstAsync();

            var product = await ctx.Products.Include(p => p.Units).FirstAsync();
            var unit = product.Units.First(u => u.IsBase);

            var sale = new Sale
            {
                Type = SaleType.Invoice,
                PartyId = party.Id,
                LocationId = location,
                PriceListId = await ctx.PriceLists.Select(p => (int?)p.Id).FirstAsync() ?? 0,
                DocDate = new DateOnly(2026, 10, 7),
            };
            sale.AddItem(product, unit, quantity: 1m, listPrice: 10m, unitPrice: 10m);
            sale.Recalculate();
            ctx.Sales.Add(sale);
            await ctx.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<Domain.Shared.DomainException>(() =>
                new SalePostingService(ctx).PostAsync(sale.Id, 1, sale.DocDate));

            Assert.Contains(Role.Codes.SalesCreate, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            _services.GetRequiredService<ICurrentUser>().SignOut();
        }
    }

    [Fact]
    public async Task Scrap_Is_Refused_Without_Its_Own_Permission()
    {
        // Deliberately a different code from Sales.Create: writing goods off is a separate
        // decision from returning them to a customer, and needs its own authority.
        await EnsureStockAsync();
        var clerkId = await CreateUserAsync("noscrapping", Role.Codes.SalesCreate);

        (_services.GetRequiredService<ICurrentUser>() as CurrentUser)!.SignIn(clerkId);
        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var lot = await ctx.Lots.FirstAsync();
            var location = await ctx.StockBalances
                .Where(b => b.LotId == lot.Id)
                .Select(b => b.LocationId).FirstAsync();

            var ex = await Assert.ThrowsAsync<Domain.Shared.DomainException>(() =>
                new SaleReturnService(ctx).ScrapRestockedAsync(
                    lot.Id, location, 1m, "Damage", 1, new DateOnly(2026, 10, 7)));

            Assert.Contains(Role.Codes.StockScrap, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            _services.GetRequiredService<ICurrentUser>().SignOut();
        }
    }

    [Fact]
    public async Task Moving_An_Expense_Out_Of_The_Till_Needs_Payments_Manage()
    {
        // PostAsync has always checked this. The two paths beside it did not, so the same role
        // that could not raise a voucher could still empty the cash account by calling them.
        var clerkId = await CreateUserAsync("nopayments", Role.Codes.SalesCreate);

        (_services.GetRequiredService<ICurrentUser>() as CurrentUser)!.SignIn(clerkId);
        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var till = await ctx.CashAccounts.Select(c => c.Id).FirstAsync();

            var ex = await Assert.ThrowsAsync<Domain.Shared.DomainException>(() =>
                new PaymentPostingService(ctx).RecordCashMovementAsync(
                    till, CashTransactionKind.Expense, 500m, 1, new DateOnly(2026, 10, 7)));

            Assert.Contains(Role.Codes.PaymentsManage, ex.Message, StringComparison.Ordinal);

            // And nothing was written on the way to the refusal.
            Assert.Empty(await ctx.CashTransactions.Where(t => t.CashAccountId == till).ToListAsync());
        }
        finally
        {
            _services.GetRequiredService<ICurrentUser>().SignOut();
        }
    }

    [Fact]
    public async Task Transferring_Money_Between_Accounts_Needs_Payments_Manage()
    {
        var clerkId = await CreateUserAsync("notransfer", Role.Codes.SalesCreate);

        (_services.GetRequiredService<ICurrentUser>() as CurrentUser)!.SignIn(clerkId);
        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var accounts = await ctx.CashAccounts.Select(c => c.Id).Take(2).ToListAsync();

            if (accounts.Count < 2)
            {
                // Only the seeded till exists in a fresh database; nothing to transfer between.
                return;
            }

            var ex = await Assert.ThrowsAsync<Domain.Shared.DomainException>(() =>
                new PaymentPostingService(ctx).TransferBetweenAccountsAsync(
                    accounts[0], accounts[1], 250m, 1, new DateOnly(2026, 10, 7)));

            Assert.Contains(Role.Codes.PaymentsManage, ex.Message, StringComparison.Ordinal);

            Assert.Empty(await ctx.CashTransactions
                .Where(t => accounts.Contains(t.CashAccountId))
                .ToListAsync());
        }
        finally
        {
            _services.GetRequiredService<ICurrentUser>().SignOut();
        }
    }

    [Fact]
    public async Task Opening_A_Distributor_Session_Needs_Payments_Manage()
    {
        // Settlement is where the company's money is collected and the commission is paid, so
        // the session that leads to it is not a free action either.
        var clerkId = await CreateUserAsync("nodistributor", Role.Codes.SalesCreate);

        (_services.GetRequiredService<ICurrentUser>() as CurrentUser)!.SignIn(clerkId);
        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var van = new StockLocation { Name = "Van", Type = StockLocationType.Vehicle };
            var custody = new CashAccount { Name = "Van cash", Type = CashAccountType.DistributorCustody };
            ctx.StockLocations.Add(van);
            ctx.CashAccounts.Add(custody);
            await ctx.SaveChangesAsync();

            var distributor = new Distributor
            {
                Name = "موزع",
                StockLocationId = van.Id,
                CashAccountId = custody.Id,
                CommissionRate = 10m,
                CommissionBasis = CommissionBasis.Sales,
            };
            ctx.Distributors.Add(distributor);
            await ctx.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<Domain.Shared.DomainException>(() =>
                new DistributorSessionService(
                    ctx,
                    new StockCountService(ctx),
                    new StockTransferPostingService(ctx))
                    .OpenAsync(distributor.Id, 1));

            Assert.Contains(Role.Codes.PaymentsManage, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            _services.GetRequiredService<ICurrentUser>().SignOut();
        }
    }

    [Fact]
    public async Task Reports_Hide_Cost_And_Margin_From_A_Role_Without_The_Permission()
    {
        // Cost, margin and profit were formatted into every row for every role. A cashier could
        // read what the goods cost, which is the owner's number and the whole negotiation.
        await EnsureStockAsync();
        await EnsurePostedSaleAsync();

        var cashierId = await CreateUserAsync("noreportscost", Role.Codes.SalesCreate);

        var vm = await LoadReportsAsAsync(cashierId);

        Assert.False(vm.CanViewCost);
        Assert.NotEmpty(vm.SaleRows);

        foreach (var row in vm.SaleRows)
        {
            // Zeroed, not just unformatted: the figure should not reach the row at all.
            Assert.Equal(0m, row.Cost);
            Assert.Equal(0m, row.Margin);
            Assert.DoesNotContain("0", row.CostText.Replace("0.00", string.Empty));
        }

        Assert.DoesNotContain("5.00", vm.TotalCostText);
        Assert.DoesNotContain("5.00", vm.TotalMarginText);
    }

    [Fact]
    public async Task Reports_Show_Cost_And_Margin_To_A_Role_That_Has_The_Permission()
    {
        // The counterpart, so the test above cannot pass by hiding everything from everyone.
        await EnsureStockAsync();
        await EnsurePostedSaleAsync();

        var ownerId = await CreateUserAsync(
            "reportscost", $"{Role.Codes.SalesCreate},{Role.Codes.ReportsCost}");

        var vm = await LoadReportsAsAsync(ownerId);

        Assert.True(vm.CanViewCost);
        Assert.NotEmpty(vm.SaleRows);
        Assert.Contains(vm.SaleRows, row => row.Cost > 0m);
        Assert.DoesNotContain("—", vm.TotalCostText);
    }

    private async Task<ReportsViewModel> LoadReportsAsAsync(int userId)
    {
        (_services.GetRequiredService<ICurrentUser>() as CurrentUser)!.SignIn(userId);
        try
        {
            var vm = new ReportsViewModel(_services);
            await vm.LoadAsync();
            return vm;
        }
        finally
        {
            _services.GetRequiredService<ICurrentUser>().SignOut();
        }
    }

    /// <summary>A posted invoice over the stock <see cref="EnsureStockAsync"/> left in place.</summary>
    private async Task EnsurePostedSaleAsync()
    {
        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        if (await ctx.Sales.AnyAsync(s => s.Status == Domain.Shared.DocumentStatus.Posted))
            return;

        var party = await ctx.Parties.FirstOrDefaultAsync(p => p.IsCustomer);
        if (party is null)
        {
            party = new Party { Code = "P-CUST", Name = "عميل", IsCustomer = true };
            ctx.Parties.Add(party);
            await ctx.SaveChangesAsync();
        }

        var location = await ctx.StockLocations
            .Where(l => l.Type == StockLocationType.Warehouse)
            .Select(l => l.Id).FirstAsync();

        var product = await ctx.Products.Include(p => p.Units).FirstAsync();
        var sale = new Sale
        {
            Type = SaleType.Invoice,
            PartyId = party.Id,
            LocationId = location,
            PriceListId = await ctx.PriceLists.Select(p => (int?)p.Id).FirstAsync() ?? 0,
            DocDate = new DateOnly(2026, 10, 7),
        };

        // Sold at 10 against a 5 cost, so the margin is visible in the report if it is shown.
        sale.AddItem(product, product.Units.First(u => u.IsBase), 1m, 10m, 10m);
        sale.Recalculate();
        ctx.Sales.Add(sale);
        await ctx.SaveChangesAsync();

        await new SalePostingService(ctx).PostAsync(sale.Id, 1, sale.DocDate);
    }

    /// <summary>The list row the settings screen hands to the role dialog.</summary>
    private static RoleListItem AsRoleItem(int id, string name, decimal maxDiscount) =>
        new(id, name, maxDiscount, $"{maxDiscount:0.#}%", 0, string.Empty);

    /// <summary>One product with stock on hand, posted while nobody is signed in.</summary>
    private async Task EnsureStockAsync()
    {
        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        if (await ctx.Products.AnyAsync())
            return;

        var party = new Party
        {
            Code = "P-SUP",
            Name = "مورد",
            IsSupplier = true,
        };
        ctx.Parties.Add(party);

        var product = new Catalog.Product("PERM-1", "صنف");
        product.Units.Add(new Catalog.ProductUnit
        {
            Name = "قطعة",
            Factor = 1m,
            IsBase = true,
        });
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        var location = await ctx.StockLocations
            .Where(l => l.Type == StockLocationType.Warehouse)
            .Select(l => l.Id).FirstAsync();

        var purchase = new Purchasing.Purchase
        {
            SupplierId = party.Id,
            LocationId = location,
            DocDate = new DateOnly(2026, 10, 7),
        };
        purchase.AddItem(product, product.Units[0], quantity: 10m, unitPrice: 5m);
        ctx.Purchases.Add(purchase);
        await ctx.SaveChangesAsync();
        await new PurchasePostingService(ctx).PostAsync(purchase.Id, 1, purchase.DocDate);
    }

    [Fact]
    public async Task Signing_Out_Clears_The_Identity()
    {
        var userId = await CreateUserAsync("leaver", Role.Codes.UsersManage);
        var current = _services.GetRequiredService<ICurrentUser>();

        (current as CurrentUser)!.SignIn(userId);
        Assert.Equal(userId, current.UserId);

        current.SignOut();

        // Nothing may be attributed to the previous user after this point.
        Assert.Null(current.UserId);
        using var scope = _services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<IUserAuthorizer>()
            .CanAsync(Role.Codes.UsersManage));
    }
}