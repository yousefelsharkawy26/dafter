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
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.ViewModels;

public sealed record SupplierOption(int Id, string Name, decimal Balance)
{
    public string Display => Balance != 0m
        ? $"{Name} (مستحق له: {Balance:N2} ج.م)"
        : Name;

    public override string ToString() => Display;
}

public sealed record ReceiveUnitOption(
    int ProductUnitId,
    string UnitName,
    decimal UnitFactor,
    decimal PurchasePrice,
    decimal OnHand)
{
    public override string ToString() => UnitName;
}

public sealed partial class ReceiveProductOption : ObservableObject
{
    public int ProductId { get; }
    public string Sku { get; }
    public string Name { get; }

    public ObservableCollection<ReceiveUnitOption> AvailableUnits { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProductUnitId), nameof(UnitName), nameof(UnitFactor),
                              nameof(PurchasePrice), nameof(OnHand),
                              nameof(Display), nameof(PurchasePriceText), nameof(OnHandText),
                              nameof(HasStock), nameof(HasMultipleUnits))]
    private ReceiveUnitOption? _selectedUnit;

    public int     ProductUnitId => SelectedUnit?.ProductUnitId ?? 0;
    public string  UnitName      => SelectedUnit?.UnitName      ?? string.Empty;
    public decimal UnitFactor    => SelectedUnit?.UnitFactor    ?? 1m;
    public decimal PurchasePrice => SelectedUnit?.PurchasePrice ?? 0m;
    public decimal OnHand        => SelectedUnit?.OnHand        ?? 0m;

    public string Display           => $"{Name} ({UnitName}) — {Sku}";
    public string PurchasePriceText => PurchasePrice > 0 ? $"{PurchasePrice:N2} ج.م" : "سعر غير محدد";
    public string OnHandText        => $"المخزون: {OnHand:N0}";
    public bool   HasStock          => OnHand > 0m;
    public bool   HasMultipleUnits  => AvailableUnits.Count > 1;

    public ReceiveProductOption(int productId, string sku, string name)
    {
        ProductId = productId;
        Sku = sku;
        Name = name;
    }

    public override string ToString() => Display;
}

public partial class ReceiveLine : ObservableObject
{
    public int ProductId { get; init; }

    [ObservableProperty] private int _productUnitId;
    public string Sku { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;

    [ObservableProperty] private string _unitName = string.Empty;
    [ObservableProperty] private decimal _unitFactor = 1m;

    [ObservableProperty] private bool _updateProductPurchasePrice;
    [ObservableProperty] private decimal _referencePurchasePrice;
    [ObservableProperty] private decimal? _scannedUnitPrice;

    public bool HasPriceDiscrepancy => ReferencePurchasePrice > 0m && UnitPrice > 0m && UnitPrice != ReferencePurchasePrice;
    public string PriceDiscrepancyText => HasPriceDiscrepancy
        ? $"السعر الحالي في النظام: {ReferencePurchasePrice:N2} ج.م"
        : string.Empty;

    private bool _isManualPrice;
    private int? _manualPriceUnitId;
    private bool _suppressManualPriceTracking;

    public ObservableCollection<ReceiveUnitOption> AvailableUnits { get; } = [];

    [ObservableProperty] private ReceiveUnitOption? _selectedUnit;

    partial void OnSelectedUnitChanged(ReceiveUnitOption? value)
    {
        if (value is null || value.ProductUnitId == ProductUnitId) return;
        ProductUnitId = value.ProductUnitId;
        UnitName = value.UnitName;
        UnitFactor = value.UnitFactor;
        ReferencePurchasePrice = value.PurchasePrice;

        if (!_isManualPrice || _manualPriceUnitId != value.ProductUnitId)
        {
            if (value.PurchasePrice > 0m)
            {
                _suppressManualPriceTracking = true;
                try
                {
                    UnitPrice = value.PurchasePrice;
                }
                finally
                {
                    _suppressManualPriceTracking = false;
                }
            }
        }

        Recalculate();
        OnPropertyChanged(nameof(HasPriceDiscrepancy));
        OnPropertyChanged(nameof(PriceDiscrepancyText));
        _onUpdated?.Invoke();
    }

    [ObservableProperty] private decimal _quantity = 1m;
    [ObservableProperty] private string _quantityInput = "1";

    [ObservableProperty] private decimal _unitPrice;
    [ObservableProperty] private string _unitPriceInput = "0";

    [ObservableProperty] private decimal _lineTotal;
    [ObservableProperty] private decimal _baseQuantity;

    private Action? _onUpdated;
    public void WireHandlers(Action onUpdated) => _onUpdated = onUpdated;

    partial void OnQuantityChanged(decimal value)
    {
        var text = Rounding.QuantityText(value);
        if (_quantityInput != text)
        {
            _quantityInput = text;
            OnPropertyChanged(nameof(QuantityInput));
        }
        OnPropertyChanged(nameof(QuantityText));
        Recalculate();
        _onUpdated?.Invoke();
    }

    partial void OnQuantityInputChanged(string value)
    {
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out var parsed) ||
            decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
        {
            if (parsed > 0m && parsed != _quantity)
            {
                _quantity = parsed;
                OnPropertyChanged(nameof(Quantity));
                OnPropertyChanged(nameof(QuantityText));
                Recalculate();
                _onUpdated?.Invoke();
            }
        }
    }

