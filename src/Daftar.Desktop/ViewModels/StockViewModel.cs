using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Domain.Inventory;
using Daftar.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.ViewModels;

public sealed record StockRow(
    int LotId,
    int ProductId,
    int LocationId,
    string ProductName,
    string Sku,
    string LocationName,
    string LocationType,
    decimal Quantity,
    string QuantityDisplay,
    string BaseUnit,
    decimal UnitCost,
    string UnitCostText,
    decimal TotalValue,
    string TotalValueText,
    string? SupplierName,
    string ReceivedOn,
    bool IsLow,
    bool IsBlocked)
{
    public string LowStockLabel => IsBlocked ? "محظور" : IsLow ? "⚠ تحت الحد" : "متوفر";
    public bool IsVehicle => LocationType == "سيارة";
    public bool IsAvailable => !IsBlocked && !IsLow;
}

public sealed record MovementRow(
    int MovementId,
    int ProductId,
    int LotId,
    DateTime OccurredAt,
    string OccurredAtText,
    string ProductName,
    string Sku,
    string LocationName,
    string LocationType,
    InventoryMovementKind Kind,
    string KindLabel,
    decimal Quantity,
    string QuantityText,
    string QuantityColor,
    bool IsInbound,
    string BaseUnit,
    string DocumentNo,
    string? Notes)
{
    public bool IsVehicle => LocationType == "سيارة";
    public bool IsSuccessKind => Kind is InventoryMovementKind.Receipt or InventoryMovementKind.SaleReturn || (Kind == InventoryMovementKind.CountAdjust && Quantity >= 0);
    public bool IsDangerKind => Kind is InventoryMovementKind.Sale or InventoryMovementKind.Scrap || (Kind == InventoryMovementKind.CountAdjust && Quantity < 0);
    public bool IsWarningKind => Kind is InventoryMovementKind.PurchaseReturn or InventoryMovementKind.TransferOut;
    public bool IsAccentKind => Kind is InventoryMovementKind.TransferIn or InventoryMovementKind.Opening;
}

public sealed record TransferLocationItem(int Id, string Name, string TypeText)
{
    public string DisplayName => Name == "Main Warehouse" ? "المستودع الرئيسي" : Name;
    public string DisplayText => $"{DisplayName} ({TypeText})";
}

public sealed record TransferLotItem(int LotId, int ProductId, string ProductName, string Sku, decimal AvailableQuantity, string UnitName)
{
    public string DisplayText => $"{ProductName} ({Sku}) — متاح: {AvailableQuantity:N3} {UnitName}";
}

public partial class StockViewModel(IServiceProvider services) : ObservableObject
{
    private readonly IServiceProvider _services = services;
    private readonly List<StockRow> _allRows = [];
    private readonly List<MovementRow> _allMovements = [];

    public ObservableCollection<StockRow> FilteredRows { get; } = [];
    public ObservableCollection<MovementRow> FilteredMovements { get; } = [];
    public ObservableCollection<TransferLocationItem> Locations { get; } = [];
    public ObservableCollection<TransferLotItem> SourceLots { get; } = [];

    // Tab Navigation: 0 = أرصدة المخزون, 1 = تتبع حركة المخزون
    [ObservableProperty]
    private int _activeTab;

    public bool IsBalancesTabActive => ActiveTab == 0;
    public bool IsTrackingTabActive => ActiveTab == 1;

    // Smart Stock KPI Metrics
    [ObservableProperty]
    private decimal _totalStockValue;

    [ObservableProperty]
    private string _totalStockValueText = "0.00 ج.م";

    [ObservableProperty]
    private int _totalProductsCount;

    [ObservableProperty]
    private int _totalLotsCount;

    [ObservableProperty]
    private int _lowStockCount;

    [ObservableProperty]
    private bool _hasLowStock;

    [ObservableProperty]
    private decimal _vansStockValue;

    [ObservableProperty]
    private string _vansStockValueText = "0.00 ج.م";

