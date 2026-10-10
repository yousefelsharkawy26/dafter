using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Domain.Security;
using Daftar.Infrastructure;
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
        Assert.False(await AsUserAsync(userId, a => a.CanAsync(Role.Codes.PeriodUnlock)));
        Assert.False(await AsUserAsync(userId, a => a.CanAsync(Role.Codes.StockScrap)));
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