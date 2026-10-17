using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Domain.Sales;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Daftar.Desktop.Diagnostics;

namespace Daftar.ViewModels;

public sealed record ReturnRow(
    int SaleId,
    string DocNo,
    string DocDate,
    string PartyName,
    string OriginalDocNo,
    decimal Total,
    string TotalText,
    string Status);

public sealed record ReturnDetailItemRow(
    string ProductName,
    string UnitName,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    string LineTotalText);

public sealed record ReturnDetailsInfo(
    int SaleId,
    string DocNo,
    string DocDate,
    string PartyName,
    string OriginalDocNo,
    decimal Total,
    string TotalText,
    string Status,
    string? Notes,
    IReadOnlyList<ReturnDetailItemRow> Items);

public sealed record OriginalInvoiceCandidate(
    int SaleId,
    string DocNo,
    string DocDate,
    string PartyName,
    decimal Total,
    string TotalText);

public partial class ReturnItemCandidateViewModel : ObservableObject
{
    public int OriginalItemId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string UnitName { get; init; } = string.Empty;
    public decimal OriginalQuantity { get; init; }
    public decimal AlreadyReturnedQuantity { get; init; }
    public decimal MaxReturnableQuantity { get; init; }
    public decimal UnitPrice { get; init; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private decimal _returnQuantity;

    public decimal LineRefundAmount => Rounding.Amount(ReturnQuantity * UnitPrice);
    public string LineRefundAmountText => $"{LineRefundAmount:N2} ج.م";

    public Action? OnQuantityChanged { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && ReturnQuantity <= 0 && MaxReturnableQuantity > 0)
            ReturnQuantity = MaxReturnableQuantity;
        else if (!value)
            ReturnQuantity = 0;

        OnPropertyChanged(nameof(LineRefundAmount));
        OnPropertyChanged(nameof(LineRefundAmountText));
        OnQuantityChanged?.Invoke();
    }

    partial void OnReturnQuantityChanged(decimal value)
    {
        if (value > MaxReturnableQuantity)
            ReturnQuantity = MaxReturnableQuantity;
        else if (value < 0)
            ReturnQuantity = 0;

        IsSelected = ReturnQuantity > 0;
        OnPropertyChanged(nameof(LineRefundAmount));
        OnPropertyChanged(nameof(LineRefundAmountText));
        OnQuantityChanged?.Invoke();
    }

    [RelayCommand]
    public void IncrementQuantity()
    {
        if (ReturnQuantity < MaxReturnableQuantity)
            ReturnQuantity += 1m;
    }

    [RelayCommand]
    public void DecrementQuantity()
    {
        if (ReturnQuantity > 0m)
            ReturnQuantity -= 1m;
    }

    [RelayCommand]
    public void SetMaxQuantity()
    {
        ReturnQuantity = MaxReturnableQuantity;
        IsSelected = ReturnQuantity > 0m;
    }
}

public partial class ReturnsViewModel(IServiceProvider services) : ObservableObject
{
    private readonly IServiceProvider _services = services;

    // --- Search & History State ---
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _filterIndex; // 0: الكل, 1: اليوم, 2: هذا الشهر
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    // --- KPIs ---
    [ObservableProperty] private int _totalReturnsCount;
    [ObservableProperty] private decimal _totalReturnsAmount;
    [ObservableProperty] private int _todayReturnsCount;
    [ObservableProperty] private decimal _todayReturnsAmount;

    // --- Return Details Viewer ---
    [ObservableProperty] private ReturnRow? _selectedReturnRow;
    [ObservableProperty] private ReturnDetailsInfo? _selectedReturnDetails;
    [ObservableProperty] private bool _isDetailsPanelOpen;