    [ObservableProperty]
    private string _totalValueText = "0.00";

    [ObservableProperty]
    private int _rowCount;

    [ObservableProperty]
    private int _movementsCount;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    // Filters for Balances Tab
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>0=الكل, 1=مخازن, 2=سيارات</summary>
    [ObservableProperty]
    private int _locationFilter;

    /// <summary>0=الكل, 1=تحت الحد فقط</summary>
    [ObservableProperty]
    private int _statusFilter;

    public bool IsLocationFilterAll => LocationFilter == 0;
    public bool IsLocationFilterWarehouse => LocationFilter == 1;
    public bool IsLocationFilterVans => LocationFilter == 2;
    public bool IsStatusFilterAll => StatusFilter == 0;
    public bool IsStatusFilterLow => StatusFilter == 1;

    // Filters for Tracking Tab
    [ObservableProperty]
    private string _trackingSearchText = string.Empty;

    /// <summary>0=الكل, 1=مبيعات, 2=مشتريات, 3=تحويلات, 4=تسويات وهالك</summary>
    [ObservableProperty]
    private int _trackingKindFilter;

    /// <summary>0=الكل, 1=مخازن, 2=سيارات</summary>
    [ObservableProperty]
    private int _trackingLocationFilter;

    public bool IsTrackingKindAll => TrackingKindFilter == 0;
    public bool IsTrackingKindSales => TrackingKindFilter == 1;
    public bool IsTrackingKindPurchases => TrackingKindFilter == 2;
    public bool IsTrackingKindTransfers => TrackingKindFilter == 3;
    public bool IsTrackingKindAdjustments => TrackingKindFilter == 4;

    public bool IsTrackingLocationAll => TrackingLocationFilter == 0;
    public bool IsTrackingLocationWarehouse => TrackingLocationFilter == 1;
    public bool IsTrackingLocationVans => TrackingLocationFilter == 2;

    // Stock Transfer properties (Floating Modal Dialog)
    [ObservableProperty]
    private bool _isTransferPanelOpen;

    [ObservableProperty]
    private TransferLocationItem? _selectedSourceLocation;

    [ObservableProperty]
    private TransferLocationItem? _selectedDestinationLocation;

    [ObservableProperty]
    private TransferLotItem? _selectedSourceLot;

    [ObservableProperty]
    private decimal _transferQuantity = 1m;

    [ObservableProperty]
    private string _transferNotes = string.Empty;

    [ObservableProperty]
    private string _transferError = string.Empty;

    [ObservableProperty]
    private string _transferSuccess = string.Empty;

    [ObservableProperty]
    private bool _isTransferring;

    public StockViewModel() : this(App.Services!) { }

    [RelayCommand]
    public async Task InitializeAsync() => await LoadAsync();

    // Tab Navigation Commands
    [RelayCommand]
    public void ShowBalancesTab()
    {
        ActiveTab = 0;
        NotifyTabProperties();
    }

    [RelayCommand]
    public void ShowTrackingTab()
    {
        ActiveTab = 1;
        NotifyTabProperties();
    }

    private void NotifyTabProperties()
    {
        OnPropertyChanged(nameof(IsBalancesTabActive));
        OnPropertyChanged(nameof(IsTrackingTabActive));
    }

    // Balances Filter Change Handlers
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnLocationFilterChanged(int value)
    {
        OnPropertyChanged(nameof(IsLocationFilterAll));
        OnPropertyChanged(nameof(IsLocationFilterWarehouse));
        OnPropertyChanged(nameof(IsLocationFilterVans));
        ApplyFilter();
    }
    partial void OnStatusFilterChanged(int value)
    {
        OnPropertyChanged(nameof(IsStatusFilterAll));
        OnPropertyChanged(nameof(IsStatusFilterLow));
        ApplyFilter();
    }

    // Balances Filter Commands
    [RelayCommand]
    public void FilterLocationsAll() => LocationFilter = 0;

