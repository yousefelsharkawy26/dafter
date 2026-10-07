using Daftar.Domain.Inventory;
using Daftar.Domain.Shared;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// FIFO allocation over real balances. Order is ReceivedOn then LotId, so two deliveries made on
/// the same day always draw in the same sequence.
/// </summary>
public class FifoTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static StockBalance MakeBalance(
        int lotId,
        decimal quantity,
        int daysAgo,
        LotStatus status = LotStatus.Active)
    {
        var lot = Lot.Create(1, Today.AddDays(-daysAgo), null, 10m);
        lot.Status = status;

        // Quantity is read-only; Apply is the one place it moves.
        var balance = new StockBalance { LotId = lotId, LocationId = 1, Lot = lot };
        balance.Apply(quantity, DateTime.UtcNow);
        return balance;
    }

    [Fact]
    public void Order_DrawsTheOldestDeliveryFirst()
    {
        var ordered = Fifo.Order([
            MakeBalance(3, 5m, 0),
            MakeBalance(1, 5m, 10),
            MakeBalance(2, 5m, 5),
        ]).Select(b => b.LotId);

        Assert.Equal([1, 2, 3], ordered);
    }

    [Fact]
    public void Order_TieBreaksOnLotId_SoTheResultIsStable()
    {
        var first = Fifo.Order([MakeBalance(7, 1m, 3), MakeBalance(4, 1m, 3), MakeBalance(9, 1m, 3)])
            .Select(b => b.LotId);
        var second = Fifo.Order([MakeBalance(9, 1m, 3), MakeBalance(4, 1m, 3), MakeBalance(7, 1m, 3)])
            .Select(b => b.LotId);

        Assert.Equal([4, 7, 9], first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Order_SkipsBlockedBatchesAndEmptyRows()
    {
        var ordered = Fifo.Order([
            MakeBalance(1, 5m, 1, LotStatus.Blocked),
            MakeBalance(2, 0m, 1),
            MakeBalance(3, 5m, 2),
        ]).Select(b => b.LotId);

        Assert.Equal([3], ordered);
    }

    [Fact]
    public void Classify_SeparatesNoStockFromOnlyBlocked()
    {
        Assert.Equal(StockAvailability.NoStock, Fifo.Classify([]));
        Assert.Equal(
            StockAvailability.OnlyBlocked,
            Fifo.Classify([MakeBalance(1, 5m, 1, LotStatus.Blocked)]));
        Assert.Equal(StockAvailability.Available, Fifo.Classify([MakeBalance(1, 5m, 1)]));
    }

    [Fact]
    public void Pick_SplitsAcrossBatchesWhenOneIsNotEnough()
    {
        var balances = new[] { MakeBalance(1, 4m, 5), MakeBalance(2, 10m, 1) };

        var picked = Fifo.Pick(balances, 6m);

        Assert.Equal([1, 2], picked.Select(b => b.LotId));
    }

    [Fact]
    public void Pick_ReportsWhatIsShort()
    {
        var error = Assert.Throws<InsufficientStockException>(() =>
            Fifo.Pick([MakeBalance(1, 3m, 5)], 5m));

        Assert.Contains("short by 2", error.Message);
    }

    [Fact]
    public void Pick_ExplainsThatOnlyBlockedStockExists()
    {
        var error = Assert.Throws<InsufficientStockException>(() =>
            Fifo.Pick([MakeBalance(1, 3m, 5, LotStatus.Blocked)], 1m));

        Assert.Contains("blocked", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pick_RejectsANonPositiveRequest()
        => Assert.Throws<DomainException>(() => Fifo.Pick([MakeBalance(1, 3m, 5)], 0m));

    [Fact]
    public void Allocate_TakesFromTheOldestFirstAndStops()
    {
        var allocation = Fifo.Allocate(
            [MakeBalance(1, 5m, 5), MakeBalance(2, 10m, 1), MakeBalance(3, 10m, 0)],
            7m);

        Assert.Equal(2, allocation.Count);
        Assert.Equal(1, allocation[0].Balance.LotId);
        Assert.Equal(5m, allocation[0].Quantity);
        Assert.Equal(2, allocation[1].Balance.LotId);
        Assert.Equal(2m, allocation[1].Quantity);
    }
}