    // --- Create Return Wizard ---
    [ObservableProperty] private bool _isCreatingReturn;
    [ObservableProperty] private string _invoiceSearchQuery = string.Empty;
    [ObservableProperty] private OriginalInvoiceCandidate? _selectedInvoice;
    [ObservableProperty] private decimal _totalRefundAmount;
    [ObservableProperty] private string _returnNotes = string.Empty;
    [ObservableProperty] private string _createStatusMessage = string.Empty;
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private bool _canConfirmReturn;

    // Backward-compatibility property
    public string OriginalInvoiceQuery
    {
        get => InvoiceSearchQuery;
        set => InvoiceSearchQuery = value;
    }

    public ObservableCollection<ReturnRow> Rows { get; } = [];
    public ObservableCollection<ReturnRow> FilteredRows { get; } = [];
    public ObservableCollection<OriginalInvoiceCandidate> CandidateInvoices { get; } = [];
    public ObservableCollection<ReturnItemCandidateViewModel> ReturnCandidateLines { get; } = [];

    public ReturnsViewModel() : this(App.Services!) { }

    [RelayCommand]
    public async Task InitializeAsync() => await LoadAsync();

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnFilterIndexChanged(int value) => ApplyFilter();

    [RelayCommand]
    public void SetFilter(int index) => FilterIndex = index;

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusMessage = "جاري التحميل...";
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var today = DateOnly.FromDateTime(DateTime.Today);

            var query = db.Sales
                .AsNoTracking()
                .Include(s => s.Party)
                .Include(s => s.OriginalSale)
                .Where(s => s.Type == SaleType.Return);

            var rows = await query
                .OrderByDescending(s => s.DocDate)
                .ThenByDescending(s => s.Id)
                .Take(200)
                .ToListAsync();

            Rows.Clear();
            decimal totalAll = 0m;
            int countToday = 0;
            decimal totalToday = 0m;

            foreach (var r in rows)
            {
                var docDateStr = r.DocDate.ToString("yyyy-MM-dd");
                var total = r.Total;
                totalAll += total;

                if (r.DocDate == today)
                {
                    countToday++;
                    totalToday += total;
                }

                Rows.Add(new ReturnRow(
                    r.Id,
                    r.DocNo ?? "(مسودة)",
                    docDateStr,
                    r.Party?.Name ?? "-",
                    r.OriginalSale?.DocNo ?? "-",
                    total,
                    $"{total:N2} ج.م",
                    r.Status == DocumentStatus.Posted ? "مرحّل" :
                    r.Status == DocumentStatus.Cancelled ? "ملغى" : "مسودة"));
            }

            TotalReturnsCount = Rows.Count;
            TotalReturnsAmount = totalAll;
            TodayReturnsCount = countToday;
            TodayReturnsAmount = totalToday;

