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
using Daftar.Domain.Sales;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.ViewModels;

public partial class SellViewModel(IServiceProvider services) : ObservableObject
{
    private readonly IServiceProvider _services = services;

    private int _locationId;

    public ObservableCollection<PartyOption> Customers { get; } = [];

    public ObservableCollection<ProductOption> Products { get; } = [];

    public ObservableCollection<CartLine> Cart { get; } = [];

    [ObservableProperty] private PartyOption? _selectedCustomer;
    [ObservableProperty] private ProductOption? _selectedProduct;
    [ObservableProperty] private decimal _quantity = 1m;
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private string _status = "اختار العميل والمنتج";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _canPost;
    /// <summary>
    /// How this sale is settled at the counter. Replaces a free-text box, which could not say how
    /// the money was taken or refuse an amount larger than the sale.
    /// </summary>
    public SettlementPanel Settlement { get; } = new(services);

    /// <summary>Amount still owed after any payment taken at the till.</summary>
    [ObservableProperty] private decimal _amountDue;

    [ObservableProperty]
    private decimal _subtotal;

    [ObservableProperty]
    private decimal _total;

    public async Task LoadAsync()
    {
        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        Customers.Clear();
        foreach (var party in await ctx.Parties
                     .Where(p => p.IsCustomer && p.Status == PartyStatus.Active)
                     .OrderBy(p => p.Name)
                     .ToListAsync())
        {
            Customers.Add(new PartyOption(party.Id, party.Name, party.CustomerType));
        }

        _locationId = await DefaultLocationAsync(ctx);
        var priceListId = await DefaultPriceListAsync(ctx);

        await LoadProductsAsync(ctx, priceListId);
        // The panel raises its own changes; without this the amount due would freeze the
        // moment the cart changed and never follow what is typed into it.
        Settlement.PropertyChanged += OnSettlementChanged;

        await Settlement.LoadAsync();
        Status = "جاهز للبيع";
    }

    private async Task LoadProductsAsync(DaftarContext ctx, int? priceListId)
    {
        Products.Clear();

        var today = DateOnly.FromDateTime(DateTime.Now);
        var resolver = new PriceResolutionService(ctx);

        // Units are read below to pick the selling unit. There are no lazy-loading proxies,
        // so without this the navigation collection is empty and every product is skipped —
        // which left the till with an empty product list and no error to explain it.
        var active = await ctx.Products
            .Include(p => p.Units)
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();

        foreach (var product in active)
        {
            var unit = product.Units.FirstOrDefault(u => u.IsDefaultSale)
                       ?? product.BaseUnit
                       ?? product.Units.FirstOrDefault();
            if (unit is null)
                continue;

            decimal price;
            try
            {
                price = (await resolver.ResolveAsync(priceListId, unit.Id, today)).Price;
            }
            catch (DomainException)
            {
                // Not priced yet — still list it so the problem is visible, rather than hiding it.
                price = 0m;
            }

            var onHand = await OnHandAsync(ctx, product.Id, unit);
            Products.Add(new ProductOption(product.Id, unit.Id, product.Sku, product.Name, unit.Name, price, onHand));
        }
    }

    /// <summary>Stock on hand across sellable batches at the selling location.</summary>
    private static async Task<decimal> OnHandAsync(DaftarContext ctx, int productId, ProductUnit unit)
    {
        var balances = await ctx.StockBalances
            .Include(b => b.Lot)
            .Where(b => b.Lot!.ProductId == productId)
            .ToListAsync();

        return Rounding.Quantity(balances
            .Where(b => b.Lot!.IsAvailable())
            .Sum(b => b.Quantity / unit.Factor));
    }

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