    [RelayCommand]
    public void FilterLocationsWarehouse() => LocationFilter = 1;

    [RelayCommand]
    public void FilterLocationsVans() => LocationFilter = 2;

    [RelayCommand]
    public void FilterStatusAll() => StatusFilter = 0;

    [RelayCommand]
    public void FilterStatusLow() => StatusFilter = 1;

    [RelayCommand]
    public void SetLocationFilter(object? f)
    {
        if (f is int i) LocationFilter = i;
        else if (f is string s && int.TryParse(s, out var parsed)) LocationFilter = parsed;
    }

    [RelayCommand]
    public void SetStatusFilter(object? f)
    {
        if (f is int i) StatusFilter = i;
        else if (f is string s && int.TryParse(s, out var parsed)) StatusFilter = parsed;
    }

    [RelayCommand]
    public void ToggleLowStockFilter()
    {
        StatusFilter = StatusFilter == 1 ? 0 : 1;
        ShowBalancesTab();
    }

    // Tracking Filter Change Handlers
    partial void OnTrackingSearchTextChanged(string value) => ApplyTrackingFilter();
    partial void OnTrackingKindFilterChanged(int value)
    {
        OnPropertyChanged(nameof(IsTrackingKindAll));
        OnPropertyChanged(nameof(IsTrackingKindSales));
        OnPropertyChanged(nameof(IsTrackingKindPurchases));
        OnPropertyChanged(nameof(IsTrackingKindTransfers));
        OnPropertyChanged(nameof(IsTrackingKindAdjustments));
        ApplyTrackingFilter();
    }
    partial void OnTrackingLocationFilterChanged(int value)
    {
        OnPropertyChanged(nameof(IsTrackingLocationAll));
        OnPropertyChanged(nameof(IsTrackingLocationWarehouse));
        OnPropertyChanged(nameof(IsTrackingLocationVans));
        ApplyTrackingFilter();
    }

    // Tracking Filter Commands
    [RelayCommand]
    public void FilterTrackingKindAll() => TrackingKindFilter = 0;

    [RelayCommand]
    public void FilterTrackingKindSales() => TrackingKindFilter = 1;

    [RelayCommand]
    public void FilterTrackingKindPurchases() => TrackingKindFilter = 2;

    [RelayCommand]
    public void FilterTrackingKindTransfers() => TrackingKindFilter = 3;

    [RelayCommand]
    public void FilterTrackingKindAdjustments() => TrackingKindFilter = 4;

    [RelayCommand]
    public void FilterTrackingLocationAll() => TrackingLocationFilter = 0;

    [RelayCommand]
    public void FilterTrackingLocationWarehouse() => TrackingLocationFilter = 1;

    [RelayCommand]
    public void FilterTrackingLocationVans() => TrackingLocationFilter = 2;

    [RelayCommand]
    public void SetTrackingKindFilter(object? f)
    {
        if (f is int i) TrackingKindFilter = i;
        else if (f is string s && int.TryParse(s, out var parsed)) TrackingKindFilter = parsed;
    }

    [RelayCommand]
    public void SetTrackingLocationFilter(object? f)
    {
        if (f is int i) TrackingLocationFilter = i;
        else if (f is string s && int.TryParse(s, out var parsed)) TrackingLocationFilter = parsed;
    }

    [RelayCommand]
    public void ResetTrackingFilters()
    {
        TrackingSearchText = string.Empty;
        TrackingKindFilter = 0;
        TrackingLocationFilter = 0;
    }

    // Smart Cross-Action Commands
    [RelayCommand]
    public void TrackProductMovements(StockRow row)
    {
        TrackingSearchText = row.Sku;
        ShowTrackingTab();
    }

