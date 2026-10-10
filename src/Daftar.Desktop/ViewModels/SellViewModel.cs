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
using Daftar.Domain.Sales;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.ViewModels;

public partial class SellViewModel : ObservableObject
{
    private readonly IServiceProvider _services;

    private int _locationId;

    /// <summary>O(1) lookups by ProductUnitId — replaces repeated FirstOrDefault scans of Products/Cart.</summary>
    private readonly Dictionary<int, ProductOption> _productsByUnitId = new();
    private readonly Dictionary<int, CartLine> _cartByUnitId = new();

    public ObservableCollection<PartyOption> Customers { get; } = [];

    public ObservableCollection<ProductOption> Products { get; } = [];

    public ObservableCollection<ProductOption> FilteredProducts { get; } = [];

    public ObservableCollection<CartLine> Cart { get; } = [];

    [ObservableProperty] private PartyOption? _selectedCustomer;
    [ObservableProperty] private ProductOption? _selectedProduct;
    [ObservableProperty] private decimal _quantity = 1m;
    [ObservableProperty] private decimal _discount = 0m;
    [ObservableProperty] private string _discountError = string.Empty;
    [ObservableProperty] private decimal _totalDiscount;
    [ObservableProperty] private decimal _invoiceDiscount;
    [ObservableProperty] private string _invoiceDiscountText = string.Empty;
    [ObservableProperty] private string _invoiceDiscountError = string.Empty;
    [ObservableProperty] private string _productSearchText = string.Empty;
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private string _status = "اختار العميل والمنتج";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _canPost;
    [ObservableProperty] private bool _isClearConfirmVisible;
    /// <summary>
    /// How this sale is settled at the counter. Replaces a free-text box, which could not say how
    /// the money was taken or refuse an amount larger than the sale.
    /// </summary>
    public SettlementPanel Settlement { get; }

    /// <summary>Amount still owed after any payment taken at the till.</summary>
    [ObservableProperty] private decimal _amountDue;

    [ObservableProperty]
    private decimal _subtotal;

    [ObservableProperty]
    private decimal _total;

    public SellViewModel(IServiceProvider services)
    {
        _services = services;
        Settlement = new SettlementPanel(services);
        // Subscribe once here — LoadAsync used to re-subscribe on every reload (including after
        // every posted sale), stacking duplicate handlers and leaking the panel each time.
        Settlement.PropertyChanged += OnSettlementChanged;
    }

    public async Task LoadAsync()
    {
        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        // The role's discount ceiling, read once per load. Role.MaxDiscountPercent was validated
        // on write and displayed in settings, but never consulted during a sale -- so a cashier
        // capped at 0% could still discount every line to cost.
        MaxAllowedDiscountPercent = await scope.ServiceProvider
            .GetRequiredService<IUserAuthorizer>()
            .MaxDiscountPercentAsync();
        OnPropertyChanged(nameof(LineDiscountRuleHint));

        Customers.Clear();
        var parties = await ctx.Parties
                     .AsNoTracking()
                     .Where(p => p.IsCustomer && p.Status == PartyStatus.Active)
                     .OrderBy(p => p.Name)
                     .ToListAsync();

        var balances = await ctx.PartyAccountTransactions
                     .AsNoTracking()
                     .GroupBy(t => t.PartyId)
                     .Select(g => new { PartyId = g.Key, Balance = g.Sum(t => t.Amount) })
                     .ToDictionaryAsync(g => g.PartyId, g => g.Balance);

        foreach (var party in parties)
        {
            var bal = balances.GetValueOrDefault(party.Id, 0m);
            Customers.Add(new PartyOption(party.Id, party.Name, party.CustomerType, bal));
        }

        if (SelectedCustomer is null)
        {
            SelectedCustomer = Customers.FirstOrDefault(c => c.Name.Contains("نقدي")) ?? Customers.FirstOrDefault();
        }

        _locationId = await DefaultLocationAsync(ctx);
        var priceListId = await DefaultPriceListAsync(ctx);

        await LoadProductsAsync(ctx, priceListId);

        await Settlement.LoadAsync();
        Status = "جاهز للبيع";
    }