        var existing = Cart.FirstOrDefault(l => l.ProductUnitId == SelectedProduct.ProductUnitId);
        if (existing is not null)
        {
            existing.Quantity += Quantity;
            existing.LineTotal = Rounding.LineTotal(existing.Quantity, existing.UnitPrice, 0m);
        }
        else
        {
            Cart.Add(new CartLine
            {
                ProductId = SelectedProduct.ProductId,
                ProductUnitId = SelectedProduct.ProductUnitId,
                Sku = SelectedProduct.Sku,
                Name = SelectedProduct.Name,
                UnitName = SelectedProduct.UnitName,
                UnitPrice = SelectedProduct.Price,
                Quantity = Quantity,
                LineTotal = Rounding.LineTotal(Quantity, SelectedProduct.Price, 0m),
            });
        }

        Recalculate();
        Status = "تمت الإضافة";
    }

    [RelayCommand]
    private void RemoveLine(CartLine? line)
    {
        if (line is null)
            return;

        Cart.Remove(line);
        Recalculate();
    }

    [RelayCommand]
    private void Clear()
    {
        Cart.Clear();
        Settlement.Reset();
        Recalculate();
        Status = "تم تفريغ السلة";
    }

    private void Recalculate()
    {
        Subtotal = Rounding.Amount(Cart.Sum(l => l.LineTotal));
        Total = Subtotal;

        // What the customer still owes after whatever is being handed over now.
        AmountDue = Rounding.Amount(Total - Settlement.AmountFor(Total));

        CanPost = Cart.Count > 0 && SelectedCustomer is not null && Total > 0m;
    }

    partial void OnQuantityChanged(decimal value) => Recalculate();

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

    partial void OnSelectedCustomerChanged(PartyOption? value)
    {
        // The customer decides whether a sale can be saved at all, so picking one has to
        // re-evaluate the save button just as adding a line does.
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

            foreach (var line in Cart)
            {
                var product = await ctx.Products.FirstAsync(p => p.Id == line.ProductId);
                var unit = await ctx.ProductUnits.FirstAsync(u => u.Id == line.ProductUnitId);
                sale.AddItem(product, unit, line.Quantity, line.UnitPrice, line.UnitPrice);
            }

            await ctx.SaveChangesAsync();

            // The sale always makes the customer owe us; the voucher is what reduces that.
            var result = await new SalePostingService(ctx).PostAsync(sale.Id, userId: 1, today);

            await Settlement.ApplyAsync(
                ctx,
                PaymentDirection.In,
                sale.PartyId,
                saleId: sale.Id,
                purchaseId: null,
                total: sale.Total,
                today,
                userId: 1);

            Status = $"تم حفظ {result.DocNo} — الإجمالي {DashboardViewModel.Money(sale.Total)}";
            Cart.Clear();
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

public sealed record PartyOption(int Id, string Name, CustomerType? Type)
{
    public string Display => Type is null ? Name : $"{Name} ({TypeLabel(Type)})";

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

public sealed record ProductOption(
    int ProductId, int ProductUnitId, string Sku, string Name, string UnitName, decimal Price, decimal OnHand)
{
    public string Display => $"{Name} — {Sku}";

    // Avalonia 12's AutoCompleteBox has no BoxItemTemplate, so the CHOSEN item falls back to
    // ToString(). A record's generated ToString() dumps every field, so override it.
    public override string ToString() => Display;
}

public partial class CartLine : ObservableObject
{
    public int ProductId { get; init; }
    public int ProductUnitId { get; init; }
    public string Sku { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string UnitName { get; init; } = string.Empty;

    [ObservableProperty]
    private decimal _quantity;

    [ObservableProperty]
    private decimal _unitPrice;

    [ObservableProperty]
    private decimal _lineTotal;

    partial void OnQuantityChanged(decimal value) => OnPropertyChanged(nameof(QuantityText));

    partial void OnUnitPriceChanged(decimal value) => OnPropertyChanged(nameof(UnitPriceText));

    partial void OnLineTotalChanged(decimal value) => OnPropertyChanged(nameof(LineTotalText));

    public string QuantityText => Domain.Shared.Rounding.QuantityText(Quantity);

    public string UnitPriceText => DashboardViewModel.Money(UnitPrice);

    public string LineTotalText => DashboardViewModel.Money(LineTotal);
}