    partial void OnUnitPriceChanged(decimal value)
    {
        if (!_suppressManualPriceTracking)
        {
            _isManualPrice = true;
            _manualPriceUnitId = ProductUnitId;
        }

        var text = value > 0 ? value.ToString("0.##") : "0";
        if (_unitPriceInput != text)
        {
            _unitPriceInput = text;
            OnPropertyChanged(nameof(UnitPriceInput));
        }
        OnPropertyChanged(nameof(UnitPriceText));
        OnPropertyChanged(nameof(HasPriceDiscrepancy));
        OnPropertyChanged(nameof(PriceDiscrepancyText));
        Recalculate();
        _onUpdated?.Invoke();
    }

    partial void OnUnitPriceInputChanged(string value)
    {
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out var parsed) ||
            decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
        {
            if (parsed >= 0m && parsed != _unitPrice)
            {
                _isManualPrice = true;
                _manualPriceUnitId = ProductUnitId;
                _unitPrice = parsed;
                OnPropertyChanged(nameof(UnitPrice));
                OnPropertyChanged(nameof(UnitPriceText));
                OnPropertyChanged(nameof(HasPriceDiscrepancy));
                OnPropertyChanged(nameof(PriceDiscrepancyText));
                Recalculate();
                _onUpdated?.Invoke();
            }
        }
    }

    partial void OnBaseQuantityChanged(decimal value) => OnPropertyChanged(nameof(BaseQuantityText));
    partial void OnLineTotalChanged(decimal value) => OnPropertyChanged(nameof(LineTotalText));

    public string QuantityText => Rounding.QuantityText(Quantity);
    public string BaseQuantityText => $"{Rounding.QuantityText(BaseQuantity)} {UnitName}";
    public string UnitPriceText => DashboardViewModel.Money(UnitPrice);
    public string LineTotalText => DashboardViewModel.Money(LineTotal);

    public void Recalculate()
    {
        BaseQuantity = Rounding.Quantity(Quantity * UnitFactor);
        LineTotal = Rounding.LineTotal(Quantity, UnitPrice, 0m);
    }
}

/// <summary>
/// Receiving a supplier invoice. Posting creates one lot per line, writes the stock movements,
/// moves the balances and records the payable — all in a single transaction.
/// </summary>
public partial class ReceiveViewModel(IServiceProvider services) : ObservableObject
{
    private bool _watchingLines;

    /// <summary>How much of this receipt is being paid now, and how.</summary>
    public SettlementPanel Settlement { get; } = new(services);

    private readonly IServiceProvider _services = services;

    private int _locationId;

    public ObservableCollection<SupplierOption> Suppliers { get; } = [];

    public ObservableCollection<ReceiveProductOption> Products { get; } = [];

    public ObservableCollection<ReceiveProductOption> FilteredProducts { get; } = [];

    public ObservableCollection<ReceiveLine> Lines { get; } = [];

    [ObservableProperty] private SupplierOption? _selectedSupplier;
    [ObservableProperty] private string _productSearchText = string.Empty;
    [ObservableProperty] private string _invoiceNo = string.Empty;
    [ObservableProperty] private string _status = "اختار المورد وابدأ الاستلام";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _isClearConfirmVisible;

    [ObservableProperty] private decimal _total;
    [ObservableProperty] private bool _canPost;

    /// <summary>Why saving is unavailable, or empty when it is available.</summary>
    [ObservableProperty] private string _postHint = string.Empty;

