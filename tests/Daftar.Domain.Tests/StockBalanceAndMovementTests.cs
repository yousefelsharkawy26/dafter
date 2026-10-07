using Daftar.Domain.Inventory;
using Xunit;

namespace Daftar.Domain.Tests;

public class StockBalanceAndMovementTests
{
    [Fact]
    public void Apply_RejectsGoingNegative()
    {
        var balance = new StockBalance { LotId = 1, LocationId = 1 };
        balance.Apply(10m, DateTime.UtcNow);

        var error = Assert.Throws<InsufficientStockException>(() => balance.Apply(-11m, DateTime.UtcNow));

        Assert.Contains("cannot be taken out", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10m, balance.Quantity);
    }

    [Fact]
    public void Apply_AllowsReturningToZero()
    {
        var balance = new StockBalance { LotId = 1, LocationId = 1 };
        balance.Apply(10m, DateTime.UtcNow);
        balance.Apply(-10m, DateTime.UtcNow);

        Assert.Equal(0m, balance.Quantity);
    }

    [Theory]
    [InlineData(InventoryMovementKind.Receipt, 5)]
    [InlineData(InventoryMovementKind.Sale, -5)]
    public void SignedQuantity_UsesTheKindToSetTheSign(InventoryMovementKind kind, double expected)
        => Assert.Equal((decimal)expected, InventoryMovement.SignedQuantity(kind, 5m));

    [Fact]
    public void Validate_RejectsSignThatContradictsTheKind()
    {
        var movement = new InventoryMovement
        {
            Kind = InventoryMovementKind.Sale,
            Quantity = 5m, // a sale must be negative
        };

        Assert.Throws<Shared.DomainException>(movement.Validate);
    }

    [Fact]
    public void Validate_RequiresReasonCode_ForAdjustAndScrap()
    {
        var adjust = new InventoryMovement { Kind = InventoryMovementKind.Adjust, Quantity = -1m };
        var scrap = new InventoryMovement { Kind = InventoryMovementKind.Scrap, Quantity = -1m };

        Assert.Throws<Shared.DomainException>(adjust.Validate);
        Assert.Throws<Shared.DomainException>(scrap.Validate);

        adjust.ReasonCode = ReasonCode.Damaged;
        scrap.ReasonCode = ReasonCode.Expired;

        adjust.Validate();
        scrap.Validate();
    }

    [Fact]
    public void Validate_RejectsZeroQuantity()
    {
        var movement = new InventoryMovement { Kind = InventoryMovementKind.Receipt, Quantity = 0m };

        Assert.Throws<Shared.DomainException>(movement.Validate);
    }

    [Fact]
    public void Balance_AlwaysEqualsTheSumOfItsMovements()
    {
        var balance = new StockBalance { LotId = 1, LocationId = 1 };
        var movements = new[] { 30m, -5m, -8m, 2m };

        foreach (var m in movements)
            balance.Apply(m, DateTime.UtcNow);

        Assert.Equal(movements.Sum(), balance.Quantity);
    }
}