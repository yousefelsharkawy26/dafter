using System;
using Daftar.Domain.Catalog;
using Daftar.Domain.Inventory;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Shared;
using Xunit;

namespace Daftar.Domain.Tests;

public class PurchaseTests
{
    private static Product Product(string sku = "OLIVE-1")
    {
        var product = new Product(sku, $"Product {sku}");
        product.Units.Add(new ProductUnit { ProductId = 1, Name = "piece", Factor = 1m, IsBase = true });
        product.Units.Add(new ProductUnit { ProductId = 1, Name = "case", Factor = 24m });
        return product;
    }

    [Fact]
    public void AddItem_SnapshotsUnitAndConvertsToBaseQuantity()
    {
        var product = Product();
        var purchase = new Purchase { DocDate = new DateOnly(2026, 10, 5), LocationId = 1 };

        var item = purchase.AddItem(product, product.Units[1], quantity: 2m, unitPrice: 100m);

        Assert.Equal("case", item.UnitName);
        Assert.Equal(24m, item.UnitFactor);
        Assert.Equal(48m, item.BaseQuantity); // 2 cases x 24 pieces
        Assert.Equal(200m, item.LineTotal);
    }

    [Fact]
    public void LotUnitCost_IsLineTotalDividedByBaseQuantity()
    {
        var product = Product();
        var purchase = new Purchase { DocDate = new DateOnly(2026, 10, 5), LocationId = 1 };

        var item = purchase.AddItem(
            product, product.Units[1], quantity: 3m, unitPrice: 100m, discountAmount: 50m);

        // (3 x 100 - 50) / 72 base pieces, rounded to 4 dp
        Assert.Equal(250m, item.LineTotal);
        Assert.Equal(3.4722m, item.ComputeLotUnitCost());
    }

    [Fact]
    public void Totals_SubtotalMinusHeaderDiscount()
    {
        var product = Product();
        var purchase = new Purchase { DocDate = new DateOnly(2026, 10, 5), LocationId = 1, DiscountAmount = 30m };

        purchase.AddItem(product, product.Units[0], quantity: 2m, unitPrice: 100m);   // 200
        purchase.AddItem(product, product.Units[0], quantity: 1m, unitPrice: 50.55m); // 50.55

        Assert.Equal(250.55m, purchase.Subtotal);
        Assert.Equal(220.55m, purchase.Total);
    }

    // ---- purchase returns move stock OUT ----

/// <summary>
/// A supplier return used to be classified as an inbound movement, so posting one *increased*
/// warehouse stock and created a second batch for goods that were still sitting in the original
/// one. The same goods then existed twice and the payable ledger disagreed with the stock ledger.
/// </summary>
[Theory]
[InlineData(InventoryMovementKind.PurchaseReturn, false)]
[InlineData(InventoryMovementKind.Receipt, true)]
[InlineData(InventoryMovementKind.Opening, true)]
[InlineData(InventoryMovementKind.SaleReturn, true)]
[InlineData(InventoryMovementKind.Sale, false)]
[InlineData(InventoryMovementKind.TransferIn, true)]
[InlineData(InventoryMovementKind.TransferOut, false)]
public void Only_A_Receipt_Adds_Stock(InventoryMovementKind kind, bool inbound)
{
    Assert.Equal(inbound, InventoryMovement.IsInboundKind(kind));
}

[Fact]
public void A_Purchase_Return_Movement_Must_Be_Negative()
{
    var quantity = InventoryMovement.SignedQuantity(InventoryMovementKind.PurchaseReturn, 10m);

    Assert.Equal(-10m, quantity);
}

[Fact]
public void A_Purchase_Return_Cannot_Carry_A_Positive_Quantity()
{
    // The sign check that rejects the wrong direction, so the bug cannot reappear.
    var movement = new InventoryMovement
    {
        Kind = InventoryMovementKind.PurchaseReturn,
        Quantity = 10m,
    };

    var ex = Assert.Throws<DomainException>(() => movement.Validate());
    Assert.Contains("inconsistent with the sign", ex.Message, StringComparison.Ordinal);
}

[Fact]
public void A_Purchase_Return_Of_A_Damaged_Batch_Still_Validates()
{
    var movement = new InventoryMovement
    {
        Kind = InventoryMovementKind.PurchaseReturn,
        Quantity = -10m,
    };

    // Negative is the only accepted sign, and it must not trip any other rule.
    movement.Validate();
}

[Fact]
public void Post_RequiresANumberAndLines()
{
        var purchase = new Purchase { DocDate = new DateOnly(2026, 10, 5), SupplierId = 1 };

        Assert.Throws<Shared.DomainException>(() => purchase.Post("PO-000001", 1, DateTime.UtcNow));

        purchase.AddItem(Product(), Product().Units[0], 1m, 10m);
        Assert.Throws<Shared.DomainException>(() => purchase.Post("  ", 1, DateTime.UtcNow));

        purchase.Post("PO-000001", 1, DateTime.UtcNow);
        Assert.Equal(Shared.DocumentStatus.Posted, purchase.Status);
        Assert.NotNull(purchase.PostedAt);
    }

    [Fact]
    public void PostedPurchase_CannotBeCancelled()
    {
        var purchase = new Purchase { DocDate = new DateOnly(2026, 10, 5), SupplierId = 1 };
        purchase.AddItem(Product(), Product().Units[0], 1m, 10m);
        purchase.Post("PO-000001", 1, DateTime.UtcNow);

        Assert.Throws<Shared.DomainException>(() => purchase.Cancel("changed my mind", 1, DateTime.UtcNow));
    }

    [Fact]
    public void Post_RefusesLinesAfterPosting()
    {
        var purchase = new Purchase { DocDate = new DateOnly(2026, 10, 5), SupplierId = 1 };
        purchase.AddItem(Product(), Product().Units[0], 1m, 10m);
        purchase.Post("PO-000001", 1, DateTime.UtcNow);

        Assert.Throws<Shared.DomainException>(() =>
            purchase.AddItem(Product("OLIVE-2"), Product("OLIVE-2").Units[0], 1m, 10m));
    }

    [Fact]
    public void Validate_RejectsAStaleLineTotal()
    {
        var product = Product();
        var purchase = new Purchase { DocDate = new DateOnly(2026, 10, 5), LocationId = 1 };
        var item = purchase.AddItem(product, product.Units[0], 2m, 100m);

        // Simulate a caller changing Quantity without letting the line recalculate.
        typeof(PurchaseItem).GetProperty(nameof(PurchaseItem.Quantity))!
            .SetValue(item, 3m);

        Assert.Throws<Shared.DomainException>(item.Validate);
    }
}