    public async Task LoadAsync()
    {
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
        var parties = await ctx.Parties
            .AsNoTracking()
            .Where(p => p.IsSupplier && p.Status != PartyStatus.Archived)
            .OrderBy(p => p.Name)
            .ToListAsync();

        var balances = await ctx.PartyAccountTransactions
            .AsNoTracking()
            .Where(t => parties.Select(p => p.Id).Contains(t.PartyId))
            .GroupBy(t => t.PartyId)
            .Select(g => new { PartyId = g.Key, Balance = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(g => g.PartyId, g => g.Balance);

        foreach (var party in parties)
        {
            var bal = balances.GetValueOrDefault(party.Id, 0m);
            Suppliers.Add(new SupplierOption(party.Id, party.Name, bal));
        }

        if (SelectedSupplier is null)
            SelectedSupplier = Suppliers.FirstOrDefault();

        Status = _locationId == 0
            ? "مفيش مخزن — أضف مخزن من الإعدادات"
            : Suppliers.Count == 0
                ? "مفيش موردين — أضفهم من شاشة الأطراف"
                : $"{Suppliers.Count} مورد جاهز للاستلام";

        await ReloadProductsAsync(ctx);
        await FillInvoiceNoAsync(ctx);
        await Settlement.LoadAsync();
    }

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
        var active = await ctx.Products
            .AsNoTracking()
            .Include(p => p.Units)
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();

        var productIds = active.Select(p => p.Id).ToArray();

        var stockRows = await ctx.StockBalances
            .AsNoTracking()
            .Include(b => b.Lot)
            .Where(b => b.LocationId == _locationId && productIds.Contains(b.Lot!.ProductId))
            .ToListAsync();

        var stockByProduct = stockRows
            .GroupBy(b => b.Lot!.ProductId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var latestPurchaseByProduct = (await ctx.PurchaseItems
                .AsNoTracking()
                .Where(pi => productIds.Contains(pi.ProductId))
                .OrderByDescending(pi => pi.Id)
                .ToListAsync())
            .GroupBy(pi => pi.ProductId)
            .ToDictionary(g => g.Key, g => g.First());

        Products.Clear();
        foreach (var product in active)
        {
            var activeUnits = product.Units
                .Where(u => u.IsActive)
                .OrderBy(u => u.IsDefaultPurchase ? 0 : u.IsBase ? 1 : 2)
                .ToList();

            if (activeUnits.Count == 0) continue;

            stockByProduct.TryGetValue(product.Id, out var rows);

            var option = new ReceiveProductOption(product.Id, product.Sku ?? string.Empty, product.Name);

            foreach (var unit in activeUnits)
            {
                var onHand = rows is not null && rows.Count > 0
                    ? Rounding.Quantity(rows.Sum(b => b.Quantity / unit.Factor))
                    : 0m;

                decimal purchasePrice = unit.PurchasePrice;
                if (purchasePrice <= 0m && latestPurchaseByProduct.TryGetValue(product.Id, out var latestPurchase))
                {
                    var factorRatio = latestPurchase.UnitFactor > 0 ? unit.Factor / latestPurchase.UnitFactor : 1m;
                    purchasePrice = Rounding.Amount(latestPurchase.UnitPrice * factorRatio);
                }

                option.AvailableUnits.Add(new ReceiveUnitOption(
                    unit.Id, unit.Name, unit.Factor, purchasePrice, onHand));
            }

            var defUnit = activeUnits.FirstOrDefault(u => u.IsDefaultPurchase) ?? activeUnits.FirstOrDefault();
            option.SelectedUnit = option.AvailableUnits.FirstOrDefault(u => u.ProductUnitId == defUnit?.Id)
                               ?? option.AvailableUnits.FirstOrDefault();

            Products.Add(option);
        }

        ApplyProductFilter();
    }

    partial void OnProductSearchTextChanged(string value) => ApplyProductFilter();

    private void ApplyProductFilter()
    {
        FilteredProducts.Clear();
        var search = ProductSearchText.Trim();
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        foreach (var p in Products)
        {
            if (!hasSearch
                || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || p.Sku.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                FilteredProducts.Add(p);
            }
        }
    }

    [RelayCommand]
    private void SubmitSearch()
    {
        if (string.IsNullOrWhiteSpace(ProductSearchText)) return;

        var search = ProductSearchText.Trim();
        var match = Products.FirstOrDefault(p => string.Equals(p.Sku, search, StringComparison.OrdinalIgnoreCase))
                    ?? FilteredProducts.FirstOrDefault(p => string.Equals(p.Sku, search, StringComparison.OrdinalIgnoreCase))
                    ?? Products.FirstOrDefault(p => string.Equals(p.Name, search, StringComparison.OrdinalIgnoreCase))
                    ?? FilteredProducts.FirstOrDefault();

        if (match is not null)
        {
            AddProductToLines(match);
        }
        else
        {
            Status = $"لم يتم العثور على صنف بالباركود: {search}";
        }

        ProductSearchText = string.Empty;
    }

    private ReceiveProductOption? _selectedProduct;
    public ReceiveProductOption? SelectedProduct
    {
        get => _selectedProduct;
        set
        {
            _selectedProduct = value;
            OnPropertyChanged(nameof(SelectedProduct));
            OnPropertyChanged(nameof(Units));
            SelectedUnit = value?.SelectedUnit;
        }
    }

    [ObservableProperty] private ReceiveUnitOption? _selectedUnit;
    [ObservableProperty] private string _quantity = "1";
    [ObservableProperty] private string _unitPrice = "0";

    public ObservableCollection<ReceiveUnitOption> Units => SelectedProduct?.AvailableUnits ?? [];

    [RelayCommand]
    public Task AddLineAsync()
    {
        if (SelectedProduct is null) return Task.CompletedTask;
        var unit = SelectedUnit ?? SelectedProduct.SelectedUnit;
        if (unit is null) return Task.CompletedTask;

        var qty = decimal.TryParse(Quantity, out var q) ? q : 1m;
        var price = decimal.TryParse(UnitPrice, out var p) ? p : unit.PurchasePrice;

        var existing = Lines.FirstOrDefault(l =>
            l.ProductId == SelectedProduct.ProductId && l.ProductUnitId == unit.ProductUnitId);

        if (existing is not null)
        {
            existing.Quantity += qty;
        }
        else
        {
            var line = new ReceiveLine
            {
                ProductId = SelectedProduct.ProductId,
                ProductUnitId = unit.ProductUnitId,
                Sku = SelectedProduct.Sku,
                ProductName = SelectedProduct.Name,
                UnitName = unit.UnitName,
                UnitFactor = unit.UnitFactor,
                Quantity = qty,
                UnitPrice = price,
                ReferencePurchasePrice = unit.PurchasePrice,
            };

            foreach (var u in SelectedProduct.AvailableUnits)
                line.AvailableUnits.Add(u);

            line.SelectedUnit = unit;
            line.WireHandlers(() => Recalculate());
            Lines.Add(line);
        }

        Recalculate();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void SelectProduct(ReceiveProductOption? product)
    {
        if (product is null) return;
        AddProductToLines(product);
    }

    [RelayCommand]
    private void AddProductToLines(ReceiveProductOption? product)
    {
        if (product is null) return;
        var unit = product.SelectedUnit;
        if (unit is null) return;

        var existing = Lines.FirstOrDefault(l =>
            l.ProductId == product.ProductId
            && l.ProductUnitId == unit.ProductUnitId);

        if (existing is not null)
        {
            existing.Quantity += 1m;
            Status = $"تمت زيادة كمية {product.Name} إلى {existing.QuantityText}";
        }
        else
        {
            var line = new ReceiveLine
            {
                ProductId = product.ProductId,
                ProductUnitId = unit.ProductUnitId,
                Sku = product.Sku,
                ProductName = product.Name,
                UnitName = unit.UnitName,
                UnitFactor = unit.UnitFactor,
                Quantity = 1m,
                UnitPrice = unit.PurchasePrice,
                ReferencePurchasePrice = unit.PurchasePrice,
            };

            foreach (var u in product.AvailableUnits)
                line.AvailableUnits.Add(u);

            line.SelectedUnit = unit;
            line.WireHandlers(() => Recalculate());

            Lines.Add(line);
            Status = $"تمت إضافة {product.Name} ({unit.UnitName}) للاستلام";
        }

        Recalculate();
    }

    [RelayCommand]
    private void IncrementLine(ReceiveLine? line)
    {
        if (line is null) return;
        line.Quantity += 1m;
        Recalculate();
    }

    [RelayCommand]
    private void DecrementLine(ReceiveLine? line)
    {
        if (line is null) return;
        if (line.Quantity > 1m)
        {
            line.Quantity -= 1m;
            Recalculate();
        }
        else
        {
            Lines.Remove(line);
            Recalculate();
        }
    }

    [RelayCommand]
    private void RemoveLine(ReceiveLine? line)
    {
        if (line is null) return;
        Lines.Remove(line);
        Recalculate();
    }

    [RelayCommand]
    private void RequestClear()
    {
        if (Lines.Count > 0 || HasScan)
            IsClearConfirmVisible = true;
    }

    [RelayCommand]
    private void CancelClear()
    {
        IsClearConfirmVisible = false;
    }

    [RelayCommand]
    private void Clear()
    {
        Lines.Clear();
        Scanned.Clear();
        HasScan = false;
        ScanStatus = string.Empty;
        IsClearConfirmVisible = false;
        Settlement.Reset();
        Recalculate();
        Status = "تم تفريغ الاستلام";
    }

    public string TotalText => DashboardViewModel.Money(Total);

    private void Recalculate()
    {
        Total = Rounding.Amount(Lines.Sum(l => l.LineTotal));
        OnPropertyChanged(nameof(TotalText));
        CanPost = SelectedSupplier is not null && Lines.Count > 0 && Total > 0m && !Busy;

        PostHint = Lines.Count == 0 || CanPost
            ? string.Empty
            : SelectedSupplier is null
                ? "اختار المورد عشان تحفظ الاستلام"
                : Total <= 0m
                    ? "الإجمالي لازم يكون أكبر من صفر"
                    : string.Empty;
    }

    partial void OnSelectedSupplierChanged(SupplierOption? value) => Recalculate();
    partial void OnTotalChanged(decimal value) => Recalculate();
    partial void OnBusyChanged(bool value) => Recalculate();

    [RelayCommand]
    private async Task PostAsync()
    {
        if (!CanPost || SelectedSupplier is null)
            return;

        // Detect conflicting proposed reference prices for the same ProductUnitId
        var approvedUpdates = Lines
            .Where(l => l.UpdateProductPurchasePrice && l.UnitPrice > 0m)
            .ToList();

        var conflicts = approvedUpdates
            .GroupBy(l => l.ProductUnitId)
            .Where(g => g.Select(x => x.UnitPrice).Distinct().Count() > 1)
            .ToList();

        if (conflicts.Count > 0)
        {
            var firstConflict = conflicts.First();
            var conflictPrices = string.Join(" و ", firstConflict.Select(x => $"{x.UnitPrice:N2} ج.م").Distinct());
            Status = $"تعارض في تحديث سعر الشراء للوحدة '{firstConflict.First().UnitName}': أسعار مختلفة معتمدة ({conflictPrices}). يرجى توحيد السعر.";
            return;
        }

        var updatesToApply = approvedUpdates
            .GroupBy(l => l.ProductUnitId)
            .ToDictionary(g => g.Key, g => g.First());

        Busy = true;
        Recalculate();
        Status = "جاري الحفظ…";

        var postedLines = Lines.Count;

        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            await using IDbContextTransaction tx = await TransactionScope.BeginAsync(ctx, default);
            try
            {
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

                // Apply approved reference price updates inside the same transaction
                if (updatesToApply.Count > 0)
                {
                    var unitIds = updatesToApply.Keys.ToList();
                    var unitsToUpdate = await ctx.ProductUnits
                        .Where(u => unitIds.Contains(u.Id))
                        .ToListAsync();

                    foreach (var (unitId, line) in updatesToApply)
                    {
                        var unit = unitsToUpdate.FirstOrDefault(u => u.Id == unitId)
                            ?? throw new DomainException($"الوحدة رقم {unitId} غير موجودة في قاعدة البيانات.");

                        if (unit.ProductId != line.ProductId)
                            throw new DomainException($"الوحدة '{unit.Name}' لا تنتمي للصنف المحدد.");

                        if (unit.PurchasePrice != line.UnitPrice)
                        {
                            unit.PurchasePrice = Rounding.Amount(line.UnitPrice);
                            unit.Validate();
                        }
                    }

                    await ctx.SaveChangesAsync();
                }

                await TransactionScope.CommitAsync(tx, ctx, default);

                var paid = Settlement.Settlement == SettlementChoice.Deferred
                    ? "آجل"
                    : $"مدفوع {DashboardViewModel.Money(Settlement.AmountFor(purchase.Total))}";

                Status = $"تم الاستلام — {result.DocNo} · {postedLines} سطر · {paid} · الإجمالي {DashboardViewModel.Money(Total)}";

                Lines.Clear();
                InvoiceNo = string.Empty;
                Settlement.Reset();
                Recalculate();
                await LoadAsync();
            }
            catch
            {
                await TransactionScope.RollbackAsync(tx, ctx, default);
                throw;
            }
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