using System;
using System.IO;
using System.Threading.Tasks;
using Daftar.Domain.Distributors;
using Daftar.Domain.Finance;
using Daftar.Domain.Inventory;
using Daftar.Infrastructure;
using Daftar.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The sessions screen's headline banner counts what was loaded, and both counts have to agree with
/// their numbers.
///
/// <para>
/// It read "تم التحميل — 3 جولة، 1 موزع": three rounds written as one round. The figure was right
/// and the word was not, on a line a manager reads every time the screen opens.
/// </para>
///
/// <para>
/// The helper's own ranges are proved beside it in <see cref="ArabicTextTests"/>, but a helper that
/// is right does not prove the line that calls it. The line is a plain interpolation with no
/// binding and no format string to inspect, so the only way to catch it is to load a screen that
/// has rounds in it and read what the banner says. Three rounds is the discriminating count: zero
/// and one take the same form, so a line stuck on the singular would still have read correctly at
/// either.
/// </para>
/// </summary>
public sealed class SessionsScreenBannerTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"daftar-banner-{Guid.NewGuid():N}.db");

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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(_dbPath))
            File.Delete(_dbPath);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_Banner_Counts_Its_Rounds_And_Its_Distributors_In_Their_Own_Forms()
    {
        await ArrangeAsync(rounds: 3, distributors: 1);

        var vm = new SessionsViewModel(_fixture.Services);
        await vm.InitializeAsync();

        Assert.Equal("تم التحميل — 3 جولات، 1 موزع", vm.StatusMessage);
    }

    /// <summary>
    /// One distributor with a vehicle and a till, carrying the rounds he was given.
    ///
    /// He may hold one open round at a time -- the database enforces that with a partial unique
    /// index -- so all but the newest are closed, and a closed round must carry the three facts its
    /// own validation asks for, or the save is refused before the banner is ever reached.
    /// </summary>
    private async Task ArrangeAsync(int rounds, int distributors)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        for (var n = 1; n <= distributors; n++)
        {
            var van = new StockLocation { Name = $"سيارة {n}", Type = StockLocationType.Vehicle };
            var custody = new CashAccount
            {
                Name = $"خزنة {n}",
                Type = CashAccountType.DistributorCustody,
            };

            db.StockLocations.Add(van);
            db.CashAccounts.Add(custody);
            await db.SaveChangesAsync();

            var distributor = new Distributor
            {
                Name = $"موزع {n}",
                StockLocationId = van.Id,
                CashAccountId = custody.Id,
                CommissionRate = 10m,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = 1,
            };

            db.Distributors.Add(distributor);
            await db.SaveChangesAsync();

            for (var round = 0; round < rounds; round++)
            {
                var newest = round == 0;

                db.DistributorSessions.Add(new DistributorSession
                {
                    DistributorId = distributor.Id,
                    Status = newest ? DistributorSessionStatus.Open : DistributorSessionStatus.Closed,
                    OpenedAt = DateTime.UtcNow.AddDays(-round),
                    OpenedBy = 1,
                    ClosedAt = newest ? null : DateTime.UtcNow.AddDays(-round).AddHours(8),
                    ClosedBy = newest ? null : 1,
                    CashDifference = newest ? null : 0m,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = 1,
                });
            }

            await db.SaveChangesAsync();
        }
    }
}
