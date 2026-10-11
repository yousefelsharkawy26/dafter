using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Domain.Finance;
using Daftar.Domain.Sales;
using Daftar.Domain.Security;
using Daftar.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Daftar.Desktop.Diagnostics;

namespace Daftar.ViewModels;

public sealed record ReportSaleRow(
    int Id,
    string DocNo,
    string DocDate,
    string PartyName,
    decimal Total,
    string TotalText,
    decimal Cost,
    string CostText,
    decimal Margin,
    string MarginText,
    string MarginPercentText,
    string MarginColor);

public sealed record ReportSaleItemRow(
    string ProductName,
    string UnitName,
    decimal Quantity,
    string QuantityText,
    decimal UnitPrice,
    string UnitPriceText,
    decimal Total,
    string TotalText,
    decimal Cost,
    string CostText,
    decimal Margin,
    string MarginText);

public sealed record TopProductRow(
    int ProductId,
    string ProductName,
    decimal QuantitySold,
    string QuantityText,
    decimal Revenue,
    string RevenueText,
    decimal Cost,
    string CostText,
    decimal Profit,
    string ProfitText,
    string MarginPercentText,
    string ProfitColor);

public sealed record OverdueRow(
    string PartyName,
    string DocNo,
    string DueDateText,
    decimal Amount,
    string AmountText,
    int DaysOverdue,
    string SeverityBadge,
    string SeverityColor);

public sealed record CashFlowRow(
    int Id,
    string DateText,
    string KindText,
    string AccountName,
    string PartyName,
    decimal Amount,
    string AmountText,
    string AmountColor,
    bool IsInflow);

public partial class ReportsViewModel(IServiceProvider services) : ObservableObject
{
    private readonly IServiceProvider _services = services;

