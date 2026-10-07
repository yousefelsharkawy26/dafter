using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Domain.Catalog;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Sales;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.ViewModels;

/// <summary>
/// Products, their units and their prices. This is the foundation: receiving cannot create a lot
/// for a product that does not exist, and selling cannot price one.
/// </summary>
public partial class ProductsViewModel(IServiceProvider services) : ObservableObject
{
    private readonly IServiceProvider _services = services;

    private int _selectedProductId;
    private int _priceListId;

    public ObservableCollection<ProductRow> Products { get; } = [];

    public ObservableCollection<UnitRow> Units { get; } = [];

    public ObservableCollection<PriceRow> Prices { get; } = [];

    // --- new product ---
    [ObservableProperty] private string _newSku = string.Empty;
    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string _newBaseUnit = "piece";
    [ObservableProperty] private string _newReorderLevel = string.Empty;
    [ObservableProperty] private string _status = "اكتب كود الصنف واسمه";

    // --- new unit ---
    [ObservableProperty] private string _unitName = string.Empty;
    [ObservableProperty] private string _unitFactor = "1";
    [ObservableProperty] private bool _unitIsBase;
    [ObservableProperty] private bool _unitDefaultSale;
    [ObservableProperty] private bool _unitDefaultPurchase;
    [ObservableProperty] private string _unitBarcode = string.Empty;

    // --- new price ---
    [ObservableProperty] private UnitRow? _priceUnit;
    [ObservableProperty] private string _priceAmount = string.Empty;

    [ObservableProperty] private ProductRow? _selectedProduct;

    public bool HasSelection => _selectedProductId > 0;

    public async Task LoadAsync()
    {
        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        _priceListId = await ctx.PriceLists
            .Where(p => p.IsDefault)
            .Select(p => p.Id)
            .FirstOrDefaultAsync();

        await ReloadProductsAsync(ctx);

        if (Products.Count > 0 && _selectedProductId == 0)
            await SelectProductAsync(Products[0]);
    }

    private async Task ReloadProductsAsync(DaftarContext ctx)
    {
        var keep = _selectedProductId;

        Products.Clear();
        foreach (var product in await ctx.Products
                     .Where(p => p.IsActive)
                     .OrderBy(p => p.Name)
                     .ToListAsync())
        {
            Products.Add(new ProductRow(product.Id, product.Sku, product.Name));
        }

        _selectedProductId = Products.Any(p => p.Id == keep) ? keep : 0;
        SelectedProduct = Products.FirstOrDefault(p => p.Id == _selectedProductId);
    }

    [RelayCommand]
    private async Task SelectProductAsync(ProductRow? row)
    {
        if (row is null)
            return;

        _selectedProductId = row.Id;
        SelectedProduct = row;
        PriceUnit = null;

        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        Units.Clear();
        foreach (var unit in await ctx.ProductUnits
                     .Where(u => u.ProductId == row.Id)
                     .OrderByDescending(u => u.IsBase)
                     .ThenBy(u => u.Factor)
                     .ToListAsync())
        {
            Units.Add(new UnitRow(
                Id: unit.Id,
                Name: unit.Name,
                Factor: unit.Factor,
                IsBase: unit.IsBase,
                IsDefaultSale: unit.IsDefaultSale,
                IsDefaultPurchase: unit.IsDefaultPurchase,
                Barcode: unit.Barcode,
                ProductId: row.Id));
        }

        Prices.Clear();
        var unitIds = Units.Select(u => u.Id).ToList();
        foreach (var price in await ctx.ProductPrices
                     .Where(p => unitIds.Contains(p.ProductUnitId))
                     .OrderByDescending(p => p.EffectiveFrom)
                     .ToListAsync())
        {
            var unit = Units.FirstOrDefault(u => u.Id == price.ProductUnitId);
            Prices.Add(new PriceRow(unit?.Name ?? "?", price.Price, price.EffectiveFrom));
        }

        PriceUnit = Units.FirstOrDefault(u => u.IsDefaultSale) ?? Units.FirstOrDefault();
        Status = $"{Units.Count} وحدة · {Prices.Count} سعر";
    }