            ApplyFilter();
            StatusMessage = $"إجمالي المرتجعات المسجلة: {Rows.Count}";
        }
        catch (Exception ex)
        {
            _services.Report("Returns.LoadAsync", ex);
            StatusMessage = $"خطأ: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        var query = Rows.AsEnumerable();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var firstDayOfMonth = new DateOnly(today.Year, today.Month, 1);

        if (FilterIndex == 1) // اليوم
        {
            var todayStr = today.ToString("yyyy-MM-dd");
            query = query.Where(r => r.DocDate == todayStr);
        }
        else if (FilterIndex == 2) // هذا الشهر
        {
            query = query.Where(r => DateOnly.TryParse(r.DocDate, out var d) && d >= firstDayOfMonth);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            query = query.Where(r =>
                r.DocNo.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                r.OriginalDocNo.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                r.PartyName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        FilteredRows.Clear();
        foreach (var row in query)
            FilteredRows.Add(row);
    }

    // --- Return Details Actions ---

    [RelayCommand]
    public async Task SelectReturnRowAsync(ReturnRow? row)
    {
        if (row is null)
            return;

        SelectedReturnRow = row;
        IsDetailsPanelOpen = true;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var sale = await db.Sales
            .AsNoTracking()
            .Include(s => s.Party)
            .Include(s => s.OriginalSale)
            .Include(s => s.Items)
            .ThenInclude(i => i.Product)
            .Include(s => s.Items)
            .ThenInclude(i => i.ProductUnit)
            .FirstOrDefaultAsync(s => s.Id == row.SaleId);

        if (sale is null)
            return;

        var items = sale.Items.Select(item => new ReturnDetailItemRow(
            item.Product?.Name ?? item.UnitName,
            item.UnitName,
            item.Quantity,
            item.UnitPrice,
            item.LineTotal,
            $"{item.LineTotal:N2} ج.م"
        )).ToList();

        SelectedReturnDetails = new ReturnDetailsInfo(
            sale.Id,
            sale.DocNo ?? row.DocNo,
            sale.DocDate.ToString("yyyy-MM-dd"),
            sale.Party?.Name ?? "-",
            sale.OriginalSale?.DocNo ?? "-",
            sale.Total,
            $"{sale.Total:N2} ج.م",
            row.Status,
            sale.Notes,
            items);
    }

    [RelayCommand]
    public void CloseDetailsPanel()
    {
        IsDetailsPanelOpen = false;
        SelectedReturnRow = null;
        SelectedReturnDetails = null;
    }

    // --- Create Return Flow ---

    [RelayCommand]
    public async Task StartCreateReturnAsync()
    {
        IsCreatingReturn = true;
        IsDetailsPanelOpen = false;
        InvoiceSearchQuery = string.Empty;
        SelectedInvoice = null;
        ReturnCandidateLines.Clear();
        TotalRefundAmount = 0m;
        ReturnNotes = string.Empty;
        CreateStatusMessage = "اختر أو ابحث عن الفاتورة الأصلية";
        CanConfirmReturn = false;

        await SearchInvoicesAsync();
    }

    [RelayCommand]
    public void CancelCreateReturn()
    {
        IsCreatingReturn = false;
        SelectedInvoice = null;
        ReturnCandidateLines.Clear();
        CreateStatusMessage = string.Empty;
    }

    [RelayCommand]
    public async Task SearchInvoicesAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var search = InvoiceSearchQuery.Trim();

            var query = db.Sales
                .AsNoTracking()
                .Include(s => s.Party)
                .Where(s => s.Type == SaleType.Invoice && s.Status == DocumentStatus.Posted);

            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(s =>
                    (s.DocNo != null && s.DocNo.Contains(search)) ||
                    (s.Party != null && (s.Party.Name.Contains(search) ||
                                         (s.Party.Phone != null && s.Party.Phone.Contains(search)) ||
                                         (s.Party.Code != null && s.Party.Code.Contains(search)))));
            }

            var candidates = await query
                .OrderByDescending(s => s.DocDate)
                .ThenByDescending(s => s.Id)
                .Take(15)
                .ToListAsync();

            CandidateInvoices.Clear();
            foreach (var c in candidates)
            {
                CandidateInvoices.Add(new OriginalInvoiceCandidate(
                    c.Id,
                    c.DocNo ?? $"SO-{c.Id}",
                    c.DocDate.ToString("yyyy-MM-dd"),
                    c.Party?.Name ?? "-",
                    c.Total,
                    $"{c.Total:N2} ج.م"));
            }

            if (CandidateInvoices.Count == 0)
                CreateStatusMessage = "لم يتم العثور على فواتير مطابقة للبحث";
            else
                CreateStatusMessage = $"تم العثور على {ArabicText.Count(CandidateInvoices.Count, "فاتورة", "فاتورتان", "فواتير")}";
        }
        catch (Exception ex)
        {
            _services.Report("Returns.SearchInvoicesAsync", ex);
            CreateStatusMessage = $"خطأ: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task SelectInvoiceAsync(OriginalInvoiceCandidate? candidate)
    {
        if (candidate is null)
            return;

        SelectedInvoice = candidate;
        CreateStatusMessage = $"جاري تحميل بنود الفاتورة {candidate.DocNo}...";

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var original = await db.Sales
                .AsNoTracking()
                .Include(s => s.Items)
                .ThenInclude(i => i.Lots)
                .Include(s => s.Items)
                .ThenInclude(i => i.Product)
                .Include(s => s.Items)
                .ThenInclude(i => i.ProductUnit)
                .FirstOrDefaultAsync(s => s.Id == candidate.SaleId);

            if (original is null)
            {
                CreateStatusMessage = "تعذر تحميل الفاتورة المحددة";
                return;
            }

            // Calculate already returned quantities per item
            var returnedLines = await db.SaleItemLots
                .Where(l => l.SaleItem!.Sale!.OriginalSaleId == candidate.SaleId &&
                            l.SaleItem.Sale!.Status == DocumentStatus.Posted)
                .Select(l => new { OriginalSaleItemId = l.SaleItem!.OriginalSaleItemId, l.BaseQuantity })
                .ToListAsync();

            var alreadyReturnedByItem = returnedLines
                .Where(r => r.OriginalSaleItemId.HasValue)
                .GroupBy(r => r.OriginalSaleItemId.GetValueOrDefault())
                .ToDictionary(g => g.Key, g => Rounding.Quantity(g.Sum(x => x.BaseQuantity)));

            ReturnCandidateLines.Clear();

            foreach (var item in original.Items)
            {
                var factor = item.UnitFactor > 0m ? item.UnitFactor : 1m;
                var baseReturned = alreadyReturnedByItem.GetValueOrDefault(item.Id);
                var unitReturned = Rounding.Quantity(baseReturned / factor);
                var maxReturnable = Math.Max(0m, item.Quantity - unitReturned);

                var vmLine = new ReturnItemCandidateViewModel
                {
                    OriginalItemId = item.Id,
                    ProductName = item.Product?.Name ?? item.UnitName,
                    UnitName = item.UnitName,
                    OriginalQuantity = item.Quantity,
                    AlreadyReturnedQuantity = unitReturned,
                    MaxReturnableQuantity = maxReturnable,
                    UnitPrice = item.UnitPrice,
                    ReturnQuantity = maxReturnable,
                    IsSelected = maxReturnable > 0m,
                    OnQuantityChanged = RecalculateRefundTotal
                };

                ReturnCandidateLines.Add(vmLine);
            }

            RecalculateRefundTotal();
            CreateStatusMessage = $"تم تحميل {ArabicText.Count(ReturnCandidateLines.Count, "بند", "بندان", "بنود")} من الفاتورة {candidate.DocNo}";
        }
        catch (Exception ex)
        {
            _services.Report("Returns.SelectInvoiceAsync", ex);
            CreateStatusMessage = $"خطأ: {ex.Message}";
        }
    }

    public void RecalculateRefundTotal()
    {
        TotalRefundAmount = ReturnCandidateLines
            .Where(l => l.IsSelected && l.ReturnQuantity > 0m)
            .Sum(l => l.LineRefundAmount);

        CanConfirmReturn = TotalRefundAmount > 0m && SelectedInvoice != null;
    }

    [RelayCommand]
    public void SelectAllLines()
    {
        foreach (var line in ReturnCandidateLines)
            line.SetMaxQuantity();
        RecalculateRefundTotal();
    }

    [RelayCommand]
    public void DeselectAllLines()
    {
        foreach (var line in ReturnCandidateLines)
            line.IsSelected = false;
        RecalculateRefundTotal();
    }

    [RelayCommand]
    public async Task ConfirmAndPostReturnAsync()
    {
        if (SelectedInvoice is null)
        {
            CreateStatusMessage = "يرجى اختيار الفاتورة الأصلية أولاً";
            return;
        }

        var linesToReturn = ReturnCandidateLines
            .Where(l => l.IsSelected && l.ReturnQuantity > 0m)
            .Select(l => new ReturnItemRequest(l.OriginalItemId, l.ReturnQuantity))
            .ToList();

        if (linesToReturn.Count == 0)
        {
            CreateStatusMessage = "يرجى تحديد بند واحد على الأقل بكمية أكبر من صفر للإرجاع";
            return;
        }

        IsCreating = true;
        CreateStatusMessage = "جاري ترحيل المرتجع وتحديث المخزون وحساب العميل...";

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var returnService = new SaleReturnService(db);
            var today = DateOnly.FromDateTime(DateTime.Today);

            var notes = string.IsNullOrWhiteSpace(ReturnNotes)
                ? $"مرتجع للفاتورة {SelectedInvoice.DocNo}"
                : ReturnNotes.Trim();

            var draft = await returnService.BeginAsync(
                SelectedInvoice.SaleId,
                today,
                userId: 1,
                requestedItems: linesToReturn,
                notes: notes);

            await returnService.PostAsync(draft.SaleId, userId: 1);

            var posted = await db.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.Id == draft.SaleId);
            var returnDocNo = posted?.DocNo ?? draft.SaleId.ToString();

            CreateStatusMessage = $"تم إنشاء وترحيل المرتجع '{returnDocNo}' بنجاح ✓";
            IsCreatingReturn = false;

            await LoadAsync();

            var newRow = Rows.FirstOrDefault(r => r.SaleId == draft.SaleId);
            if (newRow is not null)
                await SelectReturnRowAsync(newRow);
        }
        catch (DomainException dex)
        {
            CreateStatusMessage = $"تنبيه: {dex.Message}";
        }
        catch (Exception ex)
        {
            _services.Report("Returns.ConfirmAndPostReturnAsync", ex);
            CreateStatusMessage = $"خطأ: {ex.Message}";
        }
        finally
        {
            IsCreating = false;
        }
    }

    // Backward-compatibility command for tests/callers
    [RelayCommand]
    public async Task CreateReturnAsync()
    {
        var invoiceNo = OriginalInvoiceQuery.Trim();
        if (string.IsNullOrWhiteSpace(invoiceNo))
        {
            CreateStatusMessage = "يرجى إدخال رقم الفاتورة الأصلية";
            return;
        }

        IsCreating = true;
        CreateStatusMessage = "جاري البحث وإنشاء المرتجع...";
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var original = await db.Sales
                .AsNoTracking()
                .Include(s => s.Party)
                .Where(s => s.Type == SaleType.Invoice && s.Status == DocumentStatus.Posted)
                .Where(s => (s.DocNo != null && s.DocNo == invoiceNo) ||
                            (s.Party != null && (s.Party.Name == invoiceNo || s.Party.Phone == invoiceNo || (s.Party.Code != null && s.Party.Code == invoiceNo))))
                .OrderByDescending(s => s.DocDate)
                .ThenByDescending(s => s.Id)
                .FirstOrDefaultAsync();

            if (original is null)
            {
                CreateStatusMessage = $"لم يتم العثور على فاتورة مرحّلة برقم أو طرف '{invoiceNo}'";
                return;
            }

            var returnService = new SaleReturnService(db);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var draftResult = await returnService.BeginAsync(original.Id, today, userId: 1);

            await returnService.PostAsync(draftResult.SaleId, userId: 1);

            var postedReturn = await db.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.Id == draftResult.SaleId);
            CreateStatusMessage = $"تم إنشاء وترحيل المرتجع بنجاح برقم '{postedReturn?.DocNo ?? draftResult.SaleId.ToString()}' ✓";
            OriginalInvoiceQuery = string.Empty;

            await LoadAsync();
        }
        catch (DomainException dex)
        {
            CreateStatusMessage = $"تنبيه: {dex.Message}";
        }
        catch (Exception ex)
        {
            _services.Report("Returns.CreateReturnAsync", ex);
            CreateStatusMessage = $"خطأ: {ex.Message}";
        }
        finally
        {
            IsCreating = false;
        }
    }
}