    private async Task LoadProductsAsync(DaftarContext ctx, int? priceListId)
    {
        Products.Clear();
        _productsByUnitId.Clear();

        var today = DateOnly.FromDateTime(DateTime.Now);
        var resolver = new PriceResolutionService(ctx);

        // Units are read below to pick the selling unit. There are no lazy-loading proxies,
        // so without this the navigation collection is empty and every product is skipped —
        // which left the till with an empty product list and no error to explain it.
        var active = await ctx.Products
            .AsNoTracking()
            .Include(p => p.Units)
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();

        if (active.Count == 0)
        {
            ApplyProductFilter();
            return;
        }

        var productIds = active.Select(p => p.Id).ToArray();

        // ------------------------------------------------------------------
        // Batched reads. The old code ran OnHandAsync + PurchasePriceAsync per
        // product (3–5 round trips each). Everything stock/lot/purchase related
        // now comes down in three queries total and is grouped in memory.
        // ------------------------------------------------------------------
        var stockRows = await ctx.StockBalances
            .AsNoTracking()
            .Include(b => b.Lot)
            .Where(b => b.LocationId == _locationId && productIds.Contains(b.Lot!.ProductId))
            .ToListAsync();

        var stockByProduct = stockRows
            .GroupBy(b => b.Lot!.ProductId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Latest lot per product (fallback cost basis when nothing is on hand).
        var latestLotByProduct = (await ctx.Lots
                .AsNoTracking()
                .Where(l => productIds.Contains(l.ProductId))
                .OrderByDescending(l => l.ReceivedOn)
                .ToListAsync())
            .GroupBy(l => l.ProductId)
            .ToDictionary(g => g.Key, g => g.First());

        // Latest purchase line per product (final fallback cost basis).
        var latestPurchaseByProduct = (await ctx.PurchaseItems
                .AsNoTracking()
                .Where(pi => productIds.Contains(pi.ProductId))
                .OrderByDescending(pi => pi.Id)
                .ToListAsync())
            .GroupBy(pi => pi.ProductId)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var product in active)
        {
            var activeUnits = product.Units
                .Where(u => u.IsActive)
                .OrderBy(u => u.IsDefaultSale ? 0 : u.IsBase ? 1 : 2)
                .ToList();

            if (activeUnits.Count == 0)
                continue;

            stockByProduct.TryGetValue(product.Id, out var rows);

            var option = new ProductOption(product.Id, product.Sku ?? string.Empty, product.Name);

            foreach (var unit in activeUnits)
            {
                decimal price;
                try
                {
                    price = (await resolver.ResolveAsync(priceListId, unit.Id, today)).Price;
                }
                catch (DomainException)
                {
                    if (!unit.IsBase && product.BaseUnit is not null)
                    {
                        try
                        {
                            var basePrice = (await resolver.ResolveAsync(priceListId, product.BaseUnit.Id, today)).Price;
                            price = Rounding.Amount(basePrice * unit.Factor);
                        }
                        catch
                        {
                            price = 0m;
                        }
                    }
                    else
                    {
                        price = 0m;
                    }
                }

                var onHand = OnHandFromRows(rows, unit);
                var purchasePrice = PurchasePriceFromSnapshots(rows, unit, latestLotByProduct, latestPurchaseByProduct, product.Id);
                var sku = !string.IsNullOrWhiteSpace(unit.Barcode) ? unit.Barcode : product.Sku ?? string.Empty;

                var unitOption = new CartUnitOption(unit.Id, unit.Name, unit.Factor, price, purchasePrice, onHand);
                option.AvailableUnits.Add(unitOption);

                // Map every unit id → this product option for O(1) cart lookups
                _productsByUnitId[unit.Id] = option;
            }

            // Select default unit (IsDefaultSale → IsBase → first)
            option.SelectedUnit = option.AvailableUnits.FirstOrDefault()!;

            Products.Add(option);
        }

        ApplyProductFilter();
    }

    /// <summary>Stock on hand across sellable batches, computed from the pre-loaded balance rows.</summary>
    private static decimal OnHandFromRows(List<StockBalance>? rows, ProductUnit unit)
    {
        if (rows is null || rows.Count == 0)
            return 0m;

        return Rounding.Quantity(rows
            .Where(b => b.Lot!.IsAvailable())
            .Sum(b => b.Quantity / unit.Factor));
    }

    private static decimal PurchasePriceFromSnapshots(
        List<StockBalance>? rows,
        ProductUnit unit,
        Dictionary<int, Lot> latestLotByProduct,
        Dictionary<int, PurchaseItem> latestPurchaseByProduct,
        int productId)
    {
        if (rows is not null)
        {
            var availableLot = rows
                .Where(b => b.Quantity > 0 && b.Lot!.IsAvailable())
                .OrderBy(b => b.Lot!.ReceivedOn)
                .FirstOrDefault();

            if (availableLot?.Lot is not null)
                return Rounding.Unit(availableLot.Lot.UnitCost * unit.Factor);
        }

        if (latestLotByProduct.TryGetValue(productId, out var latestLot))
            return Rounding.Unit(latestLot.UnitCost * unit.Factor);

        if (latestPurchaseByProduct.TryGetValue(productId, out var latestPurchase))
        {
            var factorRatio = latestPurchase.UnitFactor > 0 ? unit.Factor / latestPurchase.UnitFactor : 1m;
            return Rounding.Unit(latestPurchase.UnitPrice * factorRatio);
        }

        return 0m;
    }

    partial void OnProductSearchTextChanged(string value) => ApplyProductFilter();

    private void ApplyProductFilter()
    {
        FilteredProducts.Clear();
        var search = ProductSearchText.Trim();
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        foreach (var p in Products)
        {
            // OrdinalIgnoreCase avoids the two ToLowerInvariant allocations per product per keystroke.
            if (!hasSearch
                || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || p.Sku.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                FilteredProducts.Add(p);
            }
        }
    }

    [RelayCommand]
    private void SelectProduct(ProductOption? product)
    {
        if (product is null) return;
        SelectedProduct = product;
        AddProductToCart(product);
    }

    private void WireCartLine(CartLine line)
    {
        line.OnQuantityUpdated = () =>
        {
            var onHand = line.SelectedUnit?.OnHand ?? 0m;
            if (line.Quantity > onHand)
            {
                Status = $"تنبيه: الكمية ({line.Quantity}) أكبر من المتاح بالمخزن ({onHand} {line.UnitName})";
            }
            Recalculate();
        };
    }

