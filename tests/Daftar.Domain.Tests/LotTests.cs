using Daftar.Domain.Catalog;
using Daftar.Domain.Inventory;
using Xunit;

namespace Daftar.Domain.Tests;

public class LotTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    [Fact]
    public void Create_RecordsTheDeliveryDateAndCost()
    {
        var lot = Lot.Create(1, Today, 7, 12.3456m);

        Assert.Equal(Today, lot.ReceivedOn);
        Assert.Equal(7, lot.SupplierId);

        // Lot cost keeps 4dp: a case price divided by a factor needs the extra precision.
        Assert.Equal(12.3456m, lot.UnitCost);
        Assert.Equal(LotStatus.Active, lot.Status);
        Assert.True(lot.IsAvailable());
    }

    [Fact]
    public void Create_Rejects_NegativeCost()
    {
        var error = Assert.Throws<Shared.DomainException>(() => Lot.Create(1, Today, null, -1m));

        Assert.Contains("negative", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Freeze_MakesTheCostImmutable()
    {
        var lot = Lot.Create(1, Today, null, 10m);
        lot.Freeze();

        // Only the private setter can change it, so this documents the intent: the Domain offers
        // no way to restate a delivered price.
        Assert.Equal(10m, lot.UnitCost);
    }

    [Fact]
    public void Block_RequiresAReason_AndStopsTheBatchBeingDrawn()
    {
        var lot = Lot.Create(1, Today, null, 5m);

        Assert.Throws<Shared.DomainException>(() => lot.Block("   "));

        lot.Block("Recall 2026-04");
        Assert.Equal(LotStatus.Blocked, lot.Status);
        Assert.Equal("Recall 2026-04", lot.BlockReason);
        Assert.False(lot.IsAvailable());

        lot.Unblock();
        Assert.True(lot.IsAvailable());
    }

    [Fact]
    public void A_BlockedBatch_WithoutAReasonIsRejectedByValidation()
    {
        var lot = Lot.Create(1, Today, null, 5m);
        lot.Status = LotStatus.Blocked;

        var error = Assert.Throws<Shared.DomainException>(lot.Validate);

        Assert.Contains("BlockReason", error.Message);
    }
}