    // ── Date Filters ──────────────────────────────────────────────────────────
    [ObservableProperty] private string _fromDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-30)).ToString("yyyy-MM-dd");
    [ObservableProperty] private string _toDate = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
    [ObservableProperty] private int _activePresetIndex = 4; // 0=Today, 1=Yesterday, 2=ThisWeek, 3=ThisMonth, 4=Last30Days, 5=ThisYear, -1=Custom

    // ── Navigation & Tab State ────────────────────────────────────────────────
    [ObservableProperty] private int _selectedTabIndex; // 0=Sales, 1=TopProducts, 2=Overdue, 3=CashFlow

    // ── Search & In-Report Filters ─────────────────────────────────────────────
    [ObservableProperty] private string _searchSalesText = string.Empty;
    [ObservableProperty] private string _searchProductsText = string.Empty;
    [ObservableProperty] private string _searchOverdueText = string.Empty;
    [ObservableProperty] private string _searchCashFlowText = string.Empty;
    [ObservableProperty] private int _overdueAgingFilter; // 0=All, 1=>30 days, 2=>60 days, 3=>90 days

    // ── Status & Progress ─────────────────────────────────────────────────────
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    // ── KPI Summary Values ────────────────────────────────────────────────────
    [ObservableProperty] private string _totalSalesText = "0.00 ج.م";
    [ObservableProperty] private string _totalCostText = "0.00 ج.م";
    [ObservableProperty] private string _totalMarginText = "0.00 ج.م";
    [ObservableProperty] private string _marginPercentText = "0.0%";
    [ObservableProperty] private string _collectionsText = "0.00 ج.م";
    [ObservableProperty] private string _collectionRatioText = "0.0%";
    [ObservableProperty] private string _invoicesCountText = "0 فاتورة";
    [ObservableProperty] private string _overdueText = "0.00 ج.م";
    [ObservableProperty] private string _overdueCountText = "0 متأخرة";

    // ── Collections for UI Binding ────────────────────────────────────────────
    public ObservableCollection<ReportSaleRow> SaleRows { get; } = [];
    public ObservableCollection<ReportSaleRow> FilteredSaleRows { get; } = [];
    private readonly List<ReportSaleRow> _allSaleRows = [];

    public ObservableCollection<TopProductRow> FilteredTopProducts { get; } = [];
    private readonly List<TopProductRow> _allTopProducts = [];

    public ObservableCollection<OverdueRow> OverdueRows { get; } = [];
    public ObservableCollection<OverdueRow> FilteredOverdueRows { get; } = [];
    private readonly List<OverdueRow> _allOverdueRows = [];

    public ObservableCollection<CashFlowRow> FilteredCashFlowRows { get; } = [];
    private readonly List<CashFlowRow> _allCashFlowRows = [];

    // ── Selected Invoice Drilldown ────────────────────────────────────────────
    [ObservableProperty] private ReportSaleRow? _selectedSale;
    [ObservableProperty] private bool _isSaleDetailsOpen;
    public ObservableCollection<ReportSaleItemRow> SelectedSaleItems { get; } = [];

    /// <summary>
    /// Whether the signed-in role may see what the goods cost. Cost, margin and profit are the
    /// owner's numbers: a cashier who can read them knows the price to beat and the margin to
    /// squeeze. Without Reports.Cost the reports still show revenue, and nothing that reveals cost.
    ///
    /// This is a screen-level control, not a boundary. The database is a local file the operator
    /// owns, so what it stops is a cashier opening the reports screen, not a determined user with
    /// a file manager.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KpiCardCount))]
    private bool _canViewCost;

    /// <summary>
    /// How many cards the summary strip holds.
    ///
    /// The strip is a UniformGrid with a fixed column count, so hiding the cost and margin cards
    /// without reducing it leaves an empty fifth of the bar -- two cards' worth of blank space
    /// where numbers should be. Reducing the count makes what is left fill the width.
    /// </summary>
    public int KpiCardCount => CanViewCost ? 5 : 3;

    private const string Hidden = "—";

    /// <summary>Formats a cost, margin or profit figure, or hides it.</summary>
    private string Money(decimal value) => CanViewCost ? $"{value:N2} ج.م" : Hidden;

    /// <summary>Zeroes a figure so the value itself never reaches a row that lacks permission to see it.</summary>
    private decimal Cost(decimal value) => CanViewCost ? value : 0m;

    // ── Computed Tab Booleans ─────────────────────────────────────────────────
    public bool IsSalesTabActive => SelectedTabIndex == 0;
    public bool IsProductsTabActive => SelectedTabIndex == 1;
    public bool IsOverdueTabActive => SelectedTabIndex == 2;
    public bool IsCashFlowTabActive => SelectedTabIndex == 3;

    public bool IsPresetTodayActive => ActivePresetIndex == 0;
    public bool IsPresetYesterdayActive => ActivePresetIndex == 1;
    public bool IsPresetThisWeekActive => ActivePresetIndex == 2;
    public bool IsPresetThisMonthActive => ActivePresetIndex == 3;
    public bool IsPresetLast30DaysActive => ActivePresetIndex == 4;
    public bool IsPresetThisYearActive => ActivePresetIndex == 5;

    public ReportsViewModel() : this(App.Services!) { }

    [RelayCommand]
    public async Task InitializeAsync() => await LoadAsync();

    // ── Preset Commands ───────────────────────────────────────────────────────
    [RelayCommand]
    public void SetPresetToday()
    {
        ActivePresetIndex = 0;
        var today = DateOnly.FromDateTime(DateTime.Today);
        FromDate = today.ToString("yyyy-MM-dd");
        ToDate = today.ToString("yyyy-MM-dd");
        _ = LoadAsync();
    }

    [RelayCommand]
    public void SetPresetYesterday()
    {
        ActivePresetIndex = 1;
        var yest = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
        FromDate = yest.ToString("yyyy-MM-dd");
        ToDate = yest.ToString("yyyy-MM-dd");
        _ = LoadAsync();
    }

    [RelayCommand]
    public void SetPresetThisWeek()
    {
        ActivePresetIndex = 2;
        var today = DateTime.Today;
        var diff = (7 + (today.DayOfWeek - DayOfWeek.Saturday)) % 7;
        var startOfWeek = DateOnly.FromDateTime(today.AddDays(-diff));
        FromDate = startOfWeek.ToString("yyyy-MM-dd");
        ToDate = DateOnly.FromDateTime(today).ToString("yyyy-MM-dd");
        _ = LoadAsync();
    }

    [RelayCommand]
    public void SetPresetThisMonth()
    {
        ActivePresetIndex = 3;
        var today = DateTime.Today;
        var startOfMonth = new DateOnly(today.Year, today.Month, 1);
        FromDate = startOfMonth.ToString("yyyy-MM-dd");
        ToDate = DateOnly.FromDateTime(today).ToString("yyyy-MM-dd");
        _ = LoadAsync();
    }

    [RelayCommand]
    public void SetPresetLast30Days()
    {
        ActivePresetIndex = 4;
        FromDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-30)).ToString("yyyy-MM-dd");
        ToDate = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
        _ = LoadAsync();
    }

    [RelayCommand]
    public void SetPresetThisYear()
    {
        ActivePresetIndex = 5;
        var today = DateTime.Today;
        var startOfYear = new DateOnly(today.Year, 1, 1);
        FromDate = startOfYear.ToString("yyyy-MM-dd");
        ToDate = DateOnly.FromDateTime(today).ToString("yyyy-MM-dd");
        _ = LoadAsync();
    }

    // ── Tab Navigation Commands ───────────────────────────────────────────────
    [RelayCommand] public void SelectSalesTab() => SelectedTabIndex = 0;
    [RelayCommand] public void SelectProductsTab() => SelectedTabIndex = 1;
    [RelayCommand] public void SelectOverdueTab() => SelectedTabIndex = 2;
    [RelayCommand] public void SelectCashFlowTab() => SelectedTabIndex = 3;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSalesTabActive));
        OnPropertyChanged(nameof(IsProductsTabActive));
        OnPropertyChanged(nameof(IsOverdueTabActive));
        OnPropertyChanged(nameof(IsCashFlowTabActive));
    }

    partial void OnActivePresetIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPresetTodayActive));
        OnPropertyChanged(nameof(IsPresetYesterdayActive));
        OnPropertyChanged(nameof(IsPresetThisWeekActive));
        OnPropertyChanged(nameof(IsPresetThisMonthActive));
        OnPropertyChanged(nameof(IsPresetLast30DaysActive));
        OnPropertyChanged(nameof(IsPresetThisYearActive));
    }

    // ── Search & Filter Change Handlers ───────────────────────────────────────
    partial void OnSearchSalesTextChanged(string value) => ApplySalesFilter();
    partial void OnSearchProductsTextChanged(string value) => ApplyProductsFilter();
    partial void OnSearchOverdueTextChanged(string value) => ApplyOverdueFilter();
    partial void OnOverdueAgingFilterChanged(int value) => ApplyOverdueFilter();
    partial void OnSearchCashFlowTextChanged(string value) => ApplyCashFlowFilter();

    [RelayCommand]
    public void SetOverdueAging(object? param)
    {
        if (param is int i)
            OverdueAgingFilter = i;
        else if (param is string s && int.TryParse(s, out var parsed))
            OverdueAgingFilter = parsed;
    }

    // ── Drilldown for Sale Invoice Items ──────────────────────────────────────
    [RelayCommand]
    public async Task OpenSaleDetailsAsync(object? param = null)
    {
        var target = param as ReportSaleRow ?? SelectedSale;
        if (target is null) return;

        SelectedSale = target;
        await LoadSaleDetailsAsync(target.Id);
        IsSaleDetailsOpen = true;
    }

    [RelayCommand]
    public void CloseSaleDetails()
    {
        IsSaleDetailsOpen = false;
    }

    private async Task LoadSaleDetailsAsync(int saleId)
    {
        SelectedSaleItems.Clear();
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var items = await db.SaleItems
                .AsNoTracking()
                .Include(i => i.Product)
                .Where(i => i.SaleId == saleId)
                .OrderBy(i => i.LineNo)
                .ToListAsync();

            foreach (var i in items)
            {
                var margin = i.LineTotal - i.CostTotal;
                SelectedSaleItems.Add(new ReportSaleItemRow(
                    i.Product?.Name ?? $"صنف #{i.ProductId}",
                    string.IsNullOrEmpty(i.UnitName) ? "قطعة" : i.UnitName,
                    i.Quantity,
                    $"{i.Quantity:G29}",
                    i.UnitPrice,
                    $"{i.UnitPrice:N2} ج.م",
                    i.LineTotal,
                    $"{i.LineTotal:N2} ج.م",
                    Cost(i.CostTotal),
                    Money(i.CostTotal),
                    Cost(margin),
                    Money(margin)));
            }
        }
        catch
        {
            // Ignore drilldown load errors
        }
    }

    // ── Core Data Loading ─────────────────────────────────────────────────────
    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusMessage = "جاري تجميع البيانات والتحليلات...";
        try
        {
            var from = DateOnly.TryParse(FromDate, out var f) ? f : DateOnly.FromDateTime(DateTime.Today.AddDays(-30));
            var to = DateOnly.TryParse(ToDate, out var t) ? t : DateOnly.FromDateTime(DateTime.Today);

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            // Resolved per load, not cached for the lifetime of the view model: a role can be
            // edited while the window is open, and the next refresh should reflect that.
            CanViewCost = _services.GetService<IUserAuthorizer>() is { } authorizer
                && await authorizer.CanAsync(Role.Codes.ReportsCost);

            // 1. Sales in range
            var sales = await db.Sales
                .AsNoTracking()
                .Include(s => s.Party)
                .Include(s => s.Items)
                .Where(s => s.Status == Domain.Shared.DocumentStatus.Posted
                         && s.Type == SaleType.Invoice
                         && s.DocDate >= from && s.DocDate <= to)
                .OrderByDescending(s => s.DocDate)
                .ThenByDescending(s => s.Id)
                .ToListAsync();

            _allSaleRows.Clear();
            SaleRows.Clear();

            decimal totalSales = 0m;
            decimal totalCost = 0m;

            foreach (var s in sales)
            {
                var cost = s.Items.Sum(i => i.CostTotal);
                var margin = s.Total - cost;
                var marginPct = s.Total > 0 ? (margin / s.Total) * 100m : 0m;
                var color = margin >= 0 ? "#107C41" : "#D13438";

                var row = new ReportSaleRow(
                    s.Id,
                    s.DocNo ?? $"INV-{s.Id}",
                    s.DocDate.ToString("yyyy-MM-dd"),
                    s.Party?.Name ?? "عميل نقدي",
                    s.Total,
                    $"{s.Total:N2} ج.م",
                    Cost(cost),
                    Money(cost),
                    Cost(margin),
                    Money(margin),
                    CanViewCost ? $"{marginPct:N1}%" : Hidden,
                    CanViewCost ? color : "#9E9E9E");

                _allSaleRows.Add(row);
                SaleRows.Add(row);
                totalSales += s.Total;
                totalCost += CanViewCost ? cost : 0m;
            }

            var totalMargin = totalSales - totalCost;
            var overallMarginPct = totalSales > 0 ? (totalMargin / totalSales) * 100m : 0m;

            TotalSalesText = $"{totalSales:N2} ج.م";
            TotalCostText = Money(totalCost);
            TotalMarginText = Money(totalMargin);
            MarginPercentText = CanViewCost ? $"{overallMarginPct:N1}% هامش" : "مخفي";
            InvoicesCountText = $"{sales.Count} فاتورة";

            // 2. Collections in range (PaymentIn)
            var collections = await db.PartyAccountTransactions
                .AsNoTracking()
                .Where(x => x.Kind == PartyLedgerKind.PaymentIn
                         && x.BusinessDate >= from && x.BusinessDate <= to)
                .SumAsync(x => (decimal?)x.Amount) ?? 0m;

            var absCollections = Math.Abs(collections);
            CollectionsText = $"{absCollections:N2} ج.م";
            var collRatio = totalSales > 0 ? (absCollections / totalSales) * 100m : 0m;
            CollectionRatioText = $"{collRatio:N1}% من المبيعات";

            // 3. Top Products & Profitability in range
            var saleItemsInRange = await db.SaleItems
                .AsNoTracking()
                .Include(i => i.Product)
                .Where(i => i.Sale != null
                         && i.Sale.Status == Domain.Shared.DocumentStatus.Posted
                         && i.Sale.Type == SaleType.Invoice
                         && i.Sale.DocDate >= from && i.Sale.DocDate <= to)
                .ToListAsync();

            var groupedProducts = saleItemsInRange
                .GroupBy(i => new { i.ProductId, Name = i.Product != null ? i.Product.Name : $"صنف #{i.ProductId}" })
                .Select(g =>
                {
                    var qty = g.Sum(x => x.Quantity);
                    var rev = g.Sum(x => x.LineTotal);
                    var cost = g.Sum(x => x.CostTotal);
                    var profit = rev - cost;
                    var pct = rev > 0 ? (profit / rev) * 100m : 0m;
                    return new
                    {
                        g.Key.ProductId,
                        g.Key.Name,
                        Qty = qty,
                        Revenue = rev,
                        Cost = cost,
                        Profit = profit,
                        MarginPct = pct
                    };
                })
                .OrderByDescending(x => x.Revenue)
                .ToList();

            _allTopProducts.Clear();
            foreach (var p in groupedProducts)
            {
                _allTopProducts.Add(new TopProductRow(
                    p.ProductId,
                    p.Name,
                    p.Qty,
                    $"{p.Qty:G29}",
                    p.Revenue,
                    $"{p.Revenue:N2} ج.م",
                    Cost(p.Cost),
                    Money(p.Cost),
                    Cost(p.Profit),
                    Money(p.Profit),
                    CanViewCost ? $"{p.MarginPct:N1}%" : Hidden,
                    CanViewCost ? (p.Profit >= 0 ? "#107C41" : "#D13438") : "#9E9E9E"));
            }

            // 4. Overdue Receivables
            var today = DateOnly.FromDateTime(DateTime.Today);
            var overdueSales = await db.Sales
                .AsNoTracking()
                .Include(s => s.Party)
                .Where(s => s.Status == Domain.Shared.DocumentStatus.Posted
                         && s.Type == SaleType.Invoice
                         && s.DueDate.HasValue && s.DueDate < today)
                .OrderBy(s => s.DueDate)
                .ToListAsync();

            var partyIds = overdueSales.Select(s => s.PartyId).Distinct().ToList();
            var balances = await db.PartyAccountTransactions
                .AsNoTracking()
                .Where(x => partyIds.Contains(x.PartyId))
                .GroupBy(x => x.PartyId)
                .Select(g => new { PartyId = g.Key, Balance = g.Sum(x => x.Amount) })
                .ToListAsync();

            _allOverdueRows.Clear();
            OverdueRows.Clear();

            foreach (var s in overdueSales)
            {
                var days = today.DayNumber - s.DueDate!.Value.DayNumber;
                var (badge, color) = days switch
                {
                    > 90 => ("تأخير حرج (> 90)", "#D13438"),
                    > 60 => ("تأخير مرتفع (> 60)", "#C19C00"),
                    > 30 => ("تأخير متوسط (> 30)", "#E36A00"),
                    _ => ("متأخر حديثاً", "#5A5A5A")
                };

                var row = new OverdueRow(
                    s.Party?.Name ?? "عميل",
                    s.DocNo ?? $"INV-{s.Id}",
                    s.DueDate.Value.ToString("yyyy-MM-dd"),
                    s.Total,
                    $"{s.Total:N2} ج.م",
                    days,
                    badge,
                    color);

                _allOverdueRows.Add(row);
                OverdueRows.Add(row);
            }

            var totalOverdue = balances.Sum(b => b.Balance > 0 ? b.Balance : 0m);
            OverdueText = $"{totalOverdue:N2} ج.م";
            OverdueCountText = $"{overdueSales.Count} فواتير متأخرة";

            // 5. Cash Flow & Treasury Movements
            var cashMovements = await db.CashTransactions
                .AsNoTracking()
                .Include(c => c.CashAccount)
                .Include(c => c.Payment)
                    .ThenInclude(p => p != null ? p.Party : null)
                .Where(c => c.BusinessDate >= from && c.BusinessDate <= to)
                .OrderByDescending(c => c.OccurredAt)
                .Take(150)
                .ToListAsync();

            _allCashFlowRows.Clear();
            foreach (var c in cashMovements)
            {
                var isInflow = c.Amount >= 0;
                var kindText = c.Kind switch
                {
                    CashTransactionKind.Collection => "تحصيل من عميل 📥",
                    CashTransactionKind.SupplierPayment => "سداد لمورد 📤",
                    CashTransactionKind.Expense => $"مصروف تشغيلي ({c.Category?.ToString() ?? "عام"}) 💸",
                    CashTransactionKind.TransferIn => "تحويل وارد 🔄",
                    CashTransactionKind.TransferOut => "تحويل صادر 🔄",
                    CashTransactionKind.Deposit => "إيداع مباشر 💵",
                    CashTransactionKind.Withdrawal => "سحب نقدي 💵",
                    CashTransactionKind.Opening => "رصيد افتتاحي 🏁",
                    _ => c.Kind.ToString()
                };

                var partyName = c.Payment?.Party?.Name ?? "-";
                _allCashFlowRows.Add(new CashFlowRow(
                    c.Id,
                    c.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    kindText,
                    c.CashAccount?.Name ?? "خزينة",
                    partyName,
                    Math.Abs(c.Amount),
                    $"{(isInflow ? "+" : "-")}{Math.Abs(c.Amount):N2} ج.م",
                    isInflow ? "#107C41" : "#D13438",
                    isInflow));
            }

            // Apply filters to all sub-views
            ApplySalesFilter();
            ApplyProductsFilter();
            ApplyOverdueFilter();
            ApplyCashFlowFilter();

            StatusMessage = $"تم تحديث التقرير بنجاح ({from:yyyy-MM-dd} إلى {to:yyyy-MM-dd})";
        }
        catch (Exception ex)
        {
            _services.Report("Reports.LoadAsync", ex);
            StatusMessage = $"خطأ: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Filter Implementations ────────────────────────────────────────────────
    private void ApplySalesFilter()
    {
        FilteredSaleRows.Clear();
        var q = SearchSalesText.Trim().ToLowerInvariant();

        foreach (var r in _allSaleRows)
        {
            if (!string.IsNullOrEmpty(q))
            {
                var match = r.DocNo.ToLowerInvariant().Contains(q)
                         || r.PartyName.ToLowerInvariant().Contains(q)
                         || r.DocDate.Contains(q);
                if (!match) continue;
            }
            FilteredSaleRows.Add(r);
        }
    }

    private void ApplyProductsFilter()
    {
        FilteredTopProducts.Clear();
        var q = SearchProductsText.Trim().ToLowerInvariant();

        foreach (var r in _allTopProducts)
        {
            if (!string.IsNullOrEmpty(q))
            {
                var match = r.ProductName.ToLowerInvariant().Contains(q);
                if (!match) continue;
            }
            FilteredTopProducts.Add(r);
        }
    }

    private void ApplyOverdueFilter()
    {
        FilteredOverdueRows.Clear();
        var q = SearchOverdueText.Trim().ToLowerInvariant();

        foreach (var r in _allOverdueRows)
        {
            if (OverdueAgingFilter == 1 && r.DaysOverdue <= 30) continue;
            if (OverdueAgingFilter == 2 && r.DaysOverdue <= 60) continue;
            if (OverdueAgingFilter == 3 && r.DaysOverdue <= 90) continue;

            if (!string.IsNullOrEmpty(q))
            {
                var match = r.PartyName.ToLowerInvariant().Contains(q)
                         || r.DocNo.ToLowerInvariant().Contains(q)
                         || r.DueDateText.Contains(q);
                if (!match) continue;
            }
            FilteredOverdueRows.Add(r);
        }
    }

    private void ApplyCashFlowFilter()
    {
        FilteredCashFlowRows.Clear();
        var q = SearchCashFlowText.Trim().ToLowerInvariant();

        foreach (var r in _allCashFlowRows)
        {
            if (!string.IsNullOrEmpty(q))
            {
                var match = r.PartyName.ToLowerInvariant().Contains(q)
                         || r.AccountName.ToLowerInvariant().Contains(q)
                         || r.KindText.ToLowerInvariant().Contains(q)
                         || r.DateText.Contains(q);
                if (!match) continue;
            }
            FilteredCashFlowRows.Add(r);
        }
    }
}