    [RelayCommand]
    private void AddProductToCart(ProductOption? product)
    {
        if (product is null) return;
        var unit = product.SelectedUnit;
        if (unit is null) return;

        if (!unit.IsPriced)
        {
            Status = $"'{product.Name} - {unit.UnitName}' مالوش سعر بيع. ظبّط قائمة الأسعار الأول.";
            return;
        }

        if (_cartByUnitId.TryGetValue(unit.ProductUnitId, out var existing))
        {
            if (existing.Quantity < unit.OnHand)
            {
                existing.Quantity += 1m;
                var unitDiscount = existing.Quantity > 1 ? existing.Discount / (existing.Quantity - 1) : 0m;
                if (unitDiscount > 0m)
                {
                    existing.Discount = Rounding.Amount(unitDiscount * existing.Quantity);
                }
                existing.LineTotal = Rounding.LineTotal(existing.Quantity, existing.UnitPrice, existing.Discount);
                Recalculate();
                Status = $"تمت زيادة كمية {product.Name} إلى {existing.QuantityText}";
            }
            else
            {
                Status = $"الكمية المتاحة {unit.OnHand} {unit.UnitName} فقط";
            }
        }
        else
        {
            var line = new CartLine
            {
                ProductId     = product.ProductId,
                ProductUnitId = unit.ProductUnitId,
                Sku           = product.Sku,
                Name          = product.Name,
                UnitName      = unit.UnitName,
                UnitPrice     = unit.Price,
                Quantity      = 1m,
                Discount      = 0m,
                PurchasePrice = unit.PurchasePrice,
                LineTotal     = unit.Price,
            };

            foreach (var u in product.AvailableUnits) line.AvailableUnits.Add(u);
            line.SelectedUnit = unit;

            WireCartLine(line);
            Cart.Add(line);
            _cartByUnitId[line.ProductUnitId] = line;
            Recalculate();
            Status = $"تمت إضافة {product.Name} ({unit.UnitName})";
        }
    }

    [RelayCommand]
    private void IncrementLine(CartLine? line)
    {
        if (line is null) return;
        var onHand = line.SelectedUnit?.OnHand ?? 0m;
        if (line.Quantity >= onHand)
        {
            Status = $"الكمية المتاحة {onHand} {line.UnitName} فقط";
            return;
        }

        var unitDiscount = line.Quantity > 0 ? line.Discount / line.Quantity : 0m;
        line.Quantity += 1m;
        if (unitDiscount > 0m)
        {
            line.Discount = Rounding.Amount(unitDiscount * line.Quantity);
        }
        line.LineTotal = Rounding.LineTotal(line.Quantity, line.UnitPrice, line.Discount);
        Recalculate();
    }

    [RelayCommand]
    private void DecrementLine(CartLine? line)
    {
        if (line is null) return;
        if (line.Quantity > 1m)
        {
            var unitDiscount = line.Discount / line.Quantity;
            line.Quantity -= 1m;
            if (unitDiscount > 0m)
            {
                line.Discount = Rounding.Amount(unitDiscount * line.Quantity);
            }
            line.LineTotal = Rounding.LineTotal(line.Quantity, line.UnitPrice, line.Discount);
            Recalculate();
        }
        else
        {
            Cart.Remove(line);
            _cartByUnitId.Remove(line.ProductUnitId);
            Recalculate();
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
            AddProductToCart(match);
        }
        else
        {
            Status = $"لم يتم العثور على صنف بالباركود: {search}";
        }

        ProductSearchText = string.Empty;
    }

    [ObservableProperty] private CartLine? _selectedCartLine;
    [ObservableProperty] private decimal _lineDiscountInput;
    [ObservableProperty] private string _lineDiscountError = string.Empty;

    [RelayCommand]
    private void SelectCartLine(CartLine? line)
    {
        SelectedCartLine = line;
        LineDiscountError = string.Empty;
        if (line is not null)
        {
            LineDiscountInput = line.Quantity > 0 ? Rounding.Amount(line.Discount / line.Quantity) : 0m;
        }
        OnPropertyChanged(nameof(LineDiscountRuleHint));
    }

    [RelayCommand]
    private void CloseLineDiscount()
    {
        SelectedCartLine = null;
        LineDiscountError = string.Empty;
    }

    [RelayCommand]
    private void ApplyLineDiscount()
    {
        if (SelectedCartLine is null) return;
        LineDiscountError = string.Empty;

        if (LineDiscountInput < 0m)
        {
            LineDiscountError = "الخصم لا يمكن أن يكون سالباً.";
            return;
        }

        if (LineDiscountInput > SelectedCartLine.PurchasePrice)
        {
            LineDiscountError = $"الخصم ({LineDiscountInput:N2} ج.م) لا يمكن أن يتخطى سعر شراء المنتج ({SelectedCartLine.PurchasePrice:N2} ج.م).";
            return;
        }

        // The role's ceiling, not just the cost floor. MaxDiscountPercent was validated on write
        // and shown in settings but never read here, so any discount up to cost was allowed to
        // anyone regardless of what their role permitted.
        if (MaxAllowedDiscountPercent is { } cap)
        {
            var ceiling = MaxDiscountCeilingFor(SelectedCartLine.UnitPrice, cap);
            if (LineDiscountInput > ceiling)
            {
                LineDiscountError = $"خصمك ({LineDiscountInput:N2} ج.م) أكبر من الحد المسموح لدورك ({ceiling:N2} ج.م، {cap:N0}%).";
                return;
            }
        }

        if (SelectedCartLine.UnitPrice - LineDiscountInput < SelectedCartLine.PurchasePrice)
        {
            LineDiscountError = $"سعر البيع بعد الخصم لا يمكن أن يقل عن سعر شراء المنتج ({SelectedCartLine.PurchasePrice:N2} ج.م).";
            return;
        }

        SelectedCartLine.Discount = Rounding.Amount(LineDiscountInput * SelectedCartLine.Quantity);
        SelectedCartLine.LineTotal = Rounding.LineTotal(SelectedCartLine.Quantity, SelectedCartLine.UnitPrice, SelectedCartLine.Discount);
        Recalculate();
        SelectedCartLine = null;
    }

