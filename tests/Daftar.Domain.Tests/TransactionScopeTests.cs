using Daftar.Domain.Inventory;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The two promises a <see cref="TransactionScope"/> makes about undoing work.
///
/// <para>
/// A caller keeps working after it commits — every posting path refreshes what it just wrote — and it
/// does that inside the <c>try</c> whose <c>catch</c> rolls back. So the rollback has to survive being
/// asked to undo a transaction that has already committed, and the commit has to survive it too:
/// answering with EF's "This SqliteTransaction has completed" from inside the catch replaced the
/// reason the operator was about to be shown with a complaint about the transaction object.
/// </para>
/// </summary>
public sealed class TransactionScopeTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"daftar-txscope-{Guid.NewGuid():N}.db");

    private ServiceProviderFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = await ServiceProviderFixture.CreateAsync(_dbPath);

        using var scope = _fixture.Services.CreateScope();
        await DaftarDatabase.InitializeAsync(
            scope.ServiceProvider.GetRequiredService<DaftarContext>(),
            Infrastructure.Security.PasswordHasher.Hash("admin"));
    }

    public Task DisposeAsync()
    {
        _fixture.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (File.Exists(_dbPath))
            File.Delete(_dbPath);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Rolling_Back_After_A_Commit_Is_Declined_And_The_Commit_Stands()
    {
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var tx = await TransactionScope.BeginAsync(db, CancellationToken.None);

            db.StockLocations.Add(new StockLocation { Name = "After the commit" });
            await db.SaveChangesAsync();
            await tx.CommitAsync();

            // What a caller whose post-commit refresh failed does on its way out of the catch. It
            // must not throw, and it must not take the committed row with it.
            await tx.RollbackAsync();
        }

        using var check = _fixture.Services.CreateScope();
        var context = check.ServiceProvider.GetRequiredService<DaftarContext>();

        Assert.True(await context.StockLocations.AnyAsync(l => l.Name == "After the commit"));
    }

    [Fact]
    public async Task Rolling_Back_Before_The_Commit_Still_Undoes_The_Work()
    {
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var tx = await TransactionScope.BeginAsync(db, CancellationToken.None);

            db.StockLocations.Add(new StockLocation { Name = "Never committed" });
            await db.SaveChangesAsync();
            await tx.RollbackAsync();

            // Read back on the same connection, before anything is disposed. Closing the scope would
            // roll the transaction back by itself, so a check made after it proved nothing about this
            // call: the work has to be gone while the context that wrote it is still open.
            Assert.False(await db.StockLocations.AsNoTracking()
                .AnyAsync(l => l.Name == "Never committed"));
        }

        using var check = _fixture.Services.CreateScope();
        var context = check.ServiceProvider.GetRequiredService<DaftarContext>();

        Assert.False(await context.StockLocations.AnyAsync(l => l.Name == "Never committed"));
    }
}