    /// <summary>
    /// Stock transfer is switched off for now. Nothing was wrong with it; it is parked, not
    /// broken, and the code below is intact for when it comes back.
    /// </summary>
    /// <remarks>
    /// One constant, so turning it on again is a single edit rather than a hunt through the view.
    /// Both entry points read it — the header button and the per-row one — and so do the commands
    /// behind them, so hiding the buttons is not the only thing standing between an operator and
    /// a transfer. Anything bound to <c>IsVisible</c> alone can still be reached from a keyboard
    /// shortcut or a stale binding; this cannot.
    /// </remarks>
    /// <remarks>
    /// Deliberately not a <c>const</c>: with a constant the compiler can see the guard is always
    /// taken, and reports the parked code below as unreachable. Two warnings on every build for a
    /// switch that is meant to be flipped back is the wrong trade.
    /// </remarks>
    public static readonly bool StockTransferEnabled = false;

    public bool IsStockTransferEnabled => StockTransferEnabled;

    [RelayCommand]
    /// <summary>Opens the transfer dialog for one row. Refused while the feature is parked.</summary>
    public void OpenTransferForStock(StockRow row)
    {
        if (!StockTransferEnabled)
            return;

        IsTransferPanelOpen = true;
        TransferError = string.Empty;
        TransferSuccess = string.Empty;

        if (Locations.FirstOrDefault(l => l.Id == row.LocationId) is { } src)
        {
            SelectedSourceLocation = src;
        }

        _ = SelectSourceLotAfterLoadAsync(row.LotId);
    }

    private async Task SelectSourceLotAfterLoadAsync(int lotId)
    {
        if (SelectedSourceLocation is not null)
        {
            await LoadSourceLotsAsync(SelectedSourceLocation);
            if (SourceLots.FirstOrDefault(l => l.LotId == lotId) is { } lot)
            {
                SelectedSourceLot = lot;
                TransferQuantity = Math.Min(1m, lot.AvailableQuantity);
            }
        }
    }

    // Transfer Modal Handlers
    partial void OnSelectedSourceLocationChanged(TransferLocationItem? value)
    {
        _ = LoadSourceLotsAsync(value);
    }

    partial void OnSelectedSourceLotChanged(TransferLotItem? value)
    {
        TransferError = string.Empty;
        if (value is not null && (TransferQuantity <= 0m || TransferQuantity > value.AvailableQuantity))
        {
            TransferQuantity = Math.Min(1m, value.AvailableQuantity);
        }
    }

    [RelayCommand]
    /// <summary>Shows or hides the transfer dialog. Refused while the feature is parked.</summary>
    public void ToggleTransferPanel()
    {
        if (!StockTransferEnabled)
            return;

        IsTransferPanelOpen = !IsTransferPanelOpen;
        TransferError = string.Empty;
        TransferSuccess = string.Empty;
    }

    [RelayCommand]
    public void CloseTransferPanel()
    {
        IsTransferPanelOpen = false;
        TransferError = string.Empty;
        TransferSuccess = string.Empty;
    }

    [RelayCommand]
    public void SetMaxTransferQuantity()
    {
        if (SelectedSourceLot is not null)
        {
            TransferQuantity = SelectedSourceLot.AvailableQuantity;
        }
    }

