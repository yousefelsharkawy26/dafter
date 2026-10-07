using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Domain.Catalog;
using Daftar.Domain.Finance;
using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.ViewModels;

/// <summary>
/// Receiving a supplier invoice. Posting creates one lot per line, writes the stock movements,
/// moves the balances and records the payable — all in a single transaction, so a rejected line
/// leaves nothing half-received.
/// </summary>
public partial class ReceiveViewModel(IServiceProvider services) : ObservableObject
{
    private bool _watchingLines;

    /// <summary>How much of this receipt is being paid now, and how.</summary>
    public SettlementPanel Settlement { get; } = new(services);

    private readonly IServiceProvider _services = services;

    private int _locationId;

    public ObservableCollection<PartyRow> Suppliers { get; } = [];

    public ObservableCollection<ProductRow> Products { get; } = [];

    public ObservableCollection<UnitRow> Units { get; } = [];

    public ObservableCollection<ReceiveLine> Lines { get; } = [];

    [ObservableProperty] private PartyRow? _selectedSupplier;
    [ObservableProperty] private ProductRow? _selectedProduct;
    [ObservableProperty] private UnitRow? _selectedUnit;
    [ObservableProperty] private string _quantity = "1";
    [ObservableProperty] private string _unitPrice = string.Empty;
    [ObservableProperty] private string _invoiceNo = string.Empty;
    [ObservableProperty] private string _status = "اختار المورد وابدأ الاستلام";
    [ObservableProperty] private bool _busy;

    [ObservableProperty] private decimal _total;
    [ObservableProperty] private bool _canPost;

    /// <summary>Why saving is unavailable, or empty when it is available.</summary>
    [ObservableProperty] private string _postHint = string.Empty;

    public async Task LoadAsync()
    {
        // Total and CanPost are derived from the lines. Recomputing from the collection
        // itself means no place that touches a line can be forgotten — every mutation,
        // including ones added later, keeps the save button in step.
        if (!_watchingLines)
        {
            _watchingLines = true;
            Lines.CollectionChanged += (_, _) => Recalculate();
        }


        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        _locationId = await ctx.StockLocations
            .Where(l => l.IsActive)
            .OrderBy(l => l.Type == StockLocationType.Warehouse ? 0 : 1)
            .Select(l => l.Id)
            .FirstOrDefaultAsync();

        Suppliers.Clear();
        foreach (var party in await ctx.Parties
                     .Where(p => p.IsSupplier && p.Status != PartyStatus.Archived)
                     .OrderBy(p => p.Name)
                     .ToListAsync())
        {
            Suppliers.Add(new PartyRow(party.Id, party.Name));
        }

        Status = _locationId == 0
            ? "مفيش مخزن — أضف مخزن من الإعدادات"
            : Suppliers.Count == 0
                ? "مفيش موردين — أضفهم من شاشة الأطراف"
                : $"{Suppliers.Count} مورد جاهز للاستلام";

        await ReloadProductsAsync(ctx);
        await FillInvoiceNoAsync(ctx);
        await Settlement.LoadAsync();
    }