    public string LineDiscountRuleHint => SelectedCartLine is null
        ? string.Empty
        : MaxAllowedDiscountPercent is { } cap
            ? $"أقصى خصم للوحدة: {Math.Min(SelectedCartLine.PurchasePrice, MaxDiscountCeilingFor(SelectedCartLine.UnitPrice, cap)):N2} ج.م"
            : $"أقصى خصم مسموح به للوحدة: {SelectedCartLine.PurchasePrice:N2} ج.م (سعر الشراء: {SelectedCartLine.PurchasePrice:N2} ج.م)";

    /// <summary>
    /// The signed-in role's discount ceiling as a percentage, or null when the role sets none.
    /// </summary>
    public decimal? MaxAllowedDiscountPercent { get; private set; }

    private static decimal MaxDiscountCeilingFor(decimal unitPrice, decimal percent)
        => unitPrice * percent / 100m;

    [ObservableProperty] private string _paymentMode = "Cash";
    [ObservableProperty] private bool _isPaymentOtherExpanded;

    partial void OnPaymentModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsPaymentCash));
        OnPropertyChanged(nameof(IsPaymentCredit));
        OnPropertyChanged(nameof(IsPaymentOther));
        OnPropertyChanged(nameof(CurrentPaymentModeText));
    }

    public bool IsPaymentCash => PaymentMode == "Cash";
    public bool IsPaymentCredit => PaymentMode == "Credit";
    public bool IsPaymentOther => PaymentMode == "Other";

    [RelayCommand]
    private void SetPaymentCash()
    {
        PaymentMode = "Cash";
        IsPaymentOtherExpanded = false;
        var full = Settlement.Choices.FirstOrDefault(c => c.Value == SettlementChoice.Full);
        if (full is not null) Settlement.Choice = full;
        var cash = Settlement.Methods.FirstOrDefault(m => m.Value == PaymentMethod.Cash);
        if (cash is not null) Settlement.Method = cash;
        Recalculate();
    }

    [RelayCommand]
    private void SetPaymentCredit()
    {
        PaymentMode = "Credit";
        IsPaymentOtherExpanded = false;
        var deferred = Settlement.Choices.FirstOrDefault(c => c.Value == SettlementChoice.Deferred);
        if (deferred is not null) Settlement.Choice = deferred;
        Recalculate();
    }

    [RelayCommand]
    private void TogglePaymentOther()
    {
        IsPaymentOtherExpanded = !IsPaymentOtherExpanded;
        if (IsPaymentOtherExpanded)
        {
            PaymentMode = "Other";
        }
        else
        {
            PaymentMode = Settlement.Choice?.Value switch
            {
                SettlementChoice.Full => "Cash",
                SettlementChoice.Deferred => "Credit",
                _ => "Other"
            };
        }
        Recalculate();
    }

    public string CurrentPaymentModeText => PaymentMode switch
    {
        "Cash" => "نقدي (كاش بالكامل)",
        "Credit" => "آجل (على الحساب)",
        _ => "طريقة مخصصة"
    };

    public bool IsPaymentDeferredOrPartial => Settlement.Choice?.Value != SettlementChoice.Full;
    public bool HasTotalDiscount => TotalDiscount > 0m;
    public string SubtotalText => DashboardViewModel.Money(Subtotal);

    partial void OnInvoiceDiscountTextChanged(string value)
    {
        InvoiceDiscountError = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            InvoiceDiscount = 0m;
            Recalculate();
            return;
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            || decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed))
        {
            if (parsed < 0m)
            {
                InvoiceDiscountError = "الخصم لا يمكن أن يكون سالباً";
                return;
            }
            if (parsed > Subtotal)
            {
                InvoiceDiscountError = "الخصم لا يمكن أن يتجاوز المجموع الفرعي";
                return;
            }
            InvoiceDiscount = Rounding.Amount(parsed);
            Recalculate();
        }
        else
        {
            InvoiceDiscountError = "مبلغ الخصم غير صحيح";
        }
    }

    [RelayCommand]
    private void IncrementQuantity()
    {
        if (SelectedProduct is not null && Quantity < SelectedProduct.OnHand)
        {
            Quantity += 1m;
        }
    }

    [RelayCommand]
    private void DecrementQuantity()
    {
        if (Quantity > 1m)
        {
            Quantity -= 1m;
        }
    }

    public bool ValidateDiscount()
    {
        DiscountError = string.Empty;
        if (SelectedProduct is null)
            return true;

        if (Discount < 0m)
        {
            DiscountError = "الخصم لا يمكن أن يكون سالباً.";
            return false;
        }

        if (Discount > SelectedProduct.PurchasePrice)
        {
            DiscountError = $"الخصم ({Discount:N2} ج.م) لا يمكن أن يتخطى سعر شراء المنتج ({SelectedProduct.PurchasePrice:N2} ج.م).";
            return false;
        }

        if (SelectedProduct.Price - Discount < SelectedProduct.PurchasePrice)
        {
            DiscountError = $"سعر البيع بعد الخصم ({SelectedProduct.Price - Discount:N2} ج.م) لا يمكن أن يقل عن سعر شراء المنتج ({SelectedProduct.PurchasePrice:N2} ج.م).";
            return false;
        }

        return true;
    }

    partial void OnDiscountChanged(decimal value)
    {
        ValidateDiscount();
        OnPropertyChanged(nameof(CurrentLineTotalText));
    }

    partial void OnSelectedProductChanged(ProductOption? value)
    {
        ValidateDiscount();
        OnPropertyChanged(nameof(CurrentLineTotalText));
        OnPropertyChanged(nameof(MaxDiscountText));
        OnPropertyChanged(nameof(DiscountRuleHint));
    }

    public string CurrentLineTotalText
    {
        get
        {
            if (SelectedProduct is null) return "0.00 ج.م";
            var gross = Quantity * SelectedProduct.Price;
            var disc = Math.Min(gross, Discount * Quantity);
            return DashboardViewModel.Money(gross - disc);
        }
    }

    public string MaxDiscountText => SelectedProduct is not null
        ? $"{SelectedProduct.PurchasePrice:N2} ج.م"
        : "0.00 ج.م";

    public string DiscountRuleHint => SelectedProduct is not null
        ? $"أقصى خصم للوحدة: {SelectedProduct.PurchasePrice:N2} ج.م (سعر الشراء: {SelectedProduct.PurchasePrice:N2} ج.م)"
        : string.Empty;

    public string TotalDiscountText => DashboardViewModel.Money(TotalDiscount);

    private void DoSearch()
    {
        // Filtering is done by the view's ItemsSource, so this only refreshes the hint text.
        Status = $"بحث: {Search}";
    }

    /// <summary>Adds the selected product to the cart at the resolved list price.</summary>
    [RelayCommand]
    private void AddToCart()
    {
        if (SelectedProduct is null)
        {
            Status = "اختار منتج الأول";
            return;
        }

        if (Quantity <= 0m)
        {
            Status = "الكمية لازم تكون أكبر من صفر";
            return;
        }

        if (SelectedProduct.OnHand < Quantity)
        {
            Status = $"الكمية المتاحة {SelectedProduct.OnHand} {SelectedProduct.UnitName} فقط";
            return;
        }

        if (!ValidateDiscount())
        {
            Status = DiscountError;
            return;
        }

        var lineDiscount = Rounding.Amount(Discount * Quantity);
        if (_cartByUnitId.TryGetValue(SelectedProduct.ProductUnitId, out var existing))
        {
            existing.Quantity += Quantity;
            existing.Discount += lineDiscount;
            existing.LineTotal = Rounding.LineTotal(existing.Quantity, existing.UnitPrice, existing.Discount);
        }
        else
        {
            var line = new CartLine
            {
                ProductId = SelectedProduct.ProductId,
                ProductUnitId = SelectedProduct.ProductUnitId,
                Sku = SelectedProduct.Sku,
                Name = SelectedProduct.Name,
                UnitName = SelectedProduct.UnitName,
                UnitPrice = SelectedProduct.Price,
                Quantity = Quantity,
                Discount = lineDiscount,
                PurchasePrice = SelectedProduct.PurchasePrice,
                LineTotal = Rounding.LineTotal(Quantity, SelectedProduct.Price, lineDiscount),
            };

            var unitsForProduct = Products
                .Where(p => p.ProductId == SelectedProduct.ProductId)
                .Select(p => new CartUnitOption(p.ProductUnitId, p.UnitName, p.UnitFactor, p.Price, p.PurchasePrice, p.OnHand))
                .ToList();
            foreach (var u in unitsForProduct) line.AvailableUnits.Add(u);
            line.SelectedUnit = line.AvailableUnits.FirstOrDefault(u => u.ProductUnitId == SelectedProduct.ProductUnitId);

            WireCartLine(line);
            Cart.Add(line);
            _cartByUnitId[line.ProductUnitId] = line;
        }

        Recalculate();
        Quantity = 1m;
        Discount = 0m;
        DiscountError = string.Empty;
        Status = "تمت الإضافة";
    }

    [RelayCommand]
    private void RemoveLine(CartLine? line)
    {
        if (line is null)
            return;

        Cart.Remove(line);
        _cartByUnitId.Remove(line.ProductUnitId);
        Recalculate();
    }

    [RelayCommand]
    private void RequestClear()
    {
        if (Cart.Count == 0)
            return;
        IsClearConfirmVisible = true;
    }

    [RelayCommand]
    private void Clear()
    {
        Cart.Clear();
        _cartByUnitId.Clear();
        InvoiceDiscount = 0m;
        InvoiceDiscountText = string.Empty;
        InvoiceDiscountError = string.Empty;
        Settlement.Reset();
        SelectedCartLine = null;
        IsClearConfirmVisible = false;
        PaymentMode = "Cash";
        IsPaymentOtherExpanded = false;
        Recalculate();
        Status = "تم تفريغ السلة";
    }

    [RelayCommand]
    private void CancelClear()
    {
        IsClearConfirmVisible = false;
    }

    private void Recalculate()
    {
        // Single pass over the cart instead of three separate Sum() enumerations.
        decimal subtotal = 0m, lineDiscounts = 0m;
        foreach (var l in Cart)
        {
            subtotal += l.Quantity * l.UnitPrice;
            lineDiscounts += l.Discount;
        }

        Subtotal = Rounding.Amount(subtotal);

        if (InvoiceDiscount > Subtotal)
        {
            InvoiceDiscount = Subtotal;
            InvoiceDiscountText = Subtotal > 0 ? Subtotal.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
        }

        TotalDiscount = Rounding.Amount(lineDiscounts + InvoiceDiscount);
        Total = Math.Max(0m, Rounding.Amount(Subtotal - TotalDiscount));

        // What the customer still owes after whatever is being handed over now.
        AmountDue = Rounding.Amount(Total - Settlement.AmountFor(Total));

        CanPost = Cart.Count > 0 && SelectedCustomer is not null && Subtotal > 0m;
        OnPropertyChanged(nameof(TotalDiscountText));
        OnPropertyChanged(nameof(SubtotalText));
        OnPropertyChanged(nameof(HasTotalDiscount));
        OnPropertyChanged(nameof(IsPaymentDeferredOrPartial));
        OnPropertyChanged(nameof(CurrentPaymentModeText));
        OnPropertyChanged(nameof(CanPostNow));
    }

    /// <summary>Save stays locked while a post is in flight, so a double-tap cannot save twice.</summary>
    public bool CanPostNow => CanPost && !Busy;

    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanPostNow));

    partial void OnQuantityChanged(decimal value)
    {
        ValidateDiscount();
        // Quantity is the "to add" input — it never changes the cart, so Recalculate()
        // (a full cart pass) used to run here for no effect. Only the preview text needs it.
        OnPropertyChanged(nameof(CurrentLineTotalText));
    }

    partial void OnSubtotalChanged(decimal value) => OnPropertyChanged(nameof(TotalText));

    partial void OnTotalChanged(decimal value) => OnPropertyChanged(nameof(TotalText));

    partial void OnAmountDueChanged(decimal value) => OnPropertyChanged(nameof(AmountDueText));

    /// <summary>Two decimals, the way every other figure in the app is written.</summary>
    public string TotalText => DashboardViewModel.Money(Total);

    /// <summary>What the customer still owes, to two decimals.</summary>
    public string AmountDueText => DashboardViewModel.Money(AmountDue);

    private void OnSettlementChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettlementPanel.Choice)
            or nameof(SettlementPanel.PartialAmount))
        {
            Recalculate();
        }
    }

    public string SelectedCustomerBalanceText => SelectedCustomer?.BalanceDisplay ?? string.Empty;

    /// <summary>Drives the balance label colour: red when they owe us, green when in credit.</summary>
    public bool IsSelectedCustomerInDebt => SelectedCustomer is { Balance: > 0m };
    public bool IsSelectedCustomerInCredit => SelectedCustomer is { Balance: < 0m };

    partial void OnSelectedCustomerChanged(PartyOption? value)
    {
        // The customer decides whether a sale can be saved at all, so picking one has to
        // re-evaluate the save button just as adding a line does.
        OnPropertyChanged(nameof(SelectedCustomerBalanceText));
        OnPropertyChanged(nameof(IsSelectedCustomerInDebt));
        OnPropertyChanged(nameof(IsSelectedCustomerInCredit));
        Recalculate();
    }

    /// <summary>
    /// Posts the sale. Stock is drawn oldest-first and the receivable is written; whatever the
    /// settlement panel says is then taken in the same breath, because that is how a till works.
    /// </summary>
    [RelayCommand]
    private async Task PostAsync()
    {
        if (!CanPost || SelectedCustomer is null)
            return;

        Busy = true;
        Status = "جاري الحفظ…";

        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            // One transaction for the header, the lines, the posting and the voucher. The posting
            // service used to commit on its own, so a failure between the sale and its payment
            // left a posted sale with a full receivable and nothing in the till.
            await using TransactionScope tx = await TransactionScope.BeginAsync(ctx, default);
            try
            {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var listId = await DefaultPriceListAsync(ctx);

            var sale = new Sale
            {
                Type = SaleType.Invoice,
                PartyId = SelectedCustomer.Id,
                LocationId = _locationId,
                PriceListId = listId ?? await FirstPriceListIdAsync(ctx),
                DocDate = today,
                Notes = "بيع من شاشة البيع",
            };
            ctx.Sales.Add(sale);
            await ctx.SaveChangesAsync();

            // Two batched lookups replace the old per-line FirstAsync pair (2 round trips
            // per cart line → 2 total, and the change tracker is not bloated with repeats).
            var productIds = Cart.Select(l => l.ProductId).Distinct().ToArray();
            var unitIds = Cart.Select(l => l.ProductUnitId).Distinct().ToArray();

            var productsById = await ctx.Products
                .Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id);
            var unitsById = await ctx.ProductUnits
                .Where(u => unitIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id);

            foreach (var line in Cart)
            {
                // Last line of defence: a line priced at zero would post as a real sale worth
                // nothing. The cart already refuses unpriced units, so reaching here means the
                // price was changed underneath us — refuse rather than give the goods away.
                if (line.UnitPrice <= 0m)
                    throw new DomainException(
                        $"'{line.Name}' سعره صفر — اتأكد من قائمة الأسعار قبل الحفظ.");

                sale.AddItem(productsById[line.ProductId], unitsById[line.ProductUnitId],
                    line.Quantity, line.UnitPrice, line.UnitPrice, discountAmount: line.Discount);
            }

            sale.DiscountAmount = Rounding.Amount(InvoiceDiscount);
            sale.Recalculate();

            await ctx.SaveChangesAsync();

            // The sale always makes the customer owe us; the voucher is what reduces that.
            var result = await new SalePostingService(ctx).PostAsync(sale.Id, userId: 1, today);

            var settled = await Settlement.TryApplyAsync(
                ctx,
                PaymentDirection.In,
                sale.PartyId,
                saleId: sale.Id,
                purchaseId: null,
                total: sale.Total,
                today,
                userId: 1);

            if (!settled && Settlement.Settlement != SettlementChoice.Deferred)
            {
                // The cashier chose to settle but no voucher could be written. Rolling back keeps
                // the receipt and the cash in agreement; reporting success here would post the
                // invoice while the money went nowhere.
                await tx.RollbackAsync(default);
                Status = $"لم يتم حفظ الفاتورة: {Settlement.Problem}";
                return;
            }

            await tx.CommitAsync(default);

            Status = $"تم حفظ {result.DocNo} — الإجمالي {DashboardViewModel.Money(sale.Total)}";
            Cart.Clear();
            _cartByUnitId.Clear();
            Settlement.Reset();
            InvoiceDiscount = 0m;
            InvoiceDiscountText = string.Empty;
            InvoiceDiscountError = string.Empty;
            Recalculate();
            await LoadAsync();
            SelectedCustomer = Customers.FirstOrDefault(c => c.Name.Contains("نقدي")) ?? Customers.FirstOrDefault();
            }
            catch
            {
                await tx.RollbackAsync(default);
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
        }
    }

    private static async Task<int> DefaultLocationAsync(DaftarContext ctx)
    {
        var setting = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == Setting.Keys.DefaultLocationId);
        if (setting is not null && int.TryParse(setting.Value, out var id))
            return id;

        return await ctx.StockLocations
            .Where(l => l.IsActive)
            .OrderBy(l => l.Type == StockLocationType.Warehouse ? 0 : 1)
            .Select(l => l.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>Sales cannot be saved without a price list, so this falls back to the first one.</summary>
    private static async Task<int> FirstPriceListIdAsync(DaftarContext ctx)
        => await ctx.PriceLists.Select(p => p.Id).FirstOrDefaultAsync();

    private static async Task<int?> DefaultPriceListAsync(DaftarContext ctx)
    {
        var setting = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == Setting.Keys.DefaultPriceListId);
        if (setting is not null && int.TryParse(setting.Value, out var id))
            return id;

        return await ctx.PriceLists.Where(p => p.IsDefault).Select(p => (int?)p.Id).FirstOrDefaultAsync();
    }
}