    public async Task LoadSourceLotsAsync(TransferLocationItem? location)
    {
        SourceLots.Clear();
        SelectedSourceLot = null;
        TransferError = string.Empty;

        if (location is null) return;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var balances = await db.StockBalances
                .AsNoTracking()
                .Include(b => b.Lot)
                    .ThenInclude(l => l!.Product)
                        .ThenInclude(p => p!.Units)
                .Where(b => b.LocationId == location.Id && b.Quantity > 0 && b.Lot!.Status != LotStatus.Blocked)
                .OrderBy(b => b.Lot!.Product!.Name)
                .ToListAsync();

            foreach (var b in balances)
            {
                if (b.Lot?.Product is null) continue;
                var baseUnit = b.Lot.Product.Units.FirstOrDefault(u => u.IsBase)?.Name ?? "وحدة";
                SourceLots.Add(new TransferLotItem(
                    b.LotId,
                    b.Lot.ProductId,
                    b.Lot.Product.Name,
                    b.Lot.Product.Sku,
                    b.Quantity,
                    baseUnit));
            }

            SelectedSourceLot = SourceLots.FirstOrDefault();
            if (SelectedSourceLot is not null)
            {
                TransferQuantity = Math.Min(1m, SelectedSourceLot.AvailableQuantity);
            }
        }
        catch (Exception ex)
        {
            TransferError = $"خطأ في تحميل أصناف الموقع: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ExecuteTransferAsync()
    {
        TransferError = string.Empty;
        TransferSuccess = string.Empty;

        if (SelectedSourceLocation is null || SelectedDestinationLocation is null)
        {
            TransferError = "يرجى تحديد موقع المصدر وموقع الوجهة.";
            return;
        }

        if (SelectedSourceLocation.Id == SelectedDestinationLocation.Id)
        {
            TransferError = "يجب اختيار موقعين مختلفين (لا يمكن التحويل لنفس الموقع).";
            return;
        }

        if (SelectedSourceLot is null)
        {
            TransferError = "يرجى اختيار الصنف المراد تحويله.";
            return;
        }

        if (TransferQuantity <= 0m)
        {
            TransferError = "الكمية المحولة يجب أن تكون أكبر من صفر.";
            return;
        }

        if (TransferQuantity > SelectedSourceLot.AvailableQuantity)
        {
            TransferError = $"الكمية المطلوبة ({TransferQuantity:N3}) أكبر من الرصيد المتاح ({SelectedSourceLot.AvailableQuantity:N3}).";
            return;
        }

        IsTransferring = true;
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var transferService = scope.ServiceProvider.GetRequiredService<Infrastructure.Services.StockTransferPostingService>();

            var lot = await db.Lots.FirstOrDefaultAsync(l => l.Id == SelectedSourceLot.LotId);
            if (lot is null)
            {
                TransferError = "اللوت المحدد غير موجود في قاعدة البيانات.";
                return;
            }

            var transfer = new StockTransfer
            {
                FromLocationId = SelectedSourceLocation.Id,
                ToLocationId = SelectedDestinationLocation.Id,
                DocDate = DateOnly.FromDateTime(DateTime.Today),
                CreatedAt = DateTime.UtcNow,
                CreatedBy = 1,
                Notes = string.IsNullOrWhiteSpace(TransferNotes)
                    ? $"تحويل مخزون من {SelectedSourceLocation.Name} إلى {SelectedDestinationLocation.Name}"
                    : TransferNotes.Trim(),
            };

            transfer.AddItem(lot, TransferQuantity);
            db.StockTransfers.Add(transfer);
            await db.SaveChangesAsync();

            var result = await transferService.PostAsync(transfer.Id, userId: 1);

            TransferSuccess = $"تم ترحيل إذن التحويل بنجاح! رقم الإذن: {result.DocNo}";
            StatusMessage = $"✓ تم تحويل {TransferQuantity:N3} من {lot.ProductId} بنجاح (إذن #{result.DocNo})";

            // Close dialog and refresh data
            IsTransferPanelOpen = false;
            TransferNotes = string.Empty;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            TransferError = $"فشل التحويل: {ex.Message}";
        }
        finally
        {
            IsTransferring = false;
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusMessage = "جاري تحديث أرصدة وتتبع المخزون...";
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            // 1. Locations
            var locs = await db.StockLocations
                .AsNoTracking()
                .Where(l => l.IsActive)
                .OrderBy(l => l.Type == StockLocationType.Warehouse ? 0 : 1)
                .ThenBy(l => l.Name)
                .ToListAsync();

            var prevSourceId = SelectedSourceLocation?.Id;
            var prevDestId = SelectedDestinationLocation?.Id;

            Locations.Clear();
            foreach (var l in locs)
            {
                var typeText = l.Type == StockLocationType.Vehicle ? "سيارة" : "مخزن";
                Locations.Add(new TransferLocationItem(l.Id, l.Name, typeText));
            }

            if (prevSourceId.HasValue && Locations.FirstOrDefault(l => l.Id == prevSourceId.Value) is { } src)
                SelectedSourceLocation = src;
            else
                SelectedSourceLocation = Locations.FirstOrDefault(l => l.TypeText == "مخزن") ?? Locations.FirstOrDefault();

            if (prevDestId.HasValue && Locations.FirstOrDefault(l => l.Id == prevDestId.Value) is { } dst)
                SelectedDestinationLocation = dst;
            else
                SelectedDestinationLocation = Locations.FirstOrDefault(l => l.TypeText == "سيارة") ?? Locations.Skip(1).FirstOrDefault();

            // 2. Stock Balances
            var balances = await db.StockBalances
                .AsNoTracking()
                .Include(b => b.Lot)
                    .ThenInclude(l => l!.Product)
                        .ThenInclude(p => p!.Units)
                .Include(b => b.Lot)
                    .ThenInclude(l => l!.Supplier)
                .Include(b => b.Location)
                .Where(b => b.Quantity > 0)
                .OrderBy(b => b.Lot!.Product!.Name)
                .ToListAsync();

            _allRows.Clear();
            foreach (var b in balances)
            {
                var product = b.Lot?.Product;
                var lot = b.Lot;
                var location = b.Location;
                if (product is null || lot is null || location is null) continue;

                var baseUnit = product.Units.FirstOrDefault(u => u.IsBase);
                var baseUnitName = baseUnit?.Name ?? "وحدة";
                var isLow = product.ReorderLevel.HasValue && b.Quantity <= product.ReorderLevel.Value;
                var locType = location.Type == StockLocationType.Vehicle ? "سيارة" : "مخزن";
                var locName = location.Name == "Main Warehouse" ? "المستودع الرئيسي" : location.Name;

                var totalValue = b.Quantity * lot.UnitCost;

                _allRows.Add(new StockRow(
                    b.LotId,
                    product.Id,
                    b.LocationId,
                    product.Name,
                    product.Sku,
                    locName,
                    locType,
                    b.Quantity,
                    $"{b.Quantity:N3} {baseUnitName}",
                    baseUnitName,
                    lot.UnitCost,
                    $"{lot.UnitCost:N2}",
                    totalValue,
                    $"{totalValue:N2}",
                    lot.Supplier?.Name,
                    lot.ReceivedOn.ToString("yyyy-MM-dd"),
                    isLow,
                    lot.Status == LotStatus.Blocked));
            }

            // 3. Inventory Movements (Stock Tracking Ledger - Most recent 500)
            var movements = await db.InventoryMovements
                .AsNoTracking()
                .Include(m => m.Lot)
                    .ThenInclude(l => l!.Product)
                        .ThenInclude(p => p!.Units)
                .Include(m => m.Location)
                .OrderByDescending(m => m.OccurredAt)
                .Take(500)
                .ToListAsync();

            // Resolve Document Numbers without N+1
            var saleIds = movements.Where(m => m.SourceType == MovementSourceType.Sale && m.SourceId.HasValue).Select(m => m.SourceId!.Value).Distinct().ToList();
            var purchaseIds = movements.Where(m => m.SourceType == MovementSourceType.Purchase && m.SourceId.HasValue).Select(m => m.SourceId!.Value).Distinct().ToList();
            var transferIds = movements.Where(m => m.TransferId.HasValue || (m.SourceType == MovementSourceType.Transfer && m.SourceId.HasValue))
                .Select(m => m.TransferId ?? m.SourceId!.Value).Distinct().ToList();
            var countIds = movements.Where(m => m.StockCountId.HasValue || (m.SourceType == MovementSourceType.Count && m.SourceId.HasValue))
                .Select(m => m.StockCountId ?? m.SourceId!.Value).Distinct().ToList();

            Dictionary<int, string> salesMap = saleIds.Count > 0
                ? await db.Sales.AsNoTracking().Where(s => saleIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.DocNo ?? $"SO-{s.Id}")
                : [];
            Dictionary<int, string> purchasesMap = purchaseIds.Count > 0
                ? await db.Purchases.AsNoTracking().Where(p => purchaseIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DocNo ?? $"PO-{p.Id}")
                : [];
            Dictionary<int, string> transfersMap = transferIds.Count > 0
                ? await db.StockTransfers.AsNoTracking().Where(t => transferIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.DocNo ?? $"TR-{t.Id}")
                : [];
            Dictionary<int, string> countsMap = countIds.Count > 0
                ? await db.StockCounts.AsNoTracking().Where(c => countIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.DocNo ?? $"SC-{c.Id}")
                : [];

            _allMovements.Clear();
            foreach (var m in movements)
            {
                var product = m.Lot?.Product;
                var location = m.Location;
                var baseUnit = product?.Units.FirstOrDefault(u => u.IsBase)?.Name ?? "وحدة";
                var locType = location?.Type == StockLocationType.Vehicle ? "سيارة" : "مخزن";

                var kindLabel = GetKindLabel(m.Kind);
                var isInbound = m.Quantity > 0m;
                var signPrefix = isInbound ? "+" : "";
                var qtyText = $"{signPrefix}{m.Quantity:N3} {baseUnit}";
                var qtyColor = isInbound ? "#107C41" : "#D13438";

                string docNo = "—";
                if (m.SourceType == MovementSourceType.Sale && m.SourceId.HasValue && salesMap.TryGetValue(m.SourceId.Value, out var sDoc))
                    docNo = sDoc;
                else if (m.SourceType == MovementSourceType.Purchase && m.SourceId.HasValue && purchasesMap.TryGetValue(m.SourceId.Value, out var pDoc))
                    docNo = pDoc;
                else if (m.TransferId.HasValue && transfersMap.TryGetValue(m.TransferId.Value, out var tDoc))
                    docNo = tDoc;
                else if (m.SourceType == MovementSourceType.Transfer && m.SourceId.HasValue && transfersMap.TryGetValue(m.SourceId.Value, out var tDoc2))
                    docNo = tDoc2;
                else if (m.StockCountId.HasValue && countsMap.TryGetValue(m.StockCountId.Value, out var cDoc))
                    docNo = cDoc;
                else if (m.SourceType == MovementSourceType.Count && m.SourceId.HasValue && countsMap.TryGetValue(m.SourceId.Value, out var cDoc2))
                    docNo = cDoc2;
                else if (m.SourceType == MovementSourceType.Opening)
                    docNo = "رصيد افتتاحي";

                _allMovements.Add(new MovementRow(
                    m.Id,
                    m.ProductId,
                    m.LotId,
                    m.OccurredAt,
                    m.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    product?.Name ?? $"صنف #{m.ProductId}",
                    product?.Sku ?? string.Empty,
                    location?.Name == "Main Warehouse" ? "المستودع الرئيسي" : (location?.Name ?? $"موقع #{m.LocationId}"),
                    locType,
                    m.Kind,
                    kindLabel,
                    m.Quantity,
                    qtyText,
                    qtyColor,
                    isInbound,
                    baseUnit,
                    docNo,
                    m.Note ?? m.ReasonCode));
            }

            // Compute KPIs
            TotalStockValue = _allRows.Sum(r => r.TotalValue);
            TotalStockValueText = $"{TotalStockValue:N2} ج.م";
            TotalProductsCount = _allRows.Select(r => r.ProductId).Distinct().Count();
            TotalLotsCount = _allRows.Count;
            LowStockCount = _allRows.Where(r => r.IsLow).Select(r => r.ProductId).Distinct().Count();
            HasLowStock = LowStockCount > 0;
            VansStockValue = _allRows.Where(r => r.LocationType == "سيارة").Sum(r => r.TotalValue);
            VansStockValueText = $"{VansStockValue:N2} ج.م";

            ApplyFilter();
            ApplyTrackingFilter();
            StatusMessage = $"تم التحديث: {ArabicText.Count(_allRows.Count, "لوت مخزني", "لوتان مخزنيان", "لوتات مخزنية")} | {ArabicText.Count(_allMovements.Count, "حركة مسجلة", "حركتان مسجلتان", "حركات مسجلة")}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"خطأ: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string GetKindLabel(InventoryMovementKind kind)
    {
        return kind switch
        {
            InventoryMovementKind.Receipt => "📥 توريد مشتريات",
            InventoryMovementKind.PurchaseReturn => "↪️ مرتجع لمورد",
            InventoryMovementKind.Sale => "📤 فاتورة مبيعات",
            InventoryMovementKind.SaleReturn => "↩️ مرتجع مبيعات",
            InventoryMovementKind.TransferIn => "🔄 تحويل (وارد)",
            InventoryMovementKind.TransferOut => "🔄 تحويل (صادر)",
            InventoryMovementKind.CountAdjust => "⚖️ تسوية جرد",
            InventoryMovementKind.Adjust => "⚙️ تعديل يدوي",
            InventoryMovementKind.Scrap => "🗑️ هالك / تالف",
            InventoryMovementKind.Opening => "🏁 رصيد افتتاحي",
            InventoryMovementKind.Reversal => "↩️ حركة عكسية",
            _ => kind.ToString(),
        };
    }

    private void ApplyFilter()
    {
        FilteredRows.Clear();
        var search = SearchText.Trim().ToLowerInvariant();

        foreach (var r in _allRows)
        {
            if (LocationFilter == 1 && r.LocationType != "مخزن") continue;
            if (LocationFilter == 2 && r.LocationType != "سيارة") continue;
            if (StatusFilter == 1 && !r.IsLow) continue;

            if (!string.IsNullOrEmpty(search)
                && !r.ProductName.ToLowerInvariant().Contains(search)
                && !r.Sku.ToLowerInvariant().Contains(search)
                && !r.LocationName.ToLowerInvariant().Contains(search))
                continue;

            FilteredRows.Add(r);
        }

        var totalValue = FilteredRows.Sum(r => r.TotalValue);
        TotalValueText = $"{totalValue:N2}";
        RowCount = FilteredRows.Count;
    }

    private void ApplyTrackingFilter()
    {
        FilteredMovements.Clear();
        var search = TrackingSearchText.Trim().ToLowerInvariant();

        foreach (var m in _allMovements)
        {
            // Location Filter
            if (TrackingLocationFilter == 1 && m.LocationType != "مخزن") continue;
            if (TrackingLocationFilter == 2 && m.LocationType != "سيارة") continue;

            // Kind Filter
            if (TrackingKindFilter == 1 && m.Kind is not (InventoryMovementKind.Sale or InventoryMovementKind.SaleReturn)) continue;
            if (TrackingKindFilter == 2 && m.Kind is not (InventoryMovementKind.Receipt or InventoryMovementKind.PurchaseReturn)) continue;
            if (TrackingKindFilter == 3 && m.Kind is not (InventoryMovementKind.TransferIn or InventoryMovementKind.TransferOut)) continue;
            if (TrackingKindFilter == 4 && m.Kind is not (InventoryMovementKind.CountAdjust or InventoryMovementKind.Adjust or InventoryMovementKind.Scrap)) continue;

            // Search
            if (!string.IsNullOrEmpty(search)
                && !m.ProductName.ToLowerInvariant().Contains(search)
                && !m.Sku.ToLowerInvariant().Contains(search)
                && !m.LocationName.ToLowerInvariant().Contains(search)
                && !m.DocumentNo.ToLowerInvariant().Contains(search)
                && !(m.Notes ?? string.Empty).ToLowerInvariant().Contains(search))
                continue;

            FilteredMovements.Add(m);
        }

        MovementsCount = FilteredMovements.Count;
    }
}