    /// <summary>
    /// Gives the receipt a reference before it is typed, so the field is never blank and two
    /// deliveries cannot end up with the same one. The manager can still overwrite it with
    /// the supplier's own number.
    /// </summary>
    private async Task FillInvoiceNoAsync(DaftarContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(InvoiceNo))
            return;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var count = await ctx.Purchases.CountAsync(p => p.DocDate == today);
        InvoiceNo = $"INV-{today:yyyyMMdd}-{count + 1:00}";
    }

    private async Task ReloadProductsAsync(DaftarContext ctx)
    {
        var keep = SelectedProduct?.Id;

        Products.Clear();
        foreach (var product in await ctx.Products
                     .Where(p => p.IsActive)
                     .OrderBy(p => p.Name)
                     .ToListAsync())
        {
            Products.Add(new ProductRow(product.Id, product.Sku, product.Name));
        }

        var next = Products.FirstOrDefault(p => p.Id == keep) ?? Products.FirstOrDefault();
        if (next is not null)
            SelectedProduct = next;
        else
            await ReloadUnitsAsync(ctx);
    }

    private async Task ReloadUnitsAsync(DaftarContext ctx)
    {
        Units.Clear();

        if (SelectedProduct is null)
            return;

        foreach (var unit in await ctx.ProductUnits
                     .Where(u => u.ProductId == SelectedProduct.Id && u.IsActive)
                     .OrderByDescending(u => u.IsDefaultPurchase)
                     .ThenByDescending(u => u.IsBase)
                     .ToListAsync())
        {
            Units.Add(new UnitRow(
                unit.Id, unit.Name, unit.Factor, unit.IsBase, unit.IsDefaultPurchase));
        }

        SelectedUnit = Units.FirstOrDefault(u => u.IsDefaultPurchase) ?? Units.FirstOrDefault();
    }

    partial void OnSelectedProductChanged(ProductRow? value) => _ = ReloadUnitsSafeAsync();

    private async Task ReloadUnitsSafeAsync()
    {
        using var scope = _services.CreateScope();
        await ReloadUnitsAsync(scope.ServiceProvider.GetRequiredService<DaftarContext>());
    }

    [RelayCommand]
    private async Task AddLineAsync()
    {
        if (SelectedSupplier is null)
        {
            Status = "اختار المورد الأول";
            return;
        }

        if (SelectedProduct is null || SelectedUnit is null)
        {
            Status = "اختار الصنف والوحدة";
            return;
        }

        if (!decimal.TryParse(Quantity, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) || qty <= 0m)
        {
            Status = "الكمية لازم تكون رقم أكبر من صفر";
            return;
        }

        if (!decimal.TryParse(UnitPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) || price < 0m)
        {
            Status = "السعر لازم يكون رقم صحيح";
            return;
        }

        // The same product in the same unit entered twice is one line, so quantities merge.
        var existing = Lines.FirstOrDefault(l =>
            l.ProductId == SelectedProduct.Id
            && l.ProductUnitId == SelectedUnit.Id);

        if (existing is not null)
        {
            existing.Quantity += qty;
        }
        else
        {
            Lines.Add(new ReceiveLine
            {
                ProductId = SelectedProduct.Id,
                ProductUnitId = SelectedUnit.Id,
                Sku = SelectedProduct.Sku,
                ProductName = SelectedProduct.Name,
                UnitName = SelectedUnit.Name,
                UnitFactor = SelectedUnit.Factor,
                Quantity = qty,
                UnitPrice = price,
            });
        }

        Recalculate();
        Status = "تمت إضافة السطر";

        Quantity = "1";
        UnitPrice = string.Empty;
    }

    [RelayCommand]
    private void RemoveLine(ReceiveLine? line)
    {
        if (line is null)
            return;

        Lines.Remove(line);
        Recalculate();
    }

    [RelayCommand]
    private void Clear()
    {
        Lines.Clear();
        Recalculate();
        Status = "تم تفريغ الاستلام";
    }

    /// <summary>Two decimals, matching the till and the dashboard.</summary>
    public string TotalText => DashboardViewModel.Money(Total);

    private void Recalculate()
    {
        Total = Rounding.Amount(Lines.Sum(l => l.LineTotal));
        OnPropertyChanged(nameof(TotalText));
        CanPost = SelectedSupplier is not null && Lines.Count > 0 && Total > 0m && !Busy;

        // A disabled save button with lines on screen and no reason given looks broken. Say what
        // is actually missing.
        PostHint = Lines.Count == 0 || CanPost
            ? string.Empty
            : SelectedSupplier is null
                ? "اختار المورد عشان تحفظ الاستلام"
                : Total <= 0m
                    ? "الإجمالي لازم يكون أكبر من صفر"
                    : string.Empty;
    }

    partial void OnSelectedSupplierChanged(PartyRow? value) => Recalculate();
    partial void OnTotalChanged(decimal value) => Recalculate();
    partial void OnBusyChanged(bool value) => Recalculate();

    [RelayCommand]
    private async Task PostAsync()
    {
        if (!CanPost || SelectedSupplier is null)
            return;

        Busy = true;
        Recalculate();
        Status = "جاري الحفظ…";

        var postedLines = Lines.Count;

        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var today = DateOnly.FromDateTime(DateTime.Now);

            var purchase = new Purchase
            {
                Type = PurchaseType.Invoice,
                SupplierId = SelectedSupplier.Id,
                LocationId = _locationId,
                DocDate = today,
                SupplierInvoiceNo = string.IsNullOrWhiteSpace(InvoiceNo) ? null : InvoiceNo.Trim(),
                Notes = "استلام من شاشة الاستلام",
            };

            ctx.Purchases.Add(purchase);
            await ctx.SaveChangesAsync();

            foreach (var line in Lines)
            {
                var product = await ctx.Products.FirstAsync(p => p.Id == line.ProductId);
                var unit = await ctx.ProductUnits.FirstAsync(u => u.Id == line.ProductUnitId);

                purchase.AddItem(
                    product, unit,
                    quantity: line.Quantity,
                    unitPrice: line.UnitPrice);
            }

            await ctx.SaveChangesAsync();

            // Paid or not, receiving makes us owe the supplier. The voucher is what reduces it.
            var result = await new PurchasePostingService(ctx).PostAsync(purchase.Id, userId: 1, today);

            await Settlement.ApplyAsync(
                ctx,
                PaymentDirection.Out,
                purchase.SupplierId,
                saleId: null,
                purchaseId: purchase.Id,
                total: purchase.Total,
                today,
                userId: 1);

            var paid = Settlement.Settlement == SettlementChoice.Deferred
                ? "آجل"
                : $"مدفوع {DashboardViewModel.Money(Settlement.AmountFor(purchase.Total))}";

            Status = $"تم الاستلام — {result.DocNo} · {postedLines} سطر · {paid} · الإجمالي {DashboardViewModel.Money(Total)}";

            Lines.Clear();

            // Cleared so FillInvoiceNoAsync hands out the next reference for today.
            InvoiceNo = string.Empty;
            Settlement.Reset();
            Recalculate();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Status = $"فشل الحفظ: {ex.Message}";
        }
        finally
        {
            Busy = false;
            Recalculate();
        }
    }
}