public sealed record PartyOption(
    int Id,
    string Name,
    CustomerType? Type,
    decimal Balance = 0m,
    bool IsCustomer = true,
    bool IsSupplier = false)
{
    public string Display => Type is null ? Name : $"{Name} ({TypeLabel(Type)})";

    public string BalanceDisplay => Balance switch
    {
        > 0 => $"مديونية: {Balance:N2} ج.م",
        < 0 => $"له رصيد: {-Balance:N2} ج.م",
        _ => "خالص (0.00)"
    };

    private static string TypeLabel(CustomerType? type) => type switch
    {
        CustomerType.Supermarket => "سوبر ماركت",
        CustomerType.Restaurant => "مطعم",
        CustomerType.Wholesale => "جملة",
        CustomerType.Retail => "تجزئة",
        CustomerType.Individual => "أفراد",
        _ => "أخرى",
    };
}

public sealed partial class ProductOption : ObservableObject
{
    public int    ProductId  { get; }
    public string Sku        { get; }
    public string Name       { get; }

    // All active units — populated once in LoadProductsAsync.
    public ObservableCollection<CartUnitOption> AvailableUnits { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProductUnitId), nameof(UnitName), nameof(UnitFactor),
                              nameof(Price), nameof(PurchasePrice), nameof(OnHand),
                              nameof(Display), nameof(PriceText), nameof(FullPriceText),
                              nameof(PurchasePriceText), nameof(OnHandText),
                              nameof(HasStock), nameof(IsLowStock))]
    private CartUnitOption? _selectedUnit;

    // Forwarded from SelectedUnit with safe fallbacks
    public int     ProductUnitId  => SelectedUnit?.ProductUnitId  ?? 0;
    public string  UnitName       => SelectedUnit?.UnitName       ?? string.Empty;
    public decimal UnitFactor     => SelectedUnit?.UnitFactor     ?? 1m;
    public decimal Price          => SelectedUnit?.Price          ?? 0m;
    public decimal PurchasePrice  => SelectedUnit?.PurchasePrice  ?? 0m;
    public decimal OnHand         => SelectedUnit?.OnHand         ?? 0m;

    public string Display         => $"{Name} ({UnitName}) — {Sku}";
    public string PriceText       => Price % 1 == 0 ? $"{Price:N0} ج" : $"{Price:N2} ج";
    public string FullPriceText   => $"{Price:N2} ج.م";
    public string PurchasePriceText => $"{PurchasePrice:N2} ج.م";
    public string OnHandText      => $"متاح: {OnHand:N0}";

    /// <summary>Tile stays tappable only while there is something to sell.</summary>
    public bool HasStock    => OnHand > 0m;

    /// <summary>Warn the cashier before the shelf surprises them at the till.</summary>
    public bool IsLowStock  => OnHand > 0m && OnHand <= 5m;

    public bool HasMultipleUnits => AvailableUnits.Count > 1;

    public ProductOption(int productId, string sku, string name)
    {
        ProductId = productId;
        Sku       = sku;
        Name      = name;
    }

    public override string ToString() => Display;
}

