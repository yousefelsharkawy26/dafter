using System;

namespace Daftar.ViewModels;

/// <summary>
/// Rows shared by several screens, so a product picked in الاستلام and the same product picked in
/// البيع are the same shape. Optional fields let a screen that only needs part of it stay terse.
/// </summary>
public sealed record PartyRow(int Id, string Name);

public sealed record ProductRow(
    int Id,
    string Sku,
    string Name,
    bool IsActive = true,
    decimal? ReorderLevel = null,
    decimal TotalOnHand = 0m,
    string BaseUnitName = "",
    int UnitsCount = 1)
{
    public bool IsLowStock => ReorderLevel.HasValue && TotalOnHand <= ReorderLevel.Value;
    public string OnHandText => $"{TotalOnHand:G29} {BaseUnitName}".Trim();
    public string StatusText => IsActive ? "نشط" : "معطل";
}

public sealed record UnitRow(
    int Id,
    string Name,
    decimal Factor,
    bool IsBase,
    bool IsDefaultSale = false,
    bool IsDefaultPurchase = false,
    string? Barcode = null,
    int ProductId = 0,
    decimal PurchasePrice = 0m,
    decimal SalePrice = 0m)
{
    public decimal Profit => SalePrice > 0m ? SalePrice - PurchasePrice : 0m;
    public decimal ProfitMarginPercent => (PurchasePrice > 0m && SalePrice > 0m)
        ? Math.Round(((SalePrice - PurchasePrice) / PurchasePrice) * 100m, 1)
        : 0m;
    public string ProfitMarginText => (PurchasePrice > 0m && SalePrice > 0m)
        ? $"{ProfitMarginPercent:+0.0;-0.0;0}%"
        : "-";
}

public sealed record PriceRow(string UnitName, decimal Price, DateOnly EffectiveFrom);

/// <summary>Where money physically sits, as offered on a payment.</summary>
public sealed record AccountRow(int Id, string Name, Domain.Finance.CashAccountType Type);