    [RelayCommand]
    private async Task AddProductAsync()
    {
        if (string.IsNullOrWhiteSpace(NewSku) || string.IsNullOrWhiteSpace(NewName))
        {
            Status = "الكود والاسم مطلوبين";
            return;
        }

        if (string.IsNullOrWhiteSpace(NewBaseUnit))
        {
            Status = "اكتب وحدة الأساس";
            return;
        }

        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var sku = Product.NormalizeSku(NewSku);

            if (await ctx.Products.AnyAsync(p => p.Sku == sku))
            {
                Status = $"الكود {sku} مستخدم بالفعل";
                return;
            }

            var product = new Product(sku, NewName.Trim())
            {
                ReorderLevel = decimal.TryParse(NewReorderLevel, out var reorder) ? reorder : null,
            };

            // The base unit is a row like any other, with Factor 1.
            product.Units.Add(new ProductUnit
            {
                Name = NewBaseUnit.Trim(),
                Factor = 1m,
                IsBase = true,
                IsDefaultSale = true,
                IsDefaultPurchase = true,
            });

            ctx.Products.Add(product);
            await ctx.SaveChangesAsync();
            product.Units[0].ProductId = product.Id;
            await ctx.SaveChangesAsync();

            Status = $"تمت إضافة {sku}";
            NewSku = string.Empty;
            NewName = string.Empty;
            NewReorderLevel = string.Empty;

            await ReloadProductsAsync(ctx);
            await SelectProductAsync(Products.FirstOrDefault(p => p.Id == product.Id));
        }
        catch (Exception ex)
        {
            Status = $"خطأ: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task AddUnitAsync()
    {
        if (_selectedProductId == 0)
        {
            Status = "اختار صنف الأول";
            return;
        }

        if (string.IsNullOrWhiteSpace(UnitName))
        {
            Status = "اسم الوحدة مطلوب";
            return;
        }

        if (!decimal.TryParse(UnitFactor, out var factor) || factor <= 0m)
        {
            Status = "معامل التحويل لازم يكون أكبر من صفر";
            return;
        }

        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var name = UnitName.Trim();
            if (await ctx.ProductUnits.AnyAsync(u => u.ProductId == _selectedProductId && u.Name == name))
            {
                Status = $"الوحدة {name} موجودة بالفعل";
                return;
            }

            var unit = new ProductUnit
            {
                ProductId = _selectedProductId,
                Name = name,
                Factor = factor,
                IsBase = UnitIsBase,
                IsDefaultSale = UnitDefaultSale,
                IsDefaultPurchase = UnitDefaultPurchase,
                Barcode = string.IsNullOrWhiteSpace(UnitBarcode) ? null : UnitBarcode.Trim(),
            };

            // Exactly one base unit per product, enforced by a unique partial index. The old base
            // must be demoted and SAVED before the new one is inserted — SQLite checks the index
            // per statement, so inserting first would be rejected outright.
            if (unit.IsBase)
            {
                var others = await ctx.ProductUnits
                    .Where(u => u.ProductId == _selectedProductId && u.IsBase)
                    .ToListAsync();
                foreach (var other in others)
                    other.IsBase = false;
                await ctx.SaveChangesAsync();
            }

            ctx.ProductUnits.Add(unit);
            await ctx.SaveChangesAsync();

            Status = $"تمت إضافة الوحدة {name}";
            UnitName = string.Empty;
            UnitFactor = "1";
            UnitIsBase = false;
            UnitDefaultSale = false;
            UnitDefaultPurchase = false;
            UnitBarcode = string.Empty;

            await SelectProductAsync(SelectedProduct);
        }
        catch (Exception ex)
        {
            Status = $"خطأ: {ex.Message}";
        }
    }

    /// <summary>
    /// Prices are never updated: a change is a new row, so past invoices keep the price that was in
    /// force when they were written.
    /// </summary>
    [RelayCommand]
    private async Task AddPriceAsync()
    {
        if (_selectedProductId == 0 || PriceUnit is null)
        {
            Status = "اختار صنف ووحدة";
            return;
        }

        if (!decimal.TryParse(PriceAmount, out var amount) || amount < 0m)
        {
            Status = "السعر لازم يكون رقم صحيح";
            return;
        }

        if (_priceListId == 0)
        {
            Status = "مفيش قائمة أسعار افتراضية";
            return;
        }

        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var today = DateOnly.FromDateTime(DateTime.Now);

            ctx.ProductPrices.Add(new ProductPrice
            {
                PriceListId = _priceListId,
                ProductUnitId = PriceUnit.Id,
                Price = Rounding.Unit(amount),
                EffectiveFrom = today,
            });

            await ctx.SaveChangesAsync();

            Status = $"تم حفظ السعر {PriceUnit.Name} = {Rounding.Unit(amount)}";
            PriceAmount = string.Empty;

            await SelectProductAsync(SelectedProduct);
        }
        catch (Exception ex)
        {
            Status = $"خطأ: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleActiveAsync(ProductRow? row)
    {
        if (row is null)
            return;

        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var product = await ctx.Products.FirstOrDefaultAsync(p => p.Id == row.Id);
        if (product is null)
            return;

        product.IsActive = !product.IsActive;
        await ctx.SaveChangesAsync();
        await ReloadProductsAsync(ctx);
    }

    partial void OnSelectedProductChanged(ProductRow? value) => OnPropertyChanged(nameof(HasSelection));
}