public sealed record CartUnitOption(
    int ProductUnitId,
    string UnitName,
    decimal UnitFactor,
    decimal Price,
    decimal PurchasePrice,
    decimal OnHand)
{
    /// <summary>
    /// False when no price could be resolved for this unit. A zero price used to mean "free",
    /// which let a genuinely missing price list entry post a real sale at zero revenue. Such a
    /// unit is shown but cannot be added to the cart.
    /// </summary>
    public bool IsPriced => Price > 0m;

    public override string ToString() => UnitName;
}

public partial class CartLine : ObservableObject
{
    public int ProductId { get; init; }

    [ObservableProperty]
    private int _productUnitId;

    public string Sku { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    [ObservableProperty]
    private string _unitName = string.Empty;

    [ObservableProperty]
    private decimal _purchasePrice;

    public ObservableCollection<CartUnitOption> AvailableUnits { get; } = [];

    [ObservableProperty]
    private CartUnitOption? _selectedUnit;

    partial void OnSelectedUnitChanged(CartUnitOption? value)
    {
        if (value is null || value.ProductUnitId == ProductUnitId) return;

        ProductUnitId = value.ProductUnitId;
        UnitName = value.UnitName;
        UnitPrice = value.Price;
        PurchasePrice = value.PurchasePrice;

        var unitDiscount = Quantity > 0m ? Discount / Quantity : 0m;
        if (unitDiscount > 0m)
        {
            Discount = Domain.Shared.Rounding.Amount(unitDiscount * Quantity);
        }
        LineTotal = Domain.Shared.Rounding.LineTotal(Quantity, UnitPrice, Discount);
        OnPropertyChanged(nameof(UnitPriceText));
        OnPropertyChanged(nameof(PriceByQuantityText));
        OnPropertyChanged(nameof(LineTotalText));
        OnQuantityUpdated?.Invoke();
    }

    public Action? OnQuantityUpdated { get; set; }

    [ObservableProperty]
    private decimal _quantity;

    [ObservableProperty]
    private string _quantityInput = "1";

    [ObservableProperty]
    private decimal _unitPrice;

    [ObservableProperty]
    private decimal _discount;

    [ObservableProperty]
    private decimal _lineTotal;

    partial void OnQuantityChanged(decimal value)
    {
        var text = Domain.Shared.Rounding.QuantityText(value);
        if (_quantityInput != text)
        {
            _quantityInput = text;
            OnPropertyChanged(nameof(QuantityInput));
        }
        OnPropertyChanged(nameof(QuantityText));
        OnPropertyChanged(nameof(PriceByQuantityText));
        OnPropertyChanged(nameof(HasDiscount));
        OnPropertyChanged(nameof(DiscountSummaryText));
    }

    partial void OnQuantityInputChanged(string value)
    {
        if (decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out var parsed) ||
            decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out parsed))
        {
            if (parsed > 0m && parsed != _quantity)
            {
                var unitDiscount = _quantity > 0m ? Discount / _quantity : 0m;
                _quantity = parsed;
                if (unitDiscount > 0m)
                {
                    Discount = Domain.Shared.Rounding.Amount(unitDiscount * _quantity);
                }
                LineTotal = Domain.Shared.Rounding.LineTotal(_quantity, UnitPrice, Discount);
                OnPropertyChanged(nameof(Quantity));
                OnPropertyChanged(nameof(QuantityText));
                OnPropertyChanged(nameof(PriceByQuantityText));
                OnPropertyChanged(nameof(HasDiscount));
                OnPropertyChanged(nameof(DiscountSummaryText));
                OnQuantityUpdated?.Invoke();
            }
        }
    }

    partial void OnUnitPriceChanged(decimal value)
    {
        OnPropertyChanged(nameof(UnitPriceText));
        OnPropertyChanged(nameof(PriceByQuantityText));
    }

    partial void OnDiscountChanged(decimal value)
    {
        OnPropertyChanged(nameof(DiscountText));
        OnPropertyChanged(nameof(HasDiscount));
        OnPropertyChanged(nameof(DiscountSummaryText));
    }

    partial void OnLineTotalChanged(decimal value) => OnPropertyChanged(nameof(LineTotalText));

    public string QuantityText => Domain.Shared.Rounding.QuantityText(Quantity);

    public string UnitPriceText => DashboardViewModel.Money(UnitPrice);

    public string PriceByQuantityText => $"{UnitPrice:N2} × {QuantityText}";

    public string DiscountText => Discount > 0m ? DashboardViewModel.Money(Discount) : "-";

    public bool HasDiscount => Discount > 0m;

    public string DiscountSummaryText => Discount > 0m ? $"-{DashboardViewModel.Money(Discount)}" : string.Empty;

    public string LineTotalText => DashboardViewModel.Money(LineTotal);
}
