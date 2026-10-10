using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Domain.Security;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// Password hashes must never reach the audit trail.
///
/// The interceptor serialised every changed column of an audited entity, and User was audited, so
/// creating a user or changing a password wrote the full pbkdf2 string into AuditLogs.NewValues.
/// That table is append-only at the database level: the copy was permanent, changing the password
/// again did not remove the old one, and the trigger blocks deleting the row.
/// </summary>
public sealed class AuditLogRedactionTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"daftar-audit-{Guid.NewGuid():N}.db");
    private ServiceProvider _services = null!;

    private static readonly DateOnly DocDate = new(2026, 10, 7);

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

    private DaftarContext NewContext() => _services.CreateScope()
        .ServiceProvider.GetRequiredService<DaftarContext>();

    private static async Task<Domain.Security.Role> RoleAsync(DaftarContext ctx, string name)
    {
        var role = new Domain.Security.Role { Name = name, Permissions = "UsersManage" };
        ctx.Roles.Add(role);
        await ctx.SaveChangesAsync();
        return role;
    }

    [Fact]
    public async Task Creating_A_User_Does_Not_Copy_The_Hash_Into_The_Audit_Log()
    {
        var ctx = NewContext();
        var role = await RoleAsync(ctx, "Cashier");

        var user = new User
        {
            Username = "cashier1",
            FullName = "كاشير",
            PasswordHash = PasswordHasher.Hash("secret123"),
            RoleId = role.Id,
            IsActive = true,
        };

        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        var audit = await ctx.AuditLogs
            .Where(a => a.EntityName == nameof(User))
            .OrderByDescending(a => a.Id)
            .FirstAsync();

        Assert.NotNull(audit.NewValues);
        Assert.DoesNotContain("pbkdf2-sha256", audit.NewValues, StringComparison.Ordinal);
        Assert.DoesNotContain(user.PasswordHash, audit.NewValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_A_Password_Does_Not_Copy_The_New_Hash_Into_The_Audit_Log()
    {
        var ctx = NewContext();
        var role = await RoleAsync(ctx, "Cashier");

        var user = new User
        {
            Username = "cashier2",
            FullName = "كاشير",
            PasswordHash = PasswordHasher.Hash("old-secret"),
            RoleId = role.Id,
            IsActive = true,
        };

        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        var newHash = PasswordHasher.Hash("new-secret");
        user.PasswordHash = newHash;
        await ctx.SaveChangesAsync();

        var audits = await ctx.AuditLogs
            .Where(a => a.EntityName == nameof(User))
            .ToListAsync();

        foreach (var audit in audits)
        {
            if (audit.NewValues is null) continue;
            Assert.DoesNotContain("pbkdf2-sha256", audit.NewValues, StringComparison.Ordinal);
            Assert.DoesNotContain(newHash, audit.NewValues, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_Audit_Trail_Still_Records_That_The_Password_Changed()
    {
        // Redaction must not become silence: knowing a credential changed is the whole point.
        var ctx = NewContext();
        var role = await RoleAsync(ctx, "Cashier");

        var user = new User
        {
            Username = "cashier3",
            FullName = "كاشير",
            PasswordHash = PasswordHasher.Hash("first"),
            RoleId = role.Id,
            IsActive = true,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        user.PasswordHash = PasswordHasher.Hash("second");
        await ctx.SaveChangesAsync();

        var audit = await ctx.AuditLogs
            .Where(a => a.EntityName == nameof(User) && a.Action == AuditAction.Update)
            .OrderByDescending(a => a.Id)
            .FirstAsync();

        Assert.NotNull(audit.NewValues);
        Assert.Contains("PasswordHash", audit.NewValues, StringComparison.Ordinal);
        Assert.Contains("redacted", audit.NewValues, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ordinary_User_Fields_Are_Still_Audited()
    {
        // The deny-list must not swallow the rest of the trail.
        var ctx = NewContext();
        var role = await RoleAsync(ctx, "Cashier");

        var user = new User
        {
            Username = "cashier4",
            FullName = "كاشير",
            PasswordHash = PasswordHasher.Hash("pw"),
            RoleId = role.Id,
            IsActive = true,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        var audit = await ctx.AuditLogs
            .Where(a => a.EntityName == nameof(User))
            .OrderByDescending(a => a.Id)
            .FirstAsync();

        Assert.Contains(nameof(User.FullName), audit.NewValues!, StringComparison.Ordinal);
        Assert.Contains(nameof(User.IsActive), audit.NewValues!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Update_Records_What_The_Value_Was_Before()
    {
        // The trail said "FullName changed" and never said what it had been. That is a log, not
        // an account: nobody can reconstruct who was owed what, or argue the point.
        var ctx = NewContext();
        var role = await RoleAsync(ctx, "Manager");

        var user = new User
        {
            Username = "auditor1",
            FullName = "الاسم القديم",
            PasswordHash = PasswordHasher.Hash("pw"),
            RoleId = role.Id,
            IsActive = true,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        user.FullName = "الاسم الجديد";
        await ctx.SaveChangesAsync();

        var audit = await ctx.AuditLogs
            .Where(a => a.EntityName == nameof(User) && a.Action == AuditAction.Update)
            .OrderByDescending(a => a.Id)
            .FirstAsync();

        Assert.NotNull(audit.OldValues);
        Assert.Contains("الاسم القديم", audit.OldValues, StringComparison.Ordinal);
        Assert.DoesNotContain("الاسم الجديد", audit.OldValues, StringComparison.Ordinal);

        // And the new side still says what it became.
        Assert.Contains("الاسم الجديد", audit.NewValues!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Delete_Records_The_Whole_Row_It_Took_With_It()
    {
        // A delete has no "after". If the row is not captured on the way out, the information
        // is simply gone -- there is no trigger and no journal to recover it from.
        var ctx = NewContext();
        var role = await RoleAsync(ctx, "Temp");

        var user = new User
        {
            Username = "doomed",
            FullName = "حساب هيتشال",
            PasswordHash = PasswordHasher.Hash("pw"),
            RoleId = role.Id,
            IsActive = true,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        ctx.Users.Remove(user);
        await ctx.SaveChangesAsync();

        var audit = await ctx.AuditLogs
            .Where(a => a.EntityName == nameof(User) && a.Action == AuditAction.Delete)
            .OrderByDescending(a => a.Id)
            .FirstAsync();

        Assert.NotNull(audit.OldValues);
        Assert.Contains("doomed", audit.OldValues, StringComparison.Ordinal);
        Assert.Contains("حساب هيتشال", audit.OldValues, StringComparison.Ordinal);
        Assert.Null(audit.NewValues);
    }

    [Fact]
    public async Task An_Update_Keeps_The_Old_Hash_Out_Of_The_Audit_Log()
    {
        // Filling OldValues opened a second route to the same secret. The deny-list has to cover
        // the original side as well, not just the current one.
        var ctx = NewContext();
        var role = await RoleAsync(ctx, "Rotating");

        var user = new User
        {
            Username = "rotator",
            FullName = "كاشير",
            PasswordHash = PasswordHasher.Hash("first-secret"),
            RoleId = role.Id,
            IsActive = true,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        var oldHash = user.PasswordHash;
        user.PasswordHash = PasswordHasher.Hash("second-secret");
        await ctx.SaveChangesAsync();

        var audit = await ctx.AuditLogs
            .Where(a => a.EntityName == nameof(User) && a.Action == AuditAction.Update)
            .OrderByDescending(a => a.Id)
            .FirstAsync();

        Assert.NotNull(audit.OldValues);
        Assert.DoesNotContain("pbkdf2-sha256", audit.OldValues, StringComparison.Ordinal);
        Assert.DoesNotContain(oldHash, audit.OldValues, StringComparison.Ordinal);
        Assert.Contains(nameof(User.PasswordHash), audit.OldValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Update_Names_What_Changed_And_Not_The_Stamp_Columns()
    {
        // The stamp columns are set by the interceptor itself. EF has already computed the
        // modified set by the time SavingChanges runs, so they are not flagged as modified today
        // and this exclusion is currently inert -- removing it changes nothing, which was checked.
        // It stays because a future EF that re-detects after the interceptor would otherwise
        // start writing "UpdatedAt changed, UpdatedAt changed" into every row of the trail.
        var ctx = NewContext();
        var role = await RoleAsync(ctx, "NoOp");

        var user = new User
        {
            Username = "noop",
            FullName = "كاشير",
            PasswordHash = PasswordHasher.Hash("pw"),
            RoleId = role.Id,
            IsActive = true,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        user.FullName = "كاشير جديد";
        await ctx.SaveChangesAsync();

        var audit = await ctx.AuditLogs
            .Where(a => a.EntityName == nameof(User) && a.Action == AuditAction.Update)
            .OrderByDescending(a => a.Id)
            .FirstAsync();

        Assert.DoesNotContain(nameof(User.UpdatedAt), audit.OldValues!, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(User.UpdatedBy), audit.OldValues!, StringComparison.Ordinal);
        Assert.Contains(nameof(User.FullName), audit.OldValues!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_Audit_Row_Anywhere_Contains_An_Actual_Hash()
    {
        var ctx = NewContext();
        var role = await RoleAsync(ctx, "Cashier");

        var user = new User
        {
            Username = "cashier5",
            FullName = "كاشير",
            PasswordHash = PasswordHasher.Hash("pw5"),
            RoleId = role.Id,
            IsActive = true,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        user.PasswordHash = PasswordHasher.Hash("pw5-rotated");
        await ctx.SaveChangesAsync();

        foreach (var audit in await ctx.AuditLogs.ToListAsync())
        {
            if (audit.NewValues is null) continue;
            Assert.DoesNotContain("pbkdf2-sha256", audit.NewValues, StringComparison.Ordinal);
        }
    }
}