using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Domain.Distributors;
using Daftar.Domain.Finance;
using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Domain.Sales;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Daftar.Desktop.Diagnostics;

namespace Daftar.ViewModels;

// ─── Supporting records ───────────────────────────────────────────────────────

public sealed record SessionRow(
    int Id, string DistributorName, string OpenedAt, string ClosedAt,
    string StatusLabel, string SalesText, string CashSalesText, string DebtCollectionsText, string TotalCashText, string CashDiffText,
    bool IsOpen, int DistributorId = 0, string CommissionText = "-", decimal ExpectedCash = 0m)
{
    public string CollectionsText => TotalCashText;
}

public sealed record DistributorManageRow(
    int Id, string Name, string Phone, string VehicleName, string CustodyAccountName,
    decimal CommissionRate, CommissionBasis CommissionBasis, string CommissionText,
    bool IsActive, string StatusLabel, bool HasOpenSession, int? OpenSessionId)
{
    public string SessionStatusText => HasOpenSession ? "جولة مفتوحة" : "لا توجد جولة";
    public string ActiveToggleText  => IsActive ? "تعطيل" : "تفعيل";
}

public sealed record DistributorLookupItem(int Id, string Name, string VehicleName)
{
    public string DisplayText => $"{Name} ({VehicleName})";
}

public sealed record CommissionBasisOption(CommissionBasis Value, string Label);

public sealed record DistributorFilterOption(int? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record DistributorReportRow(
    int DistributorId, string DistributorName, int SessionsCount,
    string SalesText, string CollectionsText, string CommissionRateText,
    string CommissionText, string CashDiffText, bool IsCashShort);

public sealed record SessionSaleRow(
    int Id,
    string DocNo,
    string CustomerName,
    string KindText,
    string ItemsText,
    decimal Total,
    decimal PaidCash,
    string Date)
{
    public string TotalText => $"{Total:N2} ج.م";
    public string PaidCashText => $"{PaidCash:N2} ج.م";
}

public sealed record SessionInvoiceLineRow(
    string ProductName, string UnitName, decimal Quantity, decimal UnitPrice, decimal Total)
{
    public string QuantityText  => $"{Quantity:N0} {UnitName}";
    public string UnitPriceText => $"{UnitPrice:N2} ج.م";
    public string TotalText     => $"{Total:N2} ج.م";
}

public sealed record SessionInvoiceDetailRow(
    int Id,
    string DocNo,
    string CustomerName,
    string PaymentTypeLabel,
    string ItemsSummary,
    decimal Total,
    decimal PaidCash,
    decimal RemainingCredit,
    string DateText,
    List<SessionInvoiceLineRow> Lines)
{
    public string TotalText           => $"{Total:N2} ج.م";
    public string PaidCashText        => $"{PaidCash:N2} ج.م";
    public string RemainingCreditText => $"{RemainingCredit:N2} ج.م";
}

public sealed record SessionDebtCollectionRow(
    int Id, string DocNo, string CustomerName, decimal Amount, string DateText)
{
    public string AmountText => $"{Amount:N2} ج.م";
}

public sealed record SessionStockMovementRow(
    string DocNo, string TypeLabel, string FromLocation, string ToLocation, string ItemsSummary, string DateText);

public sealed record SessionUnsoldStockItem(
    string ProductName, string UnitName, decimal Quantity)
{
    public string QuantityText => $"{Quantity:N0} {UnitName}";
}

public sealed record SessionDetailsViewData(
    int SessionId,
    string DistributorName,
    string StatusText,
    string OpenedAt,
    string ClosedAt,
    decimal TotalSales,
    decimal CashSales,
    decimal CreditSales,
    decimal DebtCollections,
    decimal TotalCashReceived,
    decimal CommissionAmount,
    decimal ExpectedCash,
    decimal CountedCash,
    decimal CashDifference,
    List<SessionInvoiceDetailRow> Invoices,
    List<SessionDebtCollectionRow> Collections,
    List<SessionStockMovementRow> StockMovements)
{
    public string TotalSalesText        => $"{TotalSales:N2} ج.م";
    public string CashSalesText         => $"{CashSales:N2} ج.م";
    public string CreditSalesText       => $"{CreditSales:N2} ج.م";
    public string DebtCollectionsText   => $"{DebtCollections:N2} ج.م";
    public string TotalCashReceivedText => $"{TotalCashReceived:N2} ج.م";
    public string CommissionText        => $"{CommissionAmount:N2} ج.م";
    public string ExpectedCashText      => $"{ExpectedCash:N2} ج.م";
    public string CountedCashText       => $"{CountedCash:N2} ج.م";
    public string CashDiffText          => $"{CashDifference:N2} ج.م";
    public int InvoicesCount            => Invoices.Count;
    public int CollectionsCount         => Collections.Count;
}

public sealed record CustomerLookupItem(int Id, string Name, string Phone)
{
    public string DisplayText => string.IsNullOrWhiteSpace(Phone) ? Name : $"{Name} — {Phone}";
}

public sealed record WarehouseLocationOption(int Id, string Name)
{
    public override string ToString() => Name;
}

// ─── Wizard step enum ─────────────────────────────────────────────────────────

public enum WizardStep
{
    None,
    Open_Distributor,   // step 1/2: pick distributor
    Open_LoadVehicle,   // step 2/2: pick products to load
    Settle_Customer,    // settle loop A: pick customer
    Settle_Products,    // settle loop B: pick products
    Settle_Finish,      // final: counted cash + confirm close
}

// ─── WizardProductButton ──────────────────────────────────────────────────────

public sealed record WizardUnitOption(
    int ProductUnitId,
    string UnitName,
    decimal UnitFactor,
    decimal Available,
    decimal UnitPrice)
{
    public override string ToString() => UnitName;
}

public sealed partial class WizardProductButton : ObservableObject
{
    public int    LotId         { get; }
    public int    ProductId     { get; }
    public decimal LotMaxBase   { get; }
    public string ProductName   { get; }

    public ObservableCollection<WizardUnitOption> AvailableUnits { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProductUnitId), nameof(UnitFactor), nameof(UnitName),
                              nameof(Available), nameof(UnitPrice), nameof(PriceText),
                              nameof(AvailableText), nameof(LineTotalText), nameof(HasMultipleUnits))]
    private WizardUnitOption? _selectedUnit;

    public int     ProductUnitId => SelectedUnit?.ProductUnitId ?? 0;
    public decimal UnitFactor    => SelectedUnit?.UnitFactor    ?? 1m;
    public string  UnitName      => SelectedUnit?.UnitName      ?? string.Empty;
    public decimal Available     => SelectedUnit?.Available     ?? 0m;
    public decimal UnitPrice     => SelectedUnit?.UnitPrice     ?? 0m;

    public bool HasMultipleUnits => AvailableUnits.Count > 1;

    public Action? OnUnitChanged { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelected), nameof(QtyText), nameof(LineTotalText), nameof(TileBackground), nameof(TileBorderBrush))]
    private decimal _quantity;

    [ObservableProperty]
    private string _quantityInput = "0";

    partial void OnQuantityChanged(decimal value)
    {
        var text = value > 0 ? value.ToString("0.##") : "0";
        if (_quantityInput != text)
        {
            _quantityInput = text;
            OnPropertyChanged(nameof(QuantityInput));
        }
    }

    partial void OnSelectedUnitChanged(WizardUnitOption? value)
    {
        if (value is not null && Quantity > value.Available)
        {
            Quantity = Math.Min(Quantity, value.Available);
        }
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(AvailableText));
        OnPropertyChanged(nameof(LineTotalText));
        OnUnitChanged?.Invoke();
    }

    public bool   IsSelected       => Quantity > 0;
    public string QtyText          => Quantity > 0 ? $"× {Quantity:N0}" : string.Empty;
    public string LineTotalText    => Quantity > 0 ? $"{Rounding.Amount(Quantity * UnitPrice):N2} ج.م" : string.Empty;
    public string TileBackground   => Quantity > 0 ? "#EAF3FB" : "#FFFFFF";
    public string TileBorderBrush  => Quantity > 0 ? "#0078D7" : "#D0D0D0";

    public string PriceText     => UnitPrice > 0 ? $"{UnitPrice:N2} ج.م / {UnitName}" : string.Empty;
    public string AvailableText => $"متاح: {Available:N0} {UnitName}";

    public WizardProductButton(int lotId, int productId, decimal lotMaxBase, string productName)
    {
        LotId = lotId;
        ProductId = productId;
        LotMaxBase = lotMaxBase;
        ProductName = productName;
    }
}

// ─── Main ViewModel ───────────────────────────────────────────────────────────

public partial class SessionsViewModel(IServiceProvider services) : ObservableObject
{
    private readonly IServiceProvider _services = services;

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private int    _selectedTabIndex;
    [ObservableProperty] private SessionRow? _selectedSession;

    // Tab 1: Sessions KPI
    public ObservableCollection<SessionRow> Rows { get; } = [];
    [ObservableProperty] private string _openSessionsCountText = "0 جولة مفتوحة";
    [ObservableProperty] private string _totalSalesText       = "0.00 ج.م";
    [ObservableProperty] private string _totalCollectionsText = "0.00 ج.م";
    [ObservableProperty] private string _totalCommissionsText = "0.00 ج.م";

    // ── Wizard state ────────────────────────────────────────────────────────

    [ObservableProperty] private WizardStep _wizardStep = WizardStep.None;
    [ObservableProperty] private string     _wizardError = string.Empty;

    public bool IsWizardVisible       => WizardStep != WizardStep.None;
    public bool IsWizard_OpenDist     => WizardStep == WizardStep.Open_Distributor;
    public bool IsWizard_OpenLoad     => WizardStep == WizardStep.Open_LoadVehicle;
    public bool IsWizard_SettleCust   => WizardStep == WizardStep.Settle_Customer;
    public bool IsWizard_SettleProd   => WizardStep == WizardStep.Settle_Products;
    public bool IsWizard_SettleFinish => WizardStep == WizardStep.Settle_Finish;
    /// <summary>«إنهاء الجولة» jumps straight to the settle step; it belongs to the two steps that come
    /// before it, not to the finish step itself, where it would sit beside the primary and say the same
    /// thing twice.</summary>
    public bool IsWizard_CanFinish    => WizardStep == WizardStep.Settle_Customer
                                      || WizardStep == WizardStep.Settle_Products;

