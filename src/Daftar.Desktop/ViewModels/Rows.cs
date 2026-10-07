using System;

namespace Daftar.ViewModels;

/// <summary>
/// Rows shared by several screens, so a product picked in الاستلام and the same product picked in
/// البيع are the same shape. Optional fields let a screen that only needs part of it stay terse.
/// </summary>
public sealed record PartyRow(int Id, string Name);

public sealed record ProductRow(int Id, string Sku, string Name);

public sealed record UnitRow(
    int Id,
    string Name,
    decimal Factor,
    bool IsBase,
    bool IsDefaultSale = false,
    bool IsDefaultPurchase = false,
    string? Barcode = null,
    int ProductId = 0);

public sealed record PriceRow(string UnitName, decimal Price, DateOnly EffectiveFrom);

/// <summary>Where money physically sits, as offered on a payment.</summary>
public sealed record AccountRow(int Id, string Name, Domain.Finance.CashAccountType Type);