/// <summary>One line of a supplier invoice being built.</summary>
public partial class ReceiveLine : ObservableObject
{
    public int ProductId { get; init; }
    public int ProductUnitId { get; init; }
    public string Sku { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string UnitName { get; init; } = string.Empty;
    public decimal UnitFactor { get; init; } = 1m;
    [ObservableProperty] private decimal _quantity;
    [ObservableProperty] private decimal _unitPrice;
    [ObservableProperty] private decimal _lineTotal;

    /// <summary>Base units this line actually moves — what the balance will change by.</summary>
    [ObservableProperty] private decimal _baseQuantity;

    partial void OnBaseQuantityChanged(decimal value)
        => OnPropertyChanged(nameof(BaseQuantityText));

    partial void OnQuantityChanged(decimal value)
    {
        OnPropertyChanged(nameof(QuantityText));
        Recalculate();
    }

    partial void OnUnitPriceChanged(decimal value)
    {
        OnPropertyChanged(nameof(UnitPriceText));
        Recalculate();
    }

    partial void OnLineTotalChanged(decimal value) => OnPropertyChanged(nameof(LineTotalText));

    public string UnitPriceText => DashboardViewModel.Money(UnitPrice);

    public string LineTotalText => DashboardViewModel.Money(LineTotal);

    public string QuantityText => Domain.Shared.Rounding.QuantityText(Quantity);

    public string BaseQuantityText => Domain.Shared.Rounding.QuantityText(BaseQuantity);

    private void Recalculate()
    {
        BaseQuantity = Rounding.Quantity(Quantity * UnitFactor);
        LineTotal = Rounding.LineTotal(Quantity, UnitPrice, 0m);
    }
}