    /// <summary>Stable dialog title: it does not change between steps, so the frame never jumps.</summary>
    /// <remarks>
    /// The step used to be baked into this string, which left the settle half unnumbered and reflowed
    /// the header on every step. The moving part is <see cref="WizardSubtitle"/> and the stepper.
    /// </remarks>
    public string WizardTitle => WizardStep switch
    {
        WizardStep.None => string.Empty,
        WizardStep.Open_Distributor or WizardStep.Open_LoadVehicle => "بدء جولة موزع",
        _ => "جولة الموزع",
    };

    /// <summary>Whose round this is; empty on the open half, before a distributor is known.</summary>
    public string WizardSubtitle => WizardStep is WizardStep.Open_Distributor or WizardStep.Open_LoadVehicle
        ? string.Empty
        : _wizardDistributorName;

    /// <summary>1-based step number across the whole wizard, so both halves share one count.</summary>
    public int WizardStepNumber => WizardStep switch
    {
        WizardStep.Open_Distributor  => 1,
        WizardStep.Open_LoadVehicle  => 2,
        WizardStep.Settle_Customer   => 3,
        WizardStep.Settle_Products   => 4,
        WizardStep.Settle_Finish     => 5,
        _ => 0,
    };

    public int WizardStepCount => 5;

    /// <summary>Short action label for the stepper, e.g. «اختر العميل».</summary>
    public string WizardStepName => WizardStep switch
    {
        WizardStep.Open_Distributor => "اختر الموزع",
        WizardStep.Open_LoadVehicle => "حمّل السيارة",
        WizardStep.Settle_Customer  => "اختر العميل",
        WizardStep.Settle_Products  => $"فاتورة {_wizardCurrentCustomerName}",
        WizardStep.Settle_Finish    => "إنهاء وتسوية",
        _ => string.Empty,
    };

    public string WizardStepText => $"الخطوة {WizardStepNumber} من {WizardStepCount} — {WizardStepName}";

    /// <summary>False when the van carries nothing, so the invoice step can say so instead of showing an empty grid.</summary>
    public bool WizardHasVehicleProducts => WizardProducts.Count > 0;

    /// <summary>False when every distributor already has a round open, so step 1 can explain the empty combo.</summary>
    public bool HasAvailableDistributors => AvailableDistributorsForNewSession.Count > 0;

    // Stepper: a step is "done" once the wizard has moved past it. Whether it is the *current* one is
    // already answered by the IsWizard_* flags, so only the "done" half needs new state.
    public bool WizardStepDone1 => WizardStepNumber > 1;
    public bool WizardStepDone2 => WizardStepNumber > 2;
    public bool WizardStepDone3 => WizardStepNumber > 3;
    public bool WizardStepDone4 => WizardStepNumber > 4;

    /// <summary>True on the steps that sit behind another, so the footer needs one «رجوع» button.</summary>
    public bool IsWizard_CanGoBack => WizardStep is WizardStep.Open_LoadVehicle
                                                or WizardStep.Settle_Products
                                                or WizardStep.Settle_Finish;

    /// <summary>
    /// The empty-state flags are computed rather than counted in XAML: a bare
    /// <c>{Binding SomeCollection.Count}</c> on IsVisible is not a boolean and silently leaves the
    /// element shown, which is how the "no distributors" note never appeared.
    /// </summary>
    public bool HasCustomerResults  => WizardCustomerResults.Count > 0;
    public bool HasUnsoldStock      => WizardUnsoldStockItems.Count > 0;

    partial void OnWizardStepChanged(WizardStep value)
    {
        OnPropertyChanged(nameof(IsWizardVisible));
        OnPropertyChanged(nameof(IsWizard_OpenDist));
        OnPropertyChanged(nameof(IsWizard_OpenLoad));
        OnPropertyChanged(nameof(IsWizard_SettleCust));
        OnPropertyChanged(nameof(IsWizard_SettleProd));
        OnPropertyChanged(nameof(IsWizard_SettleFinish));
        OnPropertyChanged(nameof(IsWizard_CanFinish));
        OnPropertyChanged(nameof(WizardTitle));
        OnPropertyChanged(nameof(WizardSubtitle));
        OnPropertyChanged(nameof(WizardStepNumber));
        OnPropertyChanged(nameof(WizardStepName));
        OnPropertyChanged(nameof(WizardStepText));
        OnPropertyChanged(nameof(WizardStepDone1));
        OnPropertyChanged(nameof(WizardStepDone2));
        OnPropertyChanged(nameof(WizardStepDone3));
        OnPropertyChanged(nameof(WizardStepDone4));
        OnPropertyChanged(nameof(IsWizard_CanGoBack));
        NotifyWizardCommands();
    }

    // ── Wizard command availability ───────────────────────────────────────────
    //
    // Every primary button in the wizard used to stay enabled in every state, so the only way to
    // learn what a step was waiting for was to press «التالي» and read the error banner. These
    // predicates let each button carry its own precondition — a disabled button that means
    // "nothing picked yet" says more than an enabled one that fails.

    private bool CanNextOpenToLoad()       => SelectedDistributorForNewSession is not null;
    private bool CanConfirmLoadVehicle()   => WizardSelectedWarehouse is not null
                                           && WizardProducts.Any(p => p.Quantity > 0);
    private bool CanNextSettleToProducts() => WizardSelectedCustomer is not null;
    private bool CanRegisterInvoice()      => WizardSelectedCustomer is not null
                                           && (WizardCartTotalAmount > 0m || WizardDebtCollectionAmount > 0m);

    private void NotifyWizardCommands()
    {
        WizardNext_OpenToLoadCommand.NotifyCanExecuteChanged();
        WizardConfirmLoadVehicleCommand.NotifyCanExecuteChanged();
        WizardNext_SettleToProductsCommand.NotifyCanExecuteChanged();
        WizardRegisterInvoiceCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// <see cref="WizardProducts"/> is rebuilt on every entry into a step, so each item is hooked as
    /// it arrives; the previous items are discarded with the collection.
    /// </summary>
    private void AddWizardProduct(WizardProductButton product)
    {
        product.PropertyChanged += OnWizardProductChanged;
        WizardProducts.Add(product);
        OnPropertyChanged(nameof(WizardHasVehicleProducts));
    }

    private void OnWizardProductChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(WizardProductButton.Quantity) or nameof(WizardProductButton.SelectedUnit)))
            return;
        NotifyWizardCommands();
    }

    // Wizard: Open session
    public ObservableCollection<DistributorLookupItem>   AvailableDistributorsForNewSession { get; } = [];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WizardNext_OpenToLoadCommand))]
    private DistributorLookupItem? _selectedDistributorForNewSession;
    public ObservableCollection<WarehouseLocationOption> WizardWarehouseOptions { get; } = [];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WizardConfirmLoadVehicleCommand))]
    private WarehouseLocationOption? _wizardSelectedWarehouse;

    private int    _wizardCreatedSessionId;
    private int    _wizardCreatedDistributorId;
    private bool   _wizardVehicleLoaded;
    private string _wizardDistributorName = string.Empty;
    private int    _wizardVehicleLocationId;

    // Wizard: Settle session
    private int    _wizardSettleSessionId;
    private string _wizardCurrentCustomerName = string.Empty;
    private decimal _wizardTotalSales;

    [ObservableProperty] private string _wizardCustomerSearch = string.Empty;

    /// <summary>
    /// Set while the search box is being filled in for the user rather than by them. Picking a
    /// customer types their name into the box, and that is not a search: without this the round trip
    /// it set going put the customer straight back into the list they had just been picked from.
    /// </summary>
    private bool _wizardCustomerSearchIsProgrammatic;

    public ObservableCollection<CustomerLookupItem> WizardCustomerResults { get; } = [];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WizardNext_SettleToProductsCommand))]
    [NotifyCanExecuteChangedFor(nameof(WizardRegisterInvoiceCommand))]
    private CustomerLookupItem? _wizardSelectedCustomer;

    // New customer inline form
    [ObservableProperty] private bool   _wizardIsNewCustomerVisible;
    [ObservableProperty] private string _wizardNewCustomerName    = string.Empty;
    [ObservableProperty] private string _wizardNewCustomerPhone   = string.Empty;
    [ObservableProperty] private string _wizardNewCustomerAddress = string.Empty;
    [ObservableProperty] private string _wizardNewCustomerError   = string.Empty;

    // Settlement
    private int    _wizardDistributorCashAccountId;
    [ObservableProperty] private string _wizardCountedCash     = string.Empty;
    [ObservableProperty] private string _wizardExpectedCashText = "0.00 ج.م";
    [ObservableProperty] private int    _wizardInvoiceCount;
    [ObservableProperty] private string _wizardTotalSalesText  = "0.00 ج.م";

    // Payment & Collection fields
    [ObservableProperty] private string  _wizardPaymentMode = "Cash"; // "Cash", "Credit", "Partial"
    [ObservableProperty] private decimal _wizardPaidAmount;
    [ObservableProperty] private string  _wizardPaidAmountText = "0.00";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WizardRegisterInvoiceCommand))]
    private decimal _wizardDebtCollectionAmount;
    [ObservableProperty] private string  _wizardDebtCollectionText = "0.00";
    [ObservableProperty] private decimal _wizardCustomerPreviousBalance;
    [ObservableProperty] private string  _wizardCustomerPreviousBalanceText = "0.00 ج.م";

    public decimal WizardCartTotalAmount =>
        WizardProducts.Sum(p => Rounding.Amount(p.UnitPrice * p.Quantity));

    public bool WizardIsCashPayment    => WizardPaymentMode == "Cash";
    public bool WizardIsCreditPayment  => WizardPaymentMode == "Credit";
    public bool WizardIsPartialPayment => WizardPaymentMode == "Partial";

    public decimal WizardRemainingCreditAmount =>
        Math.Max(0m, Rounding.Amount(WizardCartTotalAmount - WizardPaidAmount));

    public string WizardRemainingCreditText => $"{WizardRemainingCreditAmount:N2} ج.م";

    public string WizardRemainingCreditNotice =>
        WizardRemainingCreditAmount > 0m
            ? $"المتبقي كآجل: {WizardRemainingCreditAmount:N2} ج.م"
            : (WizardCartTotalAmount > 0m ? "مدفوع بالكامل نقداً" : string.Empty);

    public decimal WizardTotalCashFromCustomerAmount =>
        Rounding.Amount(WizardPaidAmount + WizardDebtCollectionAmount);

    public string WizardTotalCashFromCustomerText => $"{WizardTotalCashFromCustomerAmount:N2} ج.م";

    public decimal WizardProjectedBalanceAmount =>
        Rounding.Amount(WizardCustomerPreviousBalance + WizardRemainingCreditAmount - WizardDebtCollectionAmount);

    public string WizardProjectedBalanceText => WizardProjectedBalanceAmount switch
    {
        > 0m => $"{WizardProjectedBalanceAmount:N2} ج.م (مديونية عليه)",
        < 0m => $"{(-WizardProjectedBalanceAmount):N2} ج.م (رصيد له)",
        _    => "0.00 ج.م (خالص)",
    };

    public string WizardRegisterButtonText =>
        WizardCartTotalAmount > 0m
            ? "✔ تسجيل الفاتورة والدفع والتالي"
            : (WizardDebtCollectionAmount > 0m ? "✔ تسجيل التحصيل فقط والتالي" : "✔ تسجيل الفاتورة والتالي");

    public string WizardCashBtnBackground    => WizardPaymentMode == "Cash"    ? "#0078D7" : "#FFFFFF";
    public string WizardCashBtnForeground    => WizardPaymentMode == "Cash"    ? "#FFFFFF" : "#0078D7";
    public string WizardCreditBtnBackground  => WizardPaymentMode == "Credit"  ? "#D24726" : "#FFFFFF";
    public string WizardCreditBtnForeground  => WizardPaymentMode == "Credit"  ? "#FFFFFF" : "#D24726";
    public string WizardPartialBtnBackground => WizardPaymentMode == "Partial" ? "#107C41" : "#FFFFFF";
    public string WizardPartialBtnForeground => WizardPaymentMode == "Partial" ? "#FFFFFF" : "#107C41";

    // Shared product buttons + session sales log
    public ObservableCollection<WizardProductButton> WizardProducts    { get; } = [];
    public ObservableCollection<SessionSaleRow>      WizardSessionSales { get; } = [];

    // Session Details Dialog
    [ObservableProperty] private bool _isSessionDetailsDialogVisible;
    [ObservableProperty] private SessionDetailsViewData? _selectedSessionDetails;
    [ObservableProperty] private SessionInvoiceDetailRow? _selectedInvoiceInDetails;

    // Settle Finish breakdown & unsold stock
    [ObservableProperty] private string _wizardCashSalesText       = "0.00 ج.م";
    [ObservableProperty] private string _wizardCreditSalesText     = "0.00 ج.م";
    [ObservableProperty] private string _wizardDebtCollectionsText = "0.00 ج.م";
    public ObservableCollection<SessionUnsoldStockItem> WizardUnsoldStockItems { get; } = [];

    public string WizardCartTotalText
    {
        get
        {
            var total = WizardProducts.Sum(p => Rounding.Amount(p.UnitPrice * p.Quantity));
            return $"{total:N2} ج.م";
        }
    }

    // Tab 2: Distributor management
    public ObservableCollection<DistributorManageRow> Distributors { get; } = [];
    [ObservableProperty] private DistributorManageRow? _selectedDistributor;
    [ObservableProperty] private bool   _isDistributorFormVisible;
    [ObservableProperty] private bool   _isEditingDistributor;
    [ObservableProperty] private int?   _editingDistributorId;
    [ObservableProperty] private string _distributorFormTitle          = "إضافة موزع جديد";
    [ObservableProperty] private string _distributorFormName           = string.Empty;
    [ObservableProperty] private string _distributorFormPhone          = string.Empty;
    [ObservableProperty] private string _distributorFormVehicle        = string.Empty;
    [ObservableProperty] private decimal _distributorFormCommissionRate;
    [ObservableProperty] private CommissionBasisOption? _selectedCommissionBasisChoice;
    [ObservableProperty] private string _distributorFormError          = string.Empty;

    public IReadOnlyList<CommissionBasisOption> CommissionBasisChoices { get; } =
    [
        new(CommissionBasis.Sales,       "على المبيعات (صافي المبيعات)"),
        new(CommissionBasis.Collections, "على التحصيلات الفعلية"),
    ];

    // Tab 3: Reports
    public ObservableCollection<DistributorFilterOption> DistributorFilterChoices { get; } = [];
    [ObservableProperty] private DistributorFilterOption? _selectedDistributorFilter;
    public ObservableCollection<DistributorReportRow> ReportRows { get; } = [];
    [ObservableProperty] private string _reportTotalSalesText       = "0.00 ج.م";
    [ObservableProperty] private string _reportTotalCollectionsText = "0.00 ج.م";
    [ObservableProperty] private string _reportTotalCommissionsText = "0.00 ج.م";
    [ObservableProperty] private string _reportNetCashDiffText      = "0.00 ج.م";
    [ObservableProperty] private string _reportSessionsCountText    = "0 جولة";
    [ObservableProperty] private bool   _reportIsNetCashDiffNegative;

    public SessionsViewModel() : this(App.Services!) { }

    // ── Load ─────────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task InitializeAsync()
    {
        SelectedCommissionBasisChoice = CommissionBasisChoices[0];
        await LoadAsync();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusMessage = "جاري التحميل...";
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var sessions = await db.DistributorSessions
                .AsNoTracking()
                .Include(s => s.Distributor)
                .OrderByDescending(s => s.OpenedAt)
                .Take(150)
                .ToListAsync();

            Rows.Clear();
            decimal totalSales = 0m, totalCollections = 0m, totalCommissions = 0m;
            int openCount = 0;

            var sessionIds = sessions.Select(s => s.Id).ToList();
            var sessionPayments = await db.Payments.AsNoTracking()
                .Where(p => p.DistributorSessionId.HasValue
                         && sessionIds.Contains(p.DistributorSessionId.Value)
                         && p.Status == DocumentStatus.Posted
                         && p.Direction == PaymentDirection.In)
                .Select(p => new { SessionId = p.DistributorSessionId!.Value, p.SaleId, p.Amount })
                .ToListAsync();

            var paymentsBySession = sessionPayments.GroupBy(p => p.SessionId).ToDictionary(
                g => g.Key,
                g => new {
                    CashSales = g.Where(p => p.SaleId != null).Sum(p => p.Amount),
                    DebtCollections = g.Where(p => p.SaleId == null).Sum(p => p.Amount),
                    TotalCash = g.Sum(p => p.Amount),
                });

            foreach (var s in sessions)
            {
                if (s.IsOpen) openCount++;
                if (s.SalesTotal.HasValue)      totalSales       += s.SalesTotal.Value;
                if (s.CollectionsTotal.HasValue) totalCollections += s.CollectionsTotal.Value;
                if (s.CommissionAmount.HasValue) totalCommissions += s.CommissionAmount.Value;

                decimal cashSales = 0m, debtCollections = 0m, totalCash = 0m;
                if (paymentsBySession.TryGetValue(s.Id, out var pInfo))
                {
                    cashSales = pInfo.CashSales;
                    debtCollections = pInfo.DebtCollections;
                    totalCash = pInfo.TotalCash;
                }
                else if (s.CollectionsTotal.HasValue)
                {
                    totalCash = s.CollectionsTotal.Value;
                }

                Rows.Add(new SessionRow(
                    s.Id, s.Distributor?.Name ?? "-",
                    s.OpenedAt.ToString("yyyy-MM-dd HH:mm"),
                    s.ClosedAt.HasValue ? s.ClosedAt.Value.ToString("yyyy-MM-dd HH:mm") : "-",
                    s.IsOpen ? "مفتوحة" : "مغلقة",
                    s.SalesTotal.HasValue ? $"{s.SalesTotal:N2} ج.م" : "-",
                    cashSales > 0 ? $"{cashSales:N2} ج.م" : "0.00 ج.م",
                    debtCollections > 0 ? $"{debtCollections:N2} ج.م" : "0.00 ج.م",
                    totalCash > 0 ? $"{totalCash:N2} ج.م" : (s.CollectionsTotal.HasValue ? $"{s.CollectionsTotal:N2} ج.م" : "0.00 ج.م"),
                    s.CashDifference.HasValue ? $"{s.CashDifference:N2} ج.م" : "-",
                    s.IsOpen, s.DistributorId,
                    s.CommissionAmount.HasValue ? $"{s.CommissionAmount:N2} ج.م" : "-",
                    s.ExpectedCash ?? 0m));
            }

            OpenSessionsCountText = ArabicText.Count(openCount, "جولة مفتوحة", "جولتان مفتوحتان", "جولات مفتوحة");
            TotalSalesText        = $"{totalSales:N2} ج.م";
            TotalCollectionsText  = $"{totalCollections:N2} ج.م";
            TotalCommissionsText  = $"{totalCommissions:N2} ج.م";

            var distributors = await db.Distributors
                .AsNoTracking()
                .Include(d => d.StockLocation)
                .Include(d => d.CashAccount)
                .Include(d => d.Sessions.Where(s => s.Status == DistributorSessionStatus.Open))
                .OrderBy(d => d.Name)
                .ToListAsync();

            Distributors.Clear();
            AvailableDistributorsForNewSession.Clear();

            foreach (var d in distributors)
            {
                var openSession = d.Sessions.FirstOrDefault();
                var hasOpen     = openSession is not null;

                Distributors.Add(new DistributorManageRow(
                    d.Id, d.Name, d.Phone ?? "-",
                    d.StockLocation?.Name ?? "-", d.CashAccount?.Name ?? "-",
                    d.CommissionRate, d.CommissionBasis,
                    $"{d.CommissionRate:N1}% ({(d.CommissionBasis == CommissionBasis.Sales ? "مبيعات" : "تحصيل")})",
                    d.IsActive, d.IsActive ? "نشط" : "معطل", hasOpen, openSession?.Id));

                if (d.IsActive && !hasOpen)
                    AvailableDistributorsForNewSession.Add(
                        new DistributorLookupItem(d.Id, d.Name, d.StockLocation?.Name ?? "بدون سيارة"));
            }

            if (AvailableDistributorsForNewSession.Count > 0 && SelectedDistributorForNewSession is null)
                SelectedDistributorForNewSession = AvailableDistributorsForNewSession[0];

            OnPropertyChanged(nameof(HasAvailableDistributors));

            RefreshFilterChoices(distributors);
            CalculateReports(sessions, distributors);

            StatusMessage = $"تم التحميل — {ArabicText.Count(Rows.Count, "جولة", "جولتان", "جولات")}، {ArabicText.Count(Distributors.Count, "موزع", "موزعان", "موزعون")}";
        }
        catch (Exception ex)
        {
            _services.Report("Sessions.LoadAsync", ex);
            StatusMessage = $"خطأ: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    // ── Reports ──────────────────────────────────────────────────────────────

    private void RefreshFilterChoices(List<Distributor> distributors)
    {
        var currentId = SelectedDistributorFilter?.Id;
        DistributorFilterChoices.Clear();
        DistributorFilterChoices.Add(new DistributorFilterOption(null, "جميع الموزعين"));
        foreach (var d in distributors)
            DistributorFilterChoices.Add(new DistributorFilterOption(d.Id, d.Name));
        SelectedDistributorFilter =
            DistributorFilterChoices.FirstOrDefault(c => c.Id == currentId) ?? DistributorFilterChoices[0];
    }

    partial void OnSelectedDistributorFilterChanged(DistributorFilterOption? value)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        var sessions     = db.DistributorSessions.AsNoTracking().Include(s => s.Distributor).ToList();
        var distributors = db.Distributors.AsNoTracking().Include(d => d.StockLocation).ToList();
        CalculateReports(sessions, distributors);
    }

    private void CalculateReports(List<DistributorSession> allSessions, List<Distributor> allDistributors)
    {
        ReportRows.Clear();
        var filteredSessions = SelectedDistributorFilter?.Id is null
            ? allSessions
            : allSessions.Where(s => s.DistributorId == SelectedDistributorFilter.Id).ToList();
        var targetDistributors = SelectedDistributorFilter?.Id is null
            ? allDistributors
            : allDistributors.Where(d => d.Id == SelectedDistributorFilter.Id).ToList();

        decimal kpiSales = 0m, kpiCollections = 0m, kpiCommissions = 0m, kpiCashDiff = 0m;
        int kpiSessions = filteredSessions.Count;

        foreach (var d in targetDistributors)
        {
            var ds = filteredSessions.Where(s => s.DistributorId == d.Id).ToList();
            if (ds.Count == 0 && SelectedDistributorFilter?.Id is null) continue;
            decimal dSales      = ds.Where(s => s.SalesTotal.HasValue).Sum(s => s.SalesTotal!.Value);
            decimal dColl       = ds.Where(s => s.CollectionsTotal.HasValue).Sum(s => s.CollectionsTotal!.Value);
            decimal dComm       = ds.Where(s => s.CommissionAmount.HasValue).Sum(s => s.CommissionAmount!.Value);
            decimal dCashDiff   = ds.Where(s => s.CashDifference.HasValue).Sum(s => s.CashDifference!.Value);
            kpiSales += dSales; kpiCollections += dColl; kpiCommissions += dComm; kpiCashDiff += dCashDiff;
            ReportRows.Add(new DistributorReportRow(d.Id, d.Name, ds.Count,
                $"{dSales:N2} ج.م", $"{dColl:N2} ج.م", $"{d.CommissionRate:N1}%",
                $"{dComm:N2} ج.م", $"{dCashDiff:N2} ج.م", dCashDiff < 0m));
        }

        ReportTotalSalesText       = $"{kpiSales:N2} ج.م";
        ReportTotalCollectionsText = $"{kpiCollections:N2} ج.م";
        ReportTotalCommissionsText = $"{kpiCommissions:N2} ج.م";
        ReportNetCashDiffText      = $"{kpiCashDiff:N2} ج.م";
        ReportSessionsCountText    = ArabicText.Count(kpiSessions, "جولة", "جولتان", "جولات");
        ReportIsNetCashDiffNegative = kpiCashDiff < 0m;
    }

    // ── Tab 2: Distributor management ────────────────────────────────────────

    [RelayCommand]
    private void OpenAddDistributorForm()
    {
        IsEditingDistributor = false; EditingDistributorId = null;
        DistributorFormTitle = "إضافة موزع جديد";
        DistributorFormName = DistributorFormPhone = DistributorFormVehicle = DistributorFormError = string.Empty;
        DistributorFormCommissionRate = 0m;
        SelectedCommissionBasisChoice = CommissionBasisChoices[0];
        IsDistributorFormVisible = true;
    }

    [RelayCommand]
    private void OpenEditDistributorForm(DistributorManageRow? row)
    {
        var t = row ?? SelectedDistributor;
        if (t is null) { StatusMessage = "يرجى تحديد موزع لتعديله."; return; }
        IsEditingDistributor = true; EditingDistributorId = t.Id;
        DistributorFormTitle = $"تعديل: {t.Name}";
        DistributorFormName  = t.Name;
        DistributorFormPhone = t.Phone == "-" ? string.Empty : t.Phone;
        DistributorFormVehicle = t.VehicleName == "-" ? string.Empty : t.VehicleName;
        DistributorFormCommissionRate = t.CommissionRate;
        SelectedCommissionBasisChoice =
            CommissionBasisChoices.FirstOrDefault(c => c.Value == t.CommissionBasis) ?? CommissionBasisChoices[0];
        DistributorFormError = string.Empty;
        IsDistributorFormVisible = true;
    }

    [RelayCommand]
    private void CloseDistributorForm()
    { IsDistributorFormVisible = false; DistributorFormError = string.Empty; }

    [RelayCommand]
    private async Task SaveDistributorAsync()
    {
        if (string.IsNullOrWhiteSpace(DistributorFormName))    { DistributorFormError = "اسم الموزع مطلوب."; return; }
        if (DistributorFormCommissionRate is < 0 or > 100)     { DistributorFormError = "العمولة بين 0 و100%."; return; }
        if (string.IsNullOrWhiteSpace(DistributorFormVehicle)) { DistributorFormError = "اسم السيارة مطلوب."; return; }

        IsBusy = true; DistributorFormError = string.Empty; StatusMessage = "جاري الحفظ...";
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            if (IsEditingDistributor && EditingDistributorId.HasValue)
            {
                var d = await db.Distributors.Include(x => x.StockLocation)
                    .FirstOrDefaultAsync(x => x.Id == EditingDistributorId.Value);
                if (d is null) { DistributorFormError = "الموزع غير موجود."; return; }
                d.Name   = DistributorFormName.Trim();
                d.Phone  = string.IsNullOrWhiteSpace(DistributorFormPhone) ? null : DistributorFormPhone.Trim();
                d.CommissionRate  = DistributorFormCommissionRate;
                d.CommissionBasis = SelectedCommissionBasisChoice?.Value ?? CommissionBasis.Sales;
                if (d.StockLocation is not null && !string.IsNullOrWhiteSpace(DistributorFormVehicle))
                    d.StockLocation.Name = DistributorFormVehicle.Trim();
                d.Validate();
                await db.SaveChangesAsync();
                StatusMessage = $"تم تحديث: {d.Name}";
            }
            else
            {
                var vehicleName = DistributorFormVehicle.Trim();
                var vehicleLoc  = await db.StockLocations.FirstOrDefaultAsync(l =>
                    l.Name == vehicleName && l.Type == StockLocationType.Vehicle);
                if (vehicleLoc is null)
                {
                    vehicleLoc = new StockLocation { Name = vehicleName, Type = StockLocationType.Vehicle, IsActive = true };
                    db.StockLocations.Add(vehicleLoc);
                    await db.SaveChangesAsync();
                }

                var custodyName = $"عهدة موزع: {DistributorFormName.Trim()}";
                var custodyAcc  = await db.CashAccounts.FirstOrDefaultAsync(a =>
                    a.Name == custodyName && a.Type == CashAccountType.DistributorCustody);
                if (custodyAcc is null)
                {
                    custodyAcc = new CashAccount { Name = custodyName, Type = CashAccountType.DistributorCustody, IsActive = true };
                    db.CashAccounts.Add(custodyAcc);
                    await db.SaveChangesAsync();
                }

                var distributor = new Distributor
                {
                    Name = DistributorFormName.Trim(),
                    Phone = string.IsNullOrWhiteSpace(DistributorFormPhone) ? null : DistributorFormPhone.Trim(),
                    StockLocationId = vehicleLoc.Id, CashAccountId = custodyAcc.Id,
                    CommissionRate  = DistributorFormCommissionRate,
                    CommissionBasis = SelectedCommissionBasisChoice?.Value ?? CommissionBasis.Sales,
                    IsActive = true,
                };
                distributor.Validate();
                db.Distributors.Add(distributor);
                await db.SaveChangesAsync();
                StatusMessage = $"تمت الإضافة: {distributor.Name}";
            }

            IsDistributorFormVisible = false;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _services.Report("Sessions.SaveDistributorAsync", ex);
            DistributorFormError = $"خطأ: {ex.Message}";
            StatusMessage = DistributorFormError;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ToggleDistributorActiveAsync(DistributorManageRow? row)
    {
        var target = row ?? SelectedDistributor;
        if (target is null) return;
        IsBusy = true;
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var d = await db.Distributors.FirstOrDefaultAsync(x => x.Id == target.Id);
            if (d is not null)
            {
                d.IsActive = !d.IsActive;
                await db.SaveChangesAsync();
                StatusMessage = d.IsActive ? $"تم تفعيل {d.Name}" : $"تم تعطيل {d.Name}";
                await LoadAsync();
            }
        }
        catch (Exception ex)
        {
            _services.Report("Sessions.ToggleDistributorActiveAsync", ex);
            StatusMessage = $"خطأ: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // WIZARD: Open Session
    // ══════════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private void OpenSessionWizard()
    {
        WizardError = string.Empty;
        ForgetCreatedRound();
        SelectedDistributorForNewSession = AvailableDistributorsForNewSession.FirstOrDefault();
        WizardStep = WizardStep.Open_Distributor;
    }

    /// <summary>
    /// Drops the round this wizard opened, if it is still unused. Opening a session writes nothing
    /// but the session row, so a round the operator backs out of would otherwise sit there holding
    /// the distributor's slot: the open-session guard keeps him off the picker, and the partial
    /// unique index blocks a brand new round.
    ///
    /// A round whose vehicle was already loaded is a real round and is handed over, not abandoned.
    /// Never throws — this runs on the cancel path, where refusing to close the dialog would be
    /// worse than leaving a row behind, so a failure is reported and the dialog still closes.
    /// </summary>
    private async Task AbandonCreatedRoundAsync()
    {
        var sessionId = _wizardCreatedSessionId;
        var loaded    = _wizardVehicleLoaded;
        ForgetCreatedRound();

        if (sessionId == 0 || loaded) return;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var svc = new DistributorSessionService(db, new StockCountService(db), new StockTransferPostingService(db));
            await svc.AbandonAsync(sessionId, userId: 1);
        }
        catch (Exception ex)
        {
            _services.Report("Sessions.AbandonCreatedRound", ex);
            StatusMessage = $"تعذّر إلغاء الجولة #{sessionId} — فيها حركات أو اتقفلت من مكان تاني.";
        }
    }

    /// <summary>Stops this wizard owning the round it opened — used once the round is real.</summary>
    private void ForgetCreatedRound()
    {
        _wizardCreatedSessionId     = 0;
        _wizardCreatedDistributorId = 0;
        _wizardVehicleLoaded        = false;
    }

    /// <summary>Creates the session, then transitions to the load-vehicle step.</summary>
    [RelayCommand(CanExecute = nameof(CanNextOpenToLoad))]
    private async Task WizardNext_OpenToLoadAsync()
    {
        if (SelectedDistributorForNewSession is null)
        { WizardError = "يرجى اختيار الموزع."; return; }

        // Same distributor as the round we already hold: reuse it rather than opening a second one,
        // which the open-session guard would refuse with a raw domain error.
        if (_wizardCreatedSessionId != 0
            && _wizardCreatedDistributorId == SelectedDistributorForNewSession.Id)
        {
            WizardError = string.Empty;
            StatusMessage = $"تم فتح الجولة #{_wizardCreatedSessionId}. اختر المنتجات للتحميل.";
            WizardStep = WizardStep.Open_LoadVehicle;
            return;
        }

        // A different distributor, or no round yet: the held round is not ours to keep.
        await AbandonCreatedRoundAsync();

        IsBusy = true; WizardError = string.Empty; StatusMessage = "جاري فتح الجولة...";
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var svc = new DistributorSessionService(db, new StockCountService(db), new StockTransferPostingService(db));
            var session = await svc.OpenAsync(SelectedDistributorForNewSession.Id, userId: 1);
            _wizardCreatedSessionId     = session.Id;
            _wizardCreatedDistributorId = SelectedDistributorForNewSession.Id;
            _wizardDistributorName  = SelectedDistributorForNewSession.Name;

            var distributor = await db.Distributors.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == SelectedDistributorForNewSession.Id);
            _wizardVehicleLocationId = distributor?.StockLocationId ?? 0;

            // Load warehouse choices
            var warehouses = await db.StockLocations.AsNoTracking()
                .Where(l => l.Type == StockLocationType.Warehouse && l.IsActive)
                .OrderBy(l => l.Name).ToListAsync();
            WizardWarehouseOptions.Clear();
            foreach (var wh in warehouses)
                WizardWarehouseOptions.Add(new WarehouseLocationOption(wh.Id, wh.Name));
            WizardSelectedWarehouse = WizardWarehouseOptions.FirstOrDefault();

            await LoadWarehouseProductButtonsAsync(db);

            StatusMessage = $"تم فتح الجولة #{session.Id}. اختر المنتجات للتحميل.";
            WizardStep = WizardStep.Open_LoadVehicle;
        }
        catch (Exception ex)
        {
            _services.Report("Sessions.WizardNext_OpenToLoadAsync", ex);

            // OpenAsync's only domain refusal is "this distributor already has an open session";
            // showing its English text inside an Arabic dialog told the operator nothing useful.
            WizardError = ex is DomainException
                ? "تعذّر فتح جولة — الموزع عنده جولة مفتوحة بالفعل. اقفلها من زر «تسوية» الأول."
                : $"فشل فتح الجولة: {ex.Message}";
            StatusMessage = WizardError;
        }
        finally { IsBusy = false; }
    }

    partial void OnWizardSelectedWarehouseChanged(WarehouseLocationOption? value)
    {
        if (value is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
                var items = await BuildWarehouseProductButtonsAsync(db);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    WizardProducts.Clear();
                    foreach (var i in items) AddWizardProduct(i);
                });
            }
            catch { /* silent */ }
        });
    }

    private async Task LoadWarehouseProductButtonsAsync(DaftarContext db)
    {
        var items = await BuildWarehouseProductButtonsAsync(db);
        WizardProducts.Clear();
        foreach (var i in items) AddWizardProduct(i);
    }

    private async Task<List<WizardProductButton>> BuildWarehouseProductButtonsAsync(DaftarContext db)
    {
        if (WizardSelectedWarehouse is null) return [];
        var balances = await db.StockBalances.AsNoTracking()
            .Include(b => b.Lot!).ThenInclude(l => l.Product!).ThenInclude(p => p.Units)
            .Where(b => b.LocationId == WizardSelectedWarehouse.Id && b.Quantity > 0)
            .OrderBy(b => b.Lot!.Product!.Name).ToListAsync();

        var result = new List<WizardProductButton>();
        foreach (var b in balances)
        {
            var product = b.Lot?.Product;
            if (product is null) continue;

            var activeUnits = product.Units.Where(u => u.IsActive)
                .OrderBy(u => u.IsDefaultSale ? 0 : u.IsBase ? 1 : 2)
                .ToList();
            if (activeUnits.Count == 0) continue;

            var btn = new WizardProductButton(
                b.LotId,
                product.Id,
                b.Quantity,
                product.Name);

            foreach (var unit in activeUnits)
            {
                var availableInUnit = Math.Floor(b.Quantity / unit.Factor);
                btn.AvailableUnits.Add(new WizardUnitOption(
                    unit.Id,
                    unit.Name,
                    unit.Factor,
                    availableInUnit,
                    0m));
            }

            btn.SelectedUnit = btn.AvailableUnits.FirstOrDefault(u => u.Available > 0) 
                            ?? btn.AvailableUnits.FirstOrDefault();

            result.Add(btn);
        }
        return result;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmLoadVehicle))]
    private async Task WizardConfirmLoadVehicleAsync()
    {
        var lines = WizardProducts.Where(p => p.Quantity > 0).ToList();
        if (lines.Count == 0)          { WizardError = "اختر صنفاً واحداً على الأقل."; return; }
        if (WizardSelectedWarehouse is null) { WizardError = "يرجى اختيار المستودع."; return; }

        // Validate total base quantity per lot does not exceed lot available
        foreach (var g in lines.GroupBy(p => p.LotId))
        {
            var totalBase = g.Sum(p => p.Quantity * p.UnitFactor);
            var maxBase = g.First().LotMaxBase;
            if (totalBase > maxBase)
            {
                WizardError = $"الكمية المختارة لـ '{g.First().ProductName}' تتجاوز المتاح بالمستودع.";
                return;
            }
        }

        IsBusy = true; WizardError = string.Empty; StatusMessage = "جاري تحميل السيارة...";
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var svc = new DistributorSessionService(db, new StockCountService(db), new StockTransferPostingService(db));
            var args = lines.Select(p => ((int LotId, decimal BaseQuantity))(p.LotId, p.Quantity * p.UnitFactor)).ToList();
            var roundId = _wizardCreatedSessionId;
            await svc.LoadVehicleAsync(roundId, WizardSelectedWarehouse.Id, args, userId: 1);

            // The van is loaded, so this is a real round now and must never be abandoned on cancel.
            ForgetCreatedRound();
            StatusMessage = $"✔ تم تحميل {ArabicText.Count(lines.Count, "صنف", "صنفان", "أصناف")} على سيارة {_wizardDistributorName}.";

            // Loading the van used to be the end of the wizard: the dialog closed, and the round turned
            // up in the list for the user to find again and settle as if it were a separate job. It is
            // the same job and the van is loaded, so the same dialog carries on into the settle half.
            await LoadAsync();
            var loadedRow = Rows.FirstOrDefault(r => r.Id == roundId);
            if (loadedRow is not null)
            {
                await OpenSettleWizardAsync(loadedRow);
            }
            else
            {
                WizardStep = WizardStep.None;
            }
        }
        catch (Exception ex)
        {
            _services.Report("Sessions.WizardConfirmLoadVehicleAsync", ex);
            WizardError = $"فشل التحميل: {ex.Message}";
            StatusMessage = WizardError;
        }
        finally { IsBusy = false; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // WIZARD: Settle Session (record sales + close)
    // ══════════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task OpenSettleWizardAsync(SessionRow? row)
    {
        var target = row ?? SelectedSession;
        if (target is null || !target.IsOpen) { StatusMessage = "حدد جولة مفتوحة."; return; }

        _wizardSettleSessionId     = target.Id;
        _wizardDistributorName     = target.DistributorName;
        _wizardCurrentCustomerName = string.Empty;
        _wizardTotalSales          = 0m;
        WizardInvoiceCount = 0;
        WizardTotalSalesText = "0.00 ج.م";
        WizardError = string.Empty;
        WizardCustomerSearch = string.Empty;
        SetCustomerResults([]);
        WizardSelectedCustomer = null;
        WizardIsNewCustomerVisible = false;
        WizardNewCustomerName = WizardNewCustomerPhone = WizardNewCustomerAddress = WizardNewCustomerError = string.Empty;
        WizardSessionSales.Clear();

        IsBusy = true;
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var distributor = await db.Distributors.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == target.DistributorId);
            _wizardVehicleLocationId = distributor?.StockLocationId ?? 0;
            _wizardDistributorCashAccountId = distributor?.CashAccountId ?? 0;

            var sessionPayments = await db.Payments.AsNoTracking()
                .Where(p => p.DistributorSessionId == target.Id && p.Status == DocumentStatus.Posted && p.Direction == PaymentDirection.In)
                .ToListAsync();

            var cashSales = sessionPayments.Where(p => p.SaleId != null).Sum(p => p.Amount);
            var debtCol   = sessionPayments.Where(p => p.SaleId == null).Sum(p => p.Amount);
            var expected  = Rounding.Amount(cashSales + debtCol);

            WizardCashSalesText       = $"{cashSales:N2} ج.م";
            WizardDebtCollectionsText = $"{debtCol:N2} ج.م";
            WizardExpectedCashText    = $"{expected:N2} ج.م";
            WizardCountedCash         = expected > 0 ? $"{expected:N2}" : "0.00";

            await RefreshWizardSessionSalesAsync(db);
        }
        catch (Exception ex)
        {
            _services.Report("Sessions.OpenSettleWizardAsync", ex);
            WizardError = $"خطأ: {ex.Message}";
        }
        finally { IsBusy = false; }

        WizardStep = WizardStep.Settle_Customer;
    }

    partial void OnWizardCustomerSearchChanged(string value)
    {
        if (_wizardCustomerSearchIsProgrammatic) return;
        if (string.IsNullOrWhiteSpace(value)) { SetCustomerResults([]); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
                var results = await db.Parties.AsNoTracking()
                    .Where(p => p.IsCustomer && p.Name.Contains(value)).Take(8).ToListAsync();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SetCustomerResults(results.Select(
                        p => new CustomerLookupItem(p.Id, p.Name, p.Phone ?? string.Empty)));
                });
            }
            catch (Exception ex) { _services.Report("Sessions.WizardCustomerSearch", ex); }
        });
    }

    private void SetCustomerResults(IEnumerable<CustomerLookupItem> items)
    {
        WizardCustomerResults.Clear();
        foreach (var item in items) WizardCustomerResults.Add(item);
        OnPropertyChanged(nameof(HasCustomerResults));
    }

    [RelayCommand]
    private void WizardSelectCustomer(CustomerLookupItem? item)
    {
        if (item is null) return;
        WizardSelectedCustomer = item;
        _wizardCustomerSearchIsProgrammatic = true;
        try { WizardCustomerSearch = item.Name; }
        finally { _wizardCustomerSearchIsProgrammatic = false; }
        SetCustomerResults([]);
        WizardIsNewCustomerVisible = false;
    }

    /// <summary>Releases the picked customer so the next invoice can go to someone else.</summary>
    [RelayCommand]
    private void WizardClearCustomer()
    {
        WizardSelectedCustomer = null;
        WizardCustomerSearch   = string.Empty;
        SetCustomerResults([]);
    }

    [RelayCommand]
    private void WizardShowNewCustomer()
    {
        WizardIsNewCustomerVisible = true;
        WizardNewCustomerName = WizardCustomerSearch;
        WizardNewCustomerPhone = WizardNewCustomerAddress = WizardNewCustomerError = string.Empty;
    }

    [RelayCommand]
    private void WizardHideNewCustomer() { WizardIsNewCustomerVisible = false; WizardNewCustomerError = string.Empty; }

    [RelayCommand]
    private async Task WizardCreateCustomerAsync()
    {
        if (string.IsNullOrWhiteSpace(WizardNewCustomerName)) { WizardNewCustomerError = "الاسم مطلوب."; return; }
        IsBusy = true; WizardNewCustomerError = string.Empty;
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var count = await db.Parties.CountAsync() + 1;
            var party = new Party
            {
                Code = $"DIST-{count:D5}", Name = WizardNewCustomerName.Trim(),
                IsCustomer = true, IsSupplier = false, CustomerType = CustomerType.Other,
                Phone   = string.IsNullOrWhiteSpace(WizardNewCustomerPhone)   ? null : WizardNewCustomerPhone.Trim(),
                Address = string.IsNullOrWhiteSpace(WizardNewCustomerAddress) ? null : WizardNewCustomerAddress.Trim(),
                Status = PartyStatus.Pending, CreditLimit = 0m, CreatedBy = 1, CreatedAt = DateTime.UtcNow,
            };
            db.Parties.Add(party);
            await db.SaveChangesAsync();
            WizardSelectedCustomer   = new CustomerLookupItem(party.Id, party.Name, party.Phone ?? string.Empty);
            WizardCustomerSearch     = party.Name;
            WizardIsNewCustomerVisible = false;
            StatusMessage = $"تم إنشاء العميل '{party.Name}'.";
        }
        catch (Exception ex)
        {
            _services.Report("Sessions.WizardCreateCustomerAsync", ex);
            WizardNewCustomerError = $"فشل: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Confirmed customer → load vehicle stock → go to product step.</summary>
    [RelayCommand(CanExecute = nameof(CanNextSettleToProducts))]
    private async Task WizardNext_SettleToProductsAsync()
    {
        if (WizardSelectedCustomer is null) { WizardError = "يرجى اختيار العميل."; return; }
        _wizardCurrentCustomerName = WizardSelectedCustomer.Name;
        OnPropertyChanged(nameof(WizardTitle));
        WizardError = string.Empty;

        IsBusy = true;
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var items = await BuildVehicleProductButtonsAsync(db);
            WizardProducts.Clear();
            foreach (var i in items) AddWizardProduct(i);

            // Fetch customer current balance
            var balance = await db.PartyAccountTransactions
                .Where(t => t.PartyId == WizardSelectedCustomer.Id)
                .Select(t => (decimal?)t.Amount)
                .SumAsync() ?? 0m;
            WizardCustomerPreviousBalance = balance;
            WizardCustomerPreviousBalanceText = balance switch
            {
                > 0m => $"{balance:N2} ج.م (مديونية)",
                < 0m => $"{(-balance):N2} ج.م (له رصيد)",
                _    => "0.00 ج.م",
            };

            // Reset payment and collection inputs for current customer
            WizardPaymentMode = "Cash";
            WizardPaidAmount = 0m;
            WizardPaidAmountText = "0.00";
            WizardDebtCollectionAmount = 0m;
            WizardDebtCollectionText = "0.00";
            NotifyPaymentCalculations();

            WizardStep = WizardStep.Settle_Products;
        }
        catch (Exception ex)
        {
            _services.Report("Sessions.WizardNext_SettleToProductsAsync", ex);
            WizardError = $"خطأ: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private async Task<List<WizardProductButton>> BuildVehicleProductButtonsAsync(DaftarContext db)
    {
        if (_wizardVehicleLocationId == 0) return [];
        var balances = await db.StockBalances.AsNoTracking()
            .Include(b => b.Lot!).ThenInclude(l => l.Product!).ThenInclude(p => p.Units)
            .Where(b => b.LocationId == _wizardVehicleLocationId && b.Quantity > 0)
            .OrderBy(b => b.Lot!.Product!.Name).ToListAsync();

        var result = new List<WizardProductButton>();
        foreach (var b in balances)
        {
            var product = b.Lot?.Product;
            if (product is null) continue;

            var activeUnits = product.Units.Where(u => u.IsActive)
                .OrderBy(u => u.IsDefaultSale ? 0 : u.IsBase ? 1 : 2)
                .ToList();
            if (activeUnits.Count == 0) continue;

            var btn = new WizardProductButton(
                b.LotId,
                product.Id,
                b.Quantity,
                product.Name);

            btn.OnUnitChanged = () => NotifyPaymentCalculations();

            foreach (var unit in activeUnits)
            {
                var availableInUnit = Math.Floor(b.Quantity / unit.Factor);

                decimal price = await db.ProductPrices.AsNoTracking()
                    .Include(p => p.PriceList)
                    .Where(p => p.ProductUnitId == unit.Id && p.PriceList!.IsDefault)
                    .Select(p => (decimal?)p.Price).FirstOrDefaultAsync() ?? 0m;

                // Fallback: if no price for this unit, estimate from base unit price × factor
                if (price == 0m && !unit.IsBase)
                {
                    var baseUnit = activeUnits.FirstOrDefault(u => u.IsBase);
                    if (baseUnit is not null)
                    {
                        var basePrice = await db.ProductPrices.AsNoTracking()
                            .Include(p => p.PriceList)
                            .Where(p => p.ProductUnitId == baseUnit.Id && p.PriceList!.IsDefault)
                            .Select(p => (decimal?)p.Price).FirstOrDefaultAsync() ?? 0m;
                        price = basePrice * unit.Factor;
                    }
                }

                btn.AvailableUnits.Add(new WizardUnitOption(
                    unit.Id,
                    unit.Name,
                    unit.Factor,
                    availableInUnit,
                    price));
            }

            btn.SelectedUnit = btn.AvailableUnits.FirstOrDefault(u => u.Available > 0)
                            ?? btn.AvailableUnits.FirstOrDefault();

            result.Add(btn);
        }
        return result;
    }

    // Product qty controls (shared for both load + sell wizards)
    [RelayCommand]
    private void WizardAddProduct(WizardProductButton? btn)
    {
        if (btn is null || btn.Quantity >= btn.Available) return;
        btn.Quantity++;
        if (WizardPaymentMode == "Cash")
        {
            WizardPaidAmount = WizardCartTotalAmount;
            WizardPaidAmountText = WizardCartTotalAmount.ToString("0.00");
        }
        NotifyPaymentCalculations();
    }

    [RelayCommand]
    private void WizardRemoveProduct(WizardProductButton? btn)
    {
        if (btn is null || btn.Quantity <= 0) return;
        btn.Quantity--;
        if (WizardPaymentMode == "Cash")
        {
            WizardPaidAmount = WizardCartTotalAmount;
            WizardPaidAmountText = WizardCartTotalAmount.ToString("0.00");
        }
        NotifyPaymentCalculations();
    }

    [RelayCommand]
    private void SetWizardPaymentCash()
    {
        WizardPaymentMode = "Cash";
        WizardPaidAmount = WizardCartTotalAmount;
        WizardPaidAmountText = WizardCartTotalAmount.ToString("0.00");
        WizardError = string.Empty;
        NotifyPaymentCalculations();
    }

    [RelayCommand]
    private void SetWizardPaymentCredit()
    {
        WizardPaymentMode = "Credit";
        WizardPaidAmount = 0m;
        WizardPaidAmountText = "0.00";
        WizardError = string.Empty;
        NotifyPaymentCalculations();
    }

    [RelayCommand]
    private void SetWizardPaymentPartial()
    {
        WizardPaymentMode = "Partial";
        WizardError = string.Empty;
        NotifyPaymentCalculations();
    }

    partial void OnWizardPaidAmountTextChanged(string value)
    {
        if (decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || decimal.TryParse(value, out parsed))
        {
            WizardPaidAmount = Math.Max(0m, parsed);
        }
        else
        {
            WizardPaidAmount = 0m;
        }
        NotifyPaymentCalculations();
    }

    partial void OnWizardDebtCollectionTextChanged(string value)
    {
        if (decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || decimal.TryParse(value, out parsed))
        {
            WizardDebtCollectionAmount = Math.Max(0m, parsed);
        }
        else
        {
            WizardDebtCollectionAmount = 0m;
        }
        NotifyPaymentCalculations();
    }

    private void NotifyPaymentCalculations()
    {
        OnPropertyChanged(nameof(WizardCartTotalAmount));
        OnPropertyChanged(nameof(WizardCartTotalText));
        OnPropertyChanged(nameof(WizardIsCashPayment));
        OnPropertyChanged(nameof(WizardIsCreditPayment));
        OnPropertyChanged(nameof(WizardIsPartialPayment));
        OnPropertyChanged(nameof(WizardRemainingCreditAmount));
        OnPropertyChanged(nameof(WizardRemainingCreditText));
        OnPropertyChanged(nameof(WizardRemainingCreditNotice));
        OnPropertyChanged(nameof(WizardTotalCashFromCustomerAmount));
        OnPropertyChanged(nameof(WizardTotalCashFromCustomerText));
        OnPropertyChanged(nameof(WizardProjectedBalanceAmount));
        OnPropertyChanged(nameof(WizardProjectedBalanceText));
        OnPropertyChanged(nameof(WizardRegisterButtonText));
        OnPropertyChanged(nameof(WizardCashBtnBackground));
        OnPropertyChanged(nameof(WizardCashBtnForeground));
        OnPropertyChanged(nameof(WizardCreditBtnBackground));
        OnPropertyChanged(nameof(WizardCreditBtnForeground));
        OnPropertyChanged(nameof(WizardPartialBtnBackground));
        OnPropertyChanged(nameof(WizardPartialBtnForeground));
    }

    /// <summary>Posts invoice + cash payment and/or debt collection for current customer.</summary>
    [RelayCommand(CanExecute = nameof(CanRegisterInvoice))]
    private async Task WizardRegisterInvoiceAsync()
    {
        if (WizardSelectedCustomer is null) { WizardError = "لا يوجد عميل محدد."; return; }
        var lines = WizardProducts.Where(p => p.Quantity > 0).ToList();
        var cartTotal = WizardCartTotalAmount;

        if (lines.Count == 0 && WizardDebtCollectionAmount <= 0m)
        {
            WizardError = "اختر صنفاً واحداً على الأقل أو أدخل مبلغ تحصيل مديونية.";
            return;
        }

        if (WizardPaidAmount < 0m)
        {
            WizardError = "المبلغ المدفوع لا يمكن أن يكون سالباً.";
            return;
        }

        if (WizardPaidAmount > cartTotal)
        {
            WizardError = $"المبلغ المدفوع ({WizardPaidAmount:N2} ج.م) يتجاوز إجمالي الفاتورة ({cartTotal:N2} ج.م).";
            return;
        }

        if (WizardDebtCollectionAmount < 0m)
        {
            WizardError = "مبلغ التحصيل لا يمكن أن يكون سالباً.";
            return;
        }

        foreach (var l in lines)
            if (l.Quantity > l.Available) { WizardError = $"الكمية لـ '{l.ProductName}' تتجاوز المتاح."; return; }

        IsBusy = true; WizardError = string.Empty; StatusMessage = "جاري تسجيل المعاملة...";

        // The scope has to outlive the try. Declared inside it, a `using` is disposed while the
        // exception unwinds -- before the catch runs -- and that closes the context, and with it the
        // transaction the catch is about to roll back.
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        TransactionScope? tx = null;
        try
        {
            tx = await TransactionScope.BeginAsync(db, CancellationToken.None);
            var today = DateOnly.FromDateTime(DateTime.Now);

            string? docNo = null;
            int? invoiceId = null;

            if (lines.Count > 0)
            {
                var priceListId = await db.PriceLists.Where(p => p.IsDefault).Select(p => (int?)p.Id).FirstOrDefaultAsync()
                               ?? await db.PriceLists.Select(p => (int?)p.Id).FirstOrDefaultAsync()
                               ?? throw new InvalidOperationException("لا توجد قائمة أسعار.");

                var sale = new Sale
                {
                    Type = SaleType.Invoice, PartyId = WizardSelectedCustomer.Id,
                    LocationId = _wizardVehicleLocationId, PriceListId = (int)priceListId,
                    DocDate = today, DistributorSessionId = _wizardSettleSessionId,
                    Notes = $"فاتورة جولة #{_wizardSettleSessionId}", CreatedBy = 1, CreatedAt = DateTime.UtcNow,
                };
                db.Sales.Add(sale);
                await db.SaveChangesAsync();

                foreach (var line in lines)
                {
                    var product = await db.Products.FirstAsync(p => p.Id == line.ProductId);
                    var unit    = await db.ProductUnits.FirstAsync(u => u.Id == line.ProductUnitId);
                    sale.AddItem(product, unit, line.Quantity, line.UnitPrice, line.UnitPrice);
                }
                await db.SaveChangesAsync();
                var result = await new SalePostingService(db).PostAsync(sale.Id, userId: 1, today);
                docNo = result.DocNo;
                invoiceId = sale.Id;

                // If customer paid cash towards this invoice, record and post payment voucher
                if (WizardPaidAmount > 0m && _wizardDistributorCashAccountId > 0)
                {
                    var invPayment = new Payment
                    {
                        Direction = PaymentDirection.In,
                        PartyId = WizardSelectedCustomer.Id,
                        CashAccountId = _wizardDistributorCashAccountId,
                        Method = PaymentMethod.Cash,
                        Amount = WizardPaidAmount,
                        SaleId = sale.Id,
                        DistributorSessionId = _wizardSettleSessionId,
                        DocDate = today,
                        Notes = $"سداد فاتورة {docNo} - جولة #{_wizardSettleSessionId}",
                        CreatedBy = 1,
                        CreatedAt = DateTime.UtcNow,
                    };
                    db.Payments.Add(invPayment);
                    await db.SaveChangesAsync();
                    await new PaymentPostingService(db).PostAsync(invPayment.Id, userId: 1, today);
                }
            }

            // If customer paid debt collection on previous balance, record and post collection payment
            if (WizardDebtCollectionAmount > 0m && _wizardDistributorCashAccountId > 0)
            {
                var colPayment = new Payment
                {
                    Direction = PaymentDirection.In,
                    PartyId = WizardSelectedCustomer.Id,
                    CashAccountId = _wizardDistributorCashAccountId,
                    Method = PaymentMethod.Cash,
                    Amount = WizardDebtCollectionAmount,
                    SaleId = null,
                    DistributorSessionId = _wizardSettleSessionId,
                    DocDate = today,
                    Notes = $"تحصيل مديونية سابقة - جولة #{_wizardSettleSessionId}",
                    CreatedBy = 1,
                    CreatedAt = DateTime.UtcNow,
                };
                db.Payments.Add(colPayment);
                await db.SaveChangesAsync();
                await new PaymentPostingService(db).PostAsync(colPayment.Id, userId: 1, today);
            }

            // After the invoice and both vouchers, so the limit is judged against what the
            // customer is left owing rather than what they were charged.
            if (invoiceId is { } posted)
                await new SalePostingService(db).EnsureCreditAllowsAsync(posted);

            await tx.CommitAsync();

            await RefreshWizardSessionSalesAsync(db);

            var paymentsAfterInvoice = await db.Payments.AsNoTracking()
                .Where(p => p.DistributorSessionId == _wizardSettleSessionId && p.Status == DocumentStatus.Posted && p.Direction == PaymentDirection.In)
                .ToListAsync();

            var currentCashSales = paymentsAfterInvoice.Where(p => p.SaleId != null).Sum(p => p.Amount);
            var currentDebtCol   = paymentsAfterInvoice.Where(p => p.SaleId == null).Sum(p => p.Amount);
            var expectedAfter    = Rounding.Amount(currentCashSales + currentDebtCol);

            WizardCashSalesText       = $"{currentCashSales:N2} ج.م";
            WizardDebtCollectionsText = $"{currentDebtCol:N2} ج.م";
            WizardExpectedCashText    = $"{expectedAfter:N2} ج.م";
            WizardCountedCash         = expectedAfter > 0 ? $"{expectedAfter:N2}" : "0.00";

            var summary = docNo is not null
                ? $"✔ تم تسجيل {docNo} ({cartTotal:N2} ج.م) | نقداً: {WizardPaidAmount:N2} | تحصيل: {WizardDebtCollectionAmount:N2}"
                : $"✔ تم تسجيل تحصيل {WizardDebtCollectionAmount:N2} ج.م من {WizardSelectedCustomer.Name}";
            StatusMessage = summary;

            // The round settles one invoice at a time, but a customer may take several in a row, so
            // the pick is kept. It used to be cleared here — which dropped the user back on an empty
            // search box and made «التالي» fail with «يرجى اختيار العميل.» mid-round. «تبديل العميل»
            // clears it on request instead.
            _wizardCurrentCustomerName = string.Empty;
            WizardPaymentMode          = "Cash";
            WizardPaidAmount           = 0m;
            WizardPaidAmountText       = "0.00";
            WizardDebtCollectionAmount = 0m;
            WizardDebtCollectionText   = "0.00";
            OnPropertyChanged(nameof(WizardTitle));
            OnPropertyChanged(nameof(WizardStepName));
            OnPropertyChanged(nameof(WizardStepText));
            WizardStep = WizardStep.Settle_Customer;
        }
        catch (Exception ex)
        {
            _services.Report("Sessions.WizardRegisterInvoiceAsync", ex);
            // Rolls the posting back on the way out of a failure, and declines once the transaction
            // has committed -- the refresh below the commit runs in this same try, so a failure
            // there used to reach this line, throw "This SqliteTransaction has completed", and
            // replace the reason the operator needed with a complaint about the transaction object.
            if (tx is not null) await tx.RollbackAsync();
            WizardError = $"فشل التسجيل: {ex.Message}";
            StatusMessage = WizardError;
        }
        finally { IsBusy = false; }
    }

    private async Task RefreshWizardSessionSalesAsync(DaftarContext db)
    {
        var sales = await db.Sales.AsNoTracking()
            .Include(s => s.Party).Include(s => s.Items)
            .Where(s => s.DistributorSessionId == _wizardSettleSessionId && s.Status == DocumentStatus.Posted)
            .OrderByDescending(s => s.PostedAt).ToListAsync();

        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.DistributorSessionId == _wizardSettleSessionId && p.Status == DocumentStatus.Posted && p.Direction == PaymentDirection.In)
            .ToListAsync();

        WizardSessionSales.Clear();
        decimal total = 0m;
        foreach (var s in sales)
        {
            total += s.Total;
            var paid = payments.Where(p => p.SaleId == s.Id).Sum(p => p.Amount);
            string kind = paid >= s.Total ? "فاتورة (نقدي)"
                        : paid == 0m     ? "فاتورة (آجل)"
                        : $"فاتورة (دفعة {paid:N0})";
            WizardSessionSales.Add(new SessionSaleRow(
                s.Id, s.DocNo ?? "-", s.Party?.Name ?? "-",
                kind, ArabicText.Count(s.Items.Count, "صنف", "صنفان", "أصناف"), s.Total, paid, s.DocDate.ToString("yyyy-MM-dd")));
        }

        var directCollections = payments.Where(p => p.SaleId == null).ToList();
        foreach (var c in directCollections)
        {
            var party = await db.Parties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == c.PartyId);
            WizardSessionSales.Add(new SessionSaleRow(
                c.Id, c.DocNo ?? "-", party?.Name ?? "-",
                "تحصيل مديونية", "-", c.Amount, c.Amount, c.DocDate.ToString("yyyy-MM-dd")));
        }

        WizardInvoiceCount   = sales.Count;
        _wizardTotalSales    = total;
        WizardTotalSalesText = $"{total:N2} ج.م";
    }

    [RelayCommand]
    private async Task WizardGoToFinishAsync()
    {
        WizardError = string.Empty;
        IsBusy = true;
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var payments = await db.Payments.AsNoTracking()
                .Where(p => p.DistributorSessionId == _wizardSettleSessionId && p.Status == DocumentStatus.Posted && p.Direction == PaymentDirection.In)
                .ToListAsync();

            var cashSales = payments.Where(p => p.SaleId != null).Sum(p => p.Amount);
            var debtCol   = payments.Where(p => p.SaleId == null).Sum(p => p.Amount);
            var credit    = Math.Max(0m, _wizardTotalSales - cashSales);

            var expectedFin = Rounding.Amount(cashSales + debtCol);
            WizardCashSalesText       = $"{cashSales:N2} ج.م";
            WizardCreditSalesText     = $"{credit:N2} ج.م";
            WizardDebtCollectionsText = $"{debtCol:N2} ج.م";
            WizardExpectedCashText    = $"{expectedFin:N2} ج.م";
            WizardCountedCash         = expectedFin > 0 ? $"{expectedFin:N2}" : "0.00";

            // Load remaining unsold stock on the vehicle
            var balances = await db.StockBalances.AsNoTracking()
                .Include(b => b.Lot!).ThenInclude(l => l.Product!).ThenInclude(p => p.Units)
                .Where(b => b.LocationId == _wizardVehicleLocationId && b.Quantity > 0)
                .ToListAsync();

            WizardUnsoldStockItems.Clear();
            foreach (var b in balances)
            {
                var prod = b.Lot?.Product;
                // Pick best display unit: IsDefaultSale → IsBase → first
                var unit = prod?.Units.Where(u => u.IsActive).FirstOrDefault(u => u.IsDefaultSale)
                        ?? prod?.Units.Where(u => u.IsActive).FirstOrDefault(u => u.IsBase)
                        ?? prod?.Units.Where(u => u.IsActive).FirstOrDefault()
                        ?? prod?.Units.FirstOrDefault();
                var factor = unit?.Factor ?? 1m;
                var displayQty = factor > 0 ? Math.Floor(b.Quantity / factor) : b.Quantity;
                WizardUnsoldStockItems.Add(new SessionUnsoldStockItem(prod?.Name ?? "-", unit?.Name ?? "وحدة", displayQty));
            }
            OnPropertyChanged(nameof(HasUnsoldStock));

            WizardStep = WizardStep.Settle_Finish;
        }
        catch (Exception ex)
        {
            _services.Report("Sessions.WizardGoToFinishAsync", ex);
            WizardError = $"خطأ: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task WizardConfirmSettlementAsync()
    {
        if (!decimal.TryParse(WizardCountedCash, out var counted))
        { WizardError = "يرجى إدخال مبلغ صحيح."; return; }

        IsBusy = true; WizardError = string.Empty; StatusMessage = "جاري إغلاق الجولة وتفريغ البضاعة المتبقية للمستودع...";
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var svc = new DistributorSessionService(db, new StockCountService(db), new StockTransferPostingService(db));
            
            // 1. Settle session
            var result = await svc.SettleAsync(_wizardSettleSessionId, counted, countedQuantities: null, userId: 1);

            // 2. Automatically return unsold stock from vehicle back to warehouse
            var warehouseId = await db.StockTransfers
                .Where(t => t.DistributorSessionId == _wizardSettleSessionId && t.ToLocationId == _wizardVehicleLocationId)
                .OrderByDescending(t => t.Id)
                .Select(t => (int?)t.FromLocationId)
                .FirstOrDefaultAsync()
                ?? await db.StockLocations.Where(l => l.Type == StockLocationType.Warehouse && l.IsActive)
                    .Select(l => (int?)l.Id).FirstOrDefaultAsync();

            string unloadMsg = string.Empty;
            if (warehouseId.HasValue && warehouseId.Value > 0)
            {
                var unloadResult = await svc.UnloadVehicleAsync(_wizardSettleSessionId, warehouseId.Value, lines: null, userId: 1);
                if (unloadResult is not null)
                {
                    unloadMsg = $" | تم إرجاع البضاعة المتبقية ({unloadResult.DocNo}) للمستودع";
                }
            }

            WizardStep = WizardStep.None;
            StatusMessage = $"✔ تمت تسوية الجولة #{result.SessionId} | مبيعات: {result.SalesTotal:N2} ج.م | عمولة: {result.CommissionAmount:N2} ج.م{unloadMsg}";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _services.Report("Sessions.WizardConfirmSettlementAsync", ex);
            WizardError = $"فشل الإغلاق: {ex.Message}";
            StatusMessage = WizardError;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task ViewSessionDetailsAsync(SessionRow? row)
    {
        var target = row ?? SelectedSession;
        if (target is null)
        {
            StatusMessage = "اختر جولة لعرض تفاصيلها.";
            return;
        }

        IsBusy = true;
        StatusMessage = "جاري تحميل تفاصيل الجولة...";
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var session = await db.DistributorSessions.AsNoTracking()
                .Include(s => s.Distributor)
                .FirstOrDefaultAsync(s => s.Id == target.Id);
            if (session is null) return;

            var sales = await db.Sales.AsNoTracking()
                .Include(s => s.Party)
                .Include(s => s.Items).ThenInclude(i => i.Product)
                .Include(s => s.Items).ThenInclude(i => i.ProductUnit)
                .Where(s => s.DistributorSessionId == session.Id && s.Status == DocumentStatus.Posted)
                .OrderByDescending(s => s.PostedAt)
                .ToListAsync();

            var payments = await db.Payments.AsNoTracking()
                .Include(p => p.Party)
                .Where(p => p.DistributorSessionId == session.Id && p.Status == DocumentStatus.Posted && p.Direction == PaymentDirection.In)
                .OrderByDescending(p => p.PostedAt)
                .ToListAsync();

            var transfers = await db.StockTransfers.AsNoTracking()
                .Include(t => t.FromLocation)
                .Include(t => t.ToLocation)
                .Include(t => t.Items).ThenInclude(i => i.Lot)!.ThenInclude(l => l!.Product)
                .Where(t => t.DistributorSessionId == session.Id && t.Status == DocumentStatus.Posted)
                .OrderBy(t => t.PostedAt)
                .ToListAsync();

            var invoiceRows = new List<SessionInvoiceDetailRow>();
            decimal totalSales = 0m, cashFromSales = 0m;

            foreach (var sale in sales)
            {
                totalSales += sale.Total;
                var paidForSale = payments.Where(p => p.SaleId == sale.Id).Sum(p => p.Amount);
                cashFromSales += paidForSale;
                var remaining = Math.Max(0m, sale.Total - paidForSale);

                string paymentType = paidForSale >= sale.Total ? "نقدي بالكامل"
                                   : paidForSale == 0m         ? "آجل بالكامل"
                                   : $"دفعة جزئية ({paidForSale:N2})";

                var lines = sale.Items.Select(i => new SessionInvoiceLineRow(
                    i.Product?.Name ?? "-",
                    i.ProductUnit?.Name ?? "وحدة",
                    i.Quantity,
                    i.UnitPrice,
                    i.LineTotal)).ToList();

                string itemsSummary = string.Join("، ", lines.Take(2).Select(l => $"{l.ProductName} ({l.Quantity:N0})"));
                if (lines.Count > 2) itemsSummary += $" +{ArabicText.Count(lines.Count - 2, "صنف آخر", "صنفان آخران", "أصناف أخرى")}";

                invoiceRows.Add(new SessionInvoiceDetailRow(
                    sale.Id,
                    sale.DocNo ?? "-",
                    sale.Party?.Name ?? "-",
                    paymentType,
                    itemsSummary,
                    sale.Total,
                    paidForSale,
                    remaining,
                    sale.DocDate.ToString("yyyy-MM-dd"),
                    lines));
            }

            var debtColRows = new List<SessionDebtCollectionRow>();
            decimal debtCollections = 0m;
            foreach (var col in payments.Where(p => p.SaleId == null))
            {
                debtCollections += col.Amount;
                debtColRows.Add(new SessionDebtCollectionRow(
                    col.Id,
                    col.DocNo ?? "-",
                    col.Party?.Name ?? "-",
                    col.Amount,
                    col.DocDate.ToString("yyyy-MM-dd")));
            }

            var stockMovementRows = new List<SessionStockMovementRow>();
            foreach (var tr in transfers)
            {
                bool isLoad = tr.ToLocation?.Type == StockLocationType.Vehicle;
                string typeLabel = isLoad ? "تحميل سيارة" : "مرتجع مستودع";
                var summary = string.Join("، ", tr.Items.Take(3).Select(i => $"{i.Lot?.Product?.Name} ({i.BaseQuantity:N0})"));
                if (tr.Items.Count > 3) summary += $" +{tr.Items.Count - 3} أخرى";

                stockMovementRows.Add(new SessionStockMovementRow(
                    tr.DocNo ?? "-",
                    typeLabel,
                    tr.FromLocation?.Name ?? "-",
                    tr.ToLocation?.Name ?? "-",
                    summary,
                    tr.DocDate.ToString("yyyy-MM-dd")));
            }

            var creditSales = Math.Max(0m, totalSales - cashFromSales);
            var totalCashReceived = cashFromSales + debtCollections;

            SelectedSessionDetails = new SessionDetailsViewData(
                session.Id,
                session.Distributor?.Name ?? target.DistributorName,
                session.IsOpen ? "مفتوحة" : "مغلقة",
                session.OpenedAt.ToString("yyyy-MM-dd HH:mm"),
                session.ClosedAt.HasValue ? session.ClosedAt.Value.ToString("yyyy-MM-dd HH:mm") : "-",
                totalSales,
                cashFromSales,
                creditSales,
                debtCollections,
                totalCashReceived,
                session.CommissionAmount ?? 0m,
                session.IsOpen ? totalCashReceived : (session.ExpectedCash ?? totalCashReceived),
                session.CountedCash ?? 0m,
                session.CashDifference ?? 0m,
                invoiceRows,
                debtColRows,
                stockMovementRows);

            SelectedInvoiceInDetails = invoiceRows.FirstOrDefault();
            IsSessionDetailsDialogVisible = true;
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _services.Report("Sessions.ViewSessionDetailsAsync", ex);
            StatusMessage = $"خطأ في عرض التفاصيل: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void CloseSessionDetailsDialog()
    {
        IsSessionDetailsDialogVisible = false;
        SelectedSessionDetails = null;
        SelectedInvoiceInDetails = null;
    }

    [RelayCommand]
    private void WizardBack()
    {
        WizardError = string.Empty;
        WizardStep = WizardStep switch
        {
            WizardStep.Open_LoadVehicle => WizardStep.Open_Distributor,
            WizardStep.Settle_Products  => WizardStep.Settle_Customer,
            WizardStep.Settle_Finish    => WizardStep.Settle_Customer,
            _ => WizardStep.None,
        };
    }

    /// <summary>
    /// Closes the wizard and drops the round it opened, if that round is still unused. The dialog
    /// closes first so cancel is instant; the cleanup runs behind it.
    /// </summary>
    [RelayCommand]
    private async Task WizardCancelAsync()
    {
        WizardStep = WizardStep.None;
        await AbandonCreatedRoundAsync();
    }
}
