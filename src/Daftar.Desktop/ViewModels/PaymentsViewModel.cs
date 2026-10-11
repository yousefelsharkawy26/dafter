using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Domain.Finance;
using Daftar.Domain.Parties;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Daftar.Desktop.Diagnostics;

namespace Daftar.ViewModels;

public sealed record TreasuryAccountCard(
    int Id,
    string Name,
    CashAccountType Type,
    decimal Balance,
    string BalanceText,
    string TypeLabel);

public sealed record MovementKindOption(CashTransactionKind Value, string Label);

public sealed record ExpenseCategoryOption(ExpenseCategory Value, string Label);

public sealed record VoucherRow(
    int Id,
    string DocNo,
    string Party,
    string Account,
    PaymentDirection Direction,
    PaymentMethod Method,
    decimal Amount,
    DateOnly DocDate,
    bool IsPosted,
    string? Reference = null,
    string? Notes = null)
{
    public string DirectionLabel => Direction == PaymentDirection.In ? "تحصيل" : "دفع";
    public string MethodLabel => Method.Label();
    public decimal SignedAmount => Direction == PaymentDirection.In ? Amount : -Amount;
    public string AmountText => (Direction == PaymentDirection.In ? $"+{Amount:N2}" : $"-{Amount:N2}") + " ج.م";
    public bool IsInflow => Direction == PaymentDirection.In;
}

public sealed record DirectionOption(PaymentDirection Value, string Label);

public partial class PaymentsViewModel(IServiceProvider services) : ObservableObject
{
    private readonly IServiceProvider _services = services;
    private decimal _exactPartyBalance;

    // --- Floating Action Dialog State ---
    [ObservableProperty] private int _activeFormTab; // 0 = سند طرف, 1 = مصروف/إيداع, 2 = تحويل بين الخزائن
    [ObservableProperty] private bool _isActionDialogOpen;

    public bool IsVouchersTabActive => ActiveFormTab == 0;
    public bool IsMovementsTabActive => ActiveFormTab == 1;
    public bool IsTransfersTabActive => ActiveFormTab == 2;

    public string ActionDialogTitle => ActiveFormTab switch
    {
        0 => IsInflowDirection ? "سند تحصيل نقدية / محفظة من عميل (وارد)" : "سند صرف نقدية / محفظة لمورد (صادر)",
        1 => "تسجيل حركة نقدية / مصروف مباشر في الخزينة",
        2 => "تحويل مالي بين الخزائن والحسابات",
        _ => "عملية مالية"
    };

    partial void OnActiveFormTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsVouchersTabActive));
        OnPropertyChanged(nameof(IsMovementsTabActive));
        OnPropertyChanged(nameof(IsTransfersTabActive));
        OnPropertyChanged(nameof(ActionDialogTitle));
    }

    [RelayCommand]
    public void OpenVoucherDialog()
    {
        ActiveFormTab = 0;
        IsActionDialogOpen = true;
        ApplyPartyFilter();
        OnPropertyChanged(nameof(ActionDialogTitle));
    }

    [RelayCommand]
    public void OpenMovementDialog()
    {
        ActiveFormTab = 1;
        IsActionDialogOpen = true;
        OnPropertyChanged(nameof(ActionDialogTitle));
    }

    [RelayCommand]
    public void OpenTransferDialog()
    {
        ActiveFormTab = 2;
        IsActionDialogOpen = true;
        OnPropertyChanged(nameof(ActionDialogTitle));
    }

    [RelayCommand]
    public void CloseActionDialog()
    {
        IsActionDialogOpen = false;
    }

    [RelayCommand] public void ShowVouchersTab() => OpenVoucherDialog();
    [RelayCommand] public void ShowMovementsTab() => OpenMovementDialog();
    [RelayCommand] public void ShowTransfersTab() => OpenTransferDialog();

    // --- KPIs & Liquidity ---
    [ObservableProperty] private decimal _totalLiquidity;
    [ObservableProperty] private decimal _cashDrawerBalance;
    [ObservableProperty] private decimal _walletsBalance;
    [ObservableProperty] private decimal _todayReceiptsTotal;
    [ObservableProperty] private decimal _todayPaymentsTotal;
    [ObservableProperty] private decimal _todayNetFlow;
    [ObservableProperty] private int _todayReceiptsCount;
    [ObservableProperty] private int _todayPaymentsCount;

    public ObservableCollection<TreasuryAccountCard> TreasuryAccounts { get; } = [];

    // --- Party Voucher Form ---
    // Master list (all active parties) — sliced by direction before display.
    public ObservableCollection<PartyOption> Parties { get; } = [];
    // What the ComboBox actually binds to: Customers when In, Suppliers when Out.
    public ObservableCollection<PartyOption> FilteredParties { get; } = [];
    [ObservableProperty] private string _partySearchText = string.Empty;

    [ObservableProperty] private DirectionOption? _selectedDirection;
    [ObservableProperty] private PartyOption? _party;
    [ObservableProperty] private string _amount = string.Empty;
    [ObservableProperty] private string _reference = string.Empty;
    [ObservableProperty] private MethodOption? _selectedMethod;
    [ObservableProperty] private AccountRow? _account;
    [ObservableProperty] private string _status = "اختر الطرف والمبلغ";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _canPost;
    [ObservableProperty] private string _outstanding = string.Empty;

    public IReadOnlyList<DirectionOption> Directions { get; } =
    [
        new(PaymentDirection.In, "تحصيل من عميل (وارد للخزينة)"),
        new(PaymentDirection.Out, "سداد لمورد (صادر من الخزينة)"),
    ];

    public IReadOnlyList<MethodOption> Methods { get; } =
    [
        new(PaymentMethod.Cash, "كاش (الخزينة النقدية)"),
        new(PaymentMethod.Wallet, "محفظة إلكترونية (فودافون كاش / انستاباي)"),
    ];

    public bool IsCashMethod => SelectedMethod?.Value == PaymentMethod.Cash;
    public bool IsWalletMethod => SelectedMethod?.Value == PaymentMethod.Wallet;

    public bool IsInflowDirection => SelectedDirection?.Value == PaymentDirection.In;
    public bool IsOutflowDirection => SelectedDirection?.Value == PaymentDirection.Out;

    public bool IsVodafoneCashActive => IsWalletMethod && Account is not null && (Account.Name.Contains("فودافون") || Account.Name.Contains("كاش"));
    public bool IsInstapayActive => IsWalletMethod && Account is not null && (Account.Name.Contains("انستاباي") || Account.Name.Contains("InstaPay"));

    [RelayCommand]
    public void SelectCash()
    {
        SelectedMethod = Methods.FirstOrDefault(m => m.Value == PaymentMethod.Cash);
    }

    [RelayCommand]
    public void SelectCashMethod() => SelectCash();

    [RelayCommand]
    public void SelectWalletMethod()
    {
        SelectedMethod = Methods.FirstOrDefault(m => m.Value == PaymentMethod.Wallet);
    }

    [RelayCommand]
    public void SelectInflowDirection()
    {
        SelectedDirection = Directions.FirstOrDefault(d => d.Value == PaymentDirection.In);
    }

    [RelayCommand]
    public void SelectOutflowDirection()
    {
        SelectedDirection = Directions.FirstOrDefault(d => d.Value == PaymentDirection.Out);
    }

    [RelayCommand]
    public async Task SelectVodafoneCashAsync()
    {
        SelectedMethod = Methods.FirstOrDefault(m => m.Value == PaymentMethod.Wallet);
        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        await ReloadAccountsAsync(ctx);
        var target = Accounts.FirstOrDefault(a => a.Name.Contains("فودافون") || a.Name.Contains("كاش"));
        if (target is not null) Account = target;
        Recalculate();
    }

    [RelayCommand]
    public async Task SelectInstapayAsync()
    {
        SelectedMethod = Methods.FirstOrDefault(m => m.Value == PaymentMethod.Wallet);
        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        await ReloadAccountsAsync(ctx);
        var target = Accounts.FirstOrDefault(a => a.Name.Contains("انستاباي") || a.Name.Contains("InstaPay"));
        if (target is not null) Account = target;
        Recalculate();
    }

    public ObservableCollection<AccountRow> Accounts { get; } = [];
    public ObservableCollection<AccountRow> AllAccounts { get; } = [];

    private PaymentDirection DirectionValue => SelectedDirection?.Value ?? PaymentDirection.In;
    private PaymentMethod MethodValue => SelectedMethod?.Value ?? PaymentMethod.Cash;

    // --- Direct Cash Movement / Expense Form ---
    public IReadOnlyList<MovementKindOption> MovementKinds { get; } =
    [
        new(CashTransactionKind.Expense, "تسجيل مصروف (خارج من الخزينة)"),
        new(CashTransactionKind.Deposit, "إيداع نقدي مباشر / تمويل (داخل)"),
        new(CashTransactionKind.Withdrawal, "مسحوبات نقدية / أرباح (خارج)"),
    ];

    public IReadOnlyList<ExpenseCategoryOption> ExpenseCategories { get; } =
    [
        new(ExpenseCategory.Utilities, "مرافق وفواتير (كهرباء / مياه / غاز)"),
        new(ExpenseCategory.Rent, "إيجار مقرات ومخازن"),
        new(ExpenseCategory.Salary, "رواتب وأجور ومكافآت"),
        new(ExpenseCategory.Fuel, "وقود ومحروقات ومواصلات"),
        new(ExpenseCategory.Maintenance, "صيانة ونظافة ومهمات"),
        new(ExpenseCategory.Other, "نثريات ومصروفات عامة أخرى"),
    ];

    [ObservableProperty] private MovementKindOption? _selectedMovementKind;
    [ObservableProperty] private ExpenseCategoryOption? _selectedExpenseCategory;
    [ObservableProperty] private AccountRow? _movementAccount;
    [ObservableProperty] private string _movementAmount = string.Empty;
    [ObservableProperty] private string _movementNotes = string.Empty;
    [ObservableProperty] private bool _canPostMovement;

    // --- Account Transfer Form ---
    [ObservableProperty] private AccountRow? _transferFromAccount;
    [ObservableProperty] private AccountRow? _transferToAccount;
    [ObservableProperty] private string _transferAmount = string.Empty;
    [ObservableProperty] private string _transferNotes = string.Empty;
    [ObservableProperty] private bool _canPostTransfer;

    // --- History & Vouchers Table ---
    public ObservableCollection<VoucherRow> Recent { get; } = [];
    public ObservableCollection<VoucherRow> FilteredRecent { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _historyFilterIndex; // 0 = الكل, 1 = مقبوضات, 2 = مدفوعات, 3 = اليوم, 4 = هذا الشهر
    [ObservableProperty] private VoucherRow? _selectedVoucher;
    [ObservableProperty] private bool _isDetailsDrawerOpen;

    public bool IsFilterAllActive => HistoryFilterIndex == 0;
    public bool IsFilterInflowActive => HistoryFilterIndex == 1;
    public bool IsFilterOutflowActive => HistoryFilterIndex == 2;
    public bool IsFilterTodayActive => HistoryFilterIndex == 3;
    public bool IsFilterMonthActive => HistoryFilterIndex == 4;

    [RelayCommand] public void FilterHistoryAll() => HistoryFilterIndex = 0;
    [RelayCommand] public void FilterHistoryInflow() => HistoryFilterIndex = 1;
    [RelayCommand] public void FilterHistoryOutflow() => HistoryFilterIndex = 2;
    [RelayCommand] public void FilterHistoryToday() => HistoryFilterIndex = 3;
    [RelayCommand] public void FilterHistoryMonth() => HistoryFilterIndex = 4;

    public PaymentsViewModel() : this(App.Services!) { }

    partial void OnSearchTextChanged(string value) => ApplyHistoryFilter();
    partial void OnHistoryFilterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsFilterAllActive));
        OnPropertyChanged(nameof(IsFilterInflowActive));
        OnPropertyChanged(nameof(IsFilterOutflowActive));
        OnPropertyChanged(nameof(IsFilterTodayActive));
        OnPropertyChanged(nameof(IsFilterMonthActive));
        ApplyHistoryFilter();
    }

    partial void OnSelectedVoucherChanged(VoucherRow? value)
    {
        if (value is not null)
            IsDetailsDrawerOpen = true;
    }

    partial void OnPartySearchTextChanged(string value) => ApplyPartyFilter();

    [RelayCommand]
    public async Task LoadAsync()
    {
        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        var keep = Party?.Id;

        Parties.Clear();
        foreach (var party in await ctx.Parties
                     .Where(p => p.Status == PartyStatus.Active && (p.IsCustomer || p.IsSupplier))
                     .OrderBy(p => p.Name)
                     .ToListAsync())
        {
            Parties.Add(new PartyOption(party.Id, party.Name, party.CustomerType,
                Balance: 0m, IsCustomer: party.IsCustomer, IsSupplier: party.IsSupplier));
        }

        ApplyPartyFilter();

        Party = keep is null ? null : Parties.FirstOrDefault(p => p.Id == keep);
        SelectedDirection ??= Directions[0];
        SelectedMethod ??= Methods.First(m => m.Value == PaymentMethod.Cash);

        SelectedMovementKind ??= MovementKinds[0];
        SelectedExpenseCategory ??= ExpenseCategories[0];

        await ReloadAccountsAsync(ctx);
        await ReloadTreasuryBalancesAsync(ctx);
        await LoadRecentAsync(ctx);
        await RefreshOutstandingAsync(ctx);
    }

    private void ApplyPartyFilter()
    {
        // Step 1: slice by direction — customers when collecting (In), suppliers when paying (Out)
        IEnumerable<PartyOption> directionSlice = DirectionValue switch
        {
            PaymentDirection.In  => Parties.Where(p => p.IsCustomer),
            PaymentDirection.Out => Parties.Where(p => p.IsSupplier),
            _                    => Parties,
        };

        // Step 2: narrow by search text
        var term = PartySearchText.Trim();
        var query = string.IsNullOrWhiteSpace(term)
            ? directionSlice
            : directionSlice.Where(p => p.Name.Contains(term, StringComparison.OrdinalIgnoreCase));

        FilteredParties.Clear();
        foreach (var p in query)
            FilteredParties.Add(p);

        // Reset Party selection if the current party is not in the new filtered list
        if (Party is not null && !FilteredParties.Any(p => p.Id == Party.Id))
            Party = null;
    }

    public async Task ReloadTreasuryBalancesAsync(DaftarContext ctx)
    {
        var accountBalances = await ctx.CashTransactions
            .GroupBy(t => t.CashAccountId)
            .Select(g => new { AccountId = g.Key, Balance = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Balance);

        var allAccounts = await ctx.CashAccounts
            .Where(a => a.IsActive)
            .OrderBy(a => a.Type)
            .ThenBy(a => a.Name)
            .ToListAsync();

        TreasuryAccounts.Clear();
        decimal totalLiquidity = 0m;
        foreach (var acc in allAccounts)
        {
            accountBalances.TryGetValue(acc.Id, out var bal);
            totalLiquidity += bal;

            var typeLabel = acc.Type switch
            {
                CashAccountType.Cash => "خزينة نقدية 💵",
                CashAccountType.Bank => "حساب بنكي 🏦",
                CashAccountType.Wallet => "محفظة إلكترونية 📱",
                CashAccountType.DistributorCustody => "عهدة موزع 🚚",
                _ => "أخرى"
            };

            TreasuryAccounts.Add(new TreasuryAccountCard(
                acc.Id,
                acc.Name,
                acc.Type,
                bal,
                $"{bal:N2} ج.م",
                typeLabel
            ));
        }
        TotalLiquidity = totalLiquidity;
        CashDrawerBalance = allAccounts
            .Where(a => a.Type == CashAccountType.Cash)
            .Sum(a => accountBalances.GetValueOrDefault(a.Id, 0m));
        WalletsBalance = allAccounts
            .Where(a => a.Type == CashAccountType.Wallet)
            .Sum(a => accountBalances.GetValueOrDefault(a.Id, 0m));

        // Today's summary
        var today = DateOnly.FromDateTime(DateTime.Today);
        var todayPayments = await ctx.Payments
            .Where(p => p.DocDate == today && p.Status == DocumentStatus.Posted)
            .ToListAsync();

        TodayReceiptsCount = todayPayments.Count(p => p.Direction == PaymentDirection.In);
        TodayReceiptsTotal = todayPayments.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.Amount);

        TodayPaymentsCount = todayPayments.Count(p => p.Direction == PaymentDirection.Out);
        TodayPaymentsTotal = todayPayments.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.Amount);

        TodayNetFlow = TodayReceiptsTotal - TodayPaymentsTotal;
    }

    private async Task ReloadAccountsAsync(DaftarContext ctx)
    {
        var allowed = MethodValue.AccountsFor();
        var keep = Account?.Id;

        var rows = await ctx.CashAccounts
            .Where(a => a.IsActive && allowed.Contains(a.Type))
            .OrderBy(a => a.Name)
            .Select(a => new AccountRow(a.Id, a.Name, a.Type))
            .ToListAsync();

        Accounts.Clear();
        foreach (var row in rows)
            Accounts.Add(row);

        Account = Account is not null && rows.Any(r => r.Id == Account.Id)
            ? Account
            : rows.FirstOrDefault();

        // Also set movement and transfer defaults
        var allRows = await ctx.CashAccounts
            .Where(a => a.IsActive)
            .OrderBy(a => a.Name)
            .Select(a => new AccountRow(a.Id, a.Name, a.Type))
            .ToListAsync();

        AllAccounts.Clear();
        foreach (var r in allRows)
            AllAccounts.Add(r);

        MovementAccount ??= allRows.FirstOrDefault();
        TransferFromAccount ??= allRows.FirstOrDefault();
        TransferToAccount ??= allRows.Skip(1).FirstOrDefault() ?? allRows.FirstOrDefault();
    }

    private async Task LoadRecentAsync(DaftarContext ctx)
    {
        Recent.Clear();

        var names = await ctx.Parties.ToDictionaryAsync(p => p.Id, p => p.Name);
        var accounts = await ctx.CashAccounts.ToDictionaryAsync(a => a.Id, a => a.Name);

        var rows = await ctx.Payments
            .AsNoTracking()
            .OrderByDescending(p => p.DocDate)
            .ThenByDescending(p => p.Id)
            .Take(100)
            .ToListAsync();

        foreach (var p in rows)
        {
            Recent.Add(new VoucherRow(
                p.Id,
                p.DocNo ?? "(مسودة)",
                names.GetValueOrDefault(p.PartyId, "?"),
                accounts.GetValueOrDefault(p.CashAccountId, "?"),
                p.Direction,
                p.Method,
                p.Amount,
                p.DocDate,
                p.Status == DocumentStatus.Posted,
                p.Reference));
        }

        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        var query = Recent.AsEnumerable();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);

        if (HistoryFilterIndex == 1) // مقبوضات فقط
            query = query.Where(v => v.Direction == PaymentDirection.In);
        else if (HistoryFilterIndex == 2) // مدفوعات فقط
            query = query.Where(v => v.Direction == PaymentDirection.Out);
        else if (HistoryFilterIndex == 3) // اليوم
            query = query.Where(v => v.DocDate == today);
        else if (HistoryFilterIndex == 4) // هذا الشهر
            query = query.Where(v => v.DocDate >= firstOfMonth);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            query = query.Where(v =>
                v.DocNo.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                v.Party.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                v.Account.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (v.Reference != null && v.Reference.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        FilteredRecent.Clear();
        foreach (var v in query)
            FilteredRecent.Add(v);
    }

    [RelayCommand]
    public void SetHistoryFilter(object? param)
    {
        if (param is int i) HistoryFilterIndex = i;
        else if (param is string s && int.TryParse(s, out var v)) HistoryFilterIndex = v;
    }

    [RelayCommand]
    public void SetActiveFormTab(object? param)
    {
        if (param is int i) ActiveFormTab = i;
        else if (param is string s && int.TryParse(s, out var v)) ActiveFormTab = v;
    }

    private async Task RefreshOutstandingAsync(DaftarContext ctx)
    {
        if (Party is null)
        {
            Outstanding = string.Empty;
            _exactPartyBalance = 0m;
            return;
        }

        var balance = (await ctx.PartyAccountTransactions
                .Where(t => t.PartyId == Party.Id)
                .ToListAsync())
            .Sum(t => t.Amount);

        _exactPartyBalance = balance;

        Outstanding = balance switch
        {
            > 0m => $"مدين لك: {balance:N2} ج.م",
            < 0m => $"أنت مدين له: {balance * -1:N2} ج.م",
            _ => "لا يوجد رصيد مستحق (الحساب متزن)",
        };
    }

    partial void OnPartyChanged(PartyOption? value) => _ = RefreshOutstandingSafeAsync();

    partial void OnSelectedDirectionChanged(DirectionOption? value)
    {
        OnPropertyChanged(nameof(IsInflowDirection));
        OnPropertyChanged(nameof(IsOutflowDirection));
        OnPropertyChanged(nameof(ActionDialogTitle));
        Status = "اختر الطرف والمبلغ";
        ApplyPartyFilter();            // refresh customer/supplier list first
        _ = RefreshOutstandingSafeAsync();
    }

    partial void OnSelectedMethodChanged(MethodOption? value)
    {
        OnPropertyChanged(nameof(IsCashMethod));
        OnPropertyChanged(nameof(IsWalletMethod));
        OnPropertyChanged(nameof(IsVodafoneCashActive));
        OnPropertyChanged(nameof(IsInstapayActive));
        _ = ReloadAccountsSafeAsync();
    }

    private async Task RefreshOutstandingSafeAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            await RefreshOutstandingAsync(scope.ServiceProvider.GetRequiredService<DaftarContext>());
            Recalculate();
        }
        catch
        {
            Outstanding = string.Empty;
        }
    }

    private async Task ReloadAccountsSafeAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            await ReloadAccountsAsync(scope.ServiceProvider.GetRequiredService<DaftarContext>());
        }
        catch
        {
            Accounts.Clear();
        }
    }

    private void Recalculate()
    {
        CanPost = Party is not null && Account is not null && TryAmount() > 0m;
        CanPostMovement = MovementAccount is not null && TryMovementAmount() > 0m;
        CanPostTransfer = TransferFromAccount is not null && TransferToAccount is not null &&
                          TransferFromAccount.Id != TransferToAccount.Id && TryTransferAmount() > 0m;
    }

    private static decimal ParseDecimal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0m;
        var clean = text.Trim()
            .Replace("ج.م", "", StringComparison.OrdinalIgnoreCase)
            .Replace("EGP", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        var sb = new System.Text.StringBuilder(clean.Length);
        foreach (var ch in clean)
        {
            if (ch is >= '٠' and <= '٩')
                sb.Append((char)('0' + (ch - '٠')));
            else if (ch is '٫' or ',')
                sb.Append('.');
            else
                sb.Append(ch);
        }
        clean = sb.ToString();

        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0m;
    }

    private decimal TryAmount() => ParseDecimal(Amount);
    private decimal TryMovementAmount() => ParseDecimal(MovementAmount);
    private decimal TryTransferAmount() => ParseDecimal(TransferAmount);

    partial void OnAmountChanged(string value) => Recalculate();
    partial void OnAccountChanged(AccountRow? value)
    {
        OnPropertyChanged(nameof(IsVodafoneCashActive));
        OnPropertyChanged(nameof(IsInstapayActive));
        Recalculate();
    }

    partial void OnMovementAmountChanged(string value) => Recalculate();
    partial void OnMovementAccountChanged(AccountRow? value) => Recalculate();

    partial void OnTransferAmountChanged(string value) => Recalculate();
    partial void OnTransferFromAccountChanged(AccountRow? value) => Recalculate();
    partial void OnTransferToAccountChanged(AccountRow? value) => Recalculate();

    [RelayCommand]
    public void FillFullOutstanding()
    {
        if (Party is null)
        {
            Status = "⚠️ اختر الطرف أولاً لتعبئة المستحق";
            return;
        }

        var owed = DirectionValue == PaymentDirection.In ? _exactPartyBalance : -_exactPartyBalance;
        if (owed > 0m)
        {
            Amount = owed.ToString("G29", CultureInfo.InvariantCulture);
            Status = $"تم تعبئة كامل المبلغ المستحق: {owed:N2} ج.م";
        }
        else if (_exactPartyBalance != 0m)
        {
            var abs = Math.Abs(_exactPartyBalance);
            Amount = abs.ToString("G29", CultureInfo.InvariantCulture);
            Status = $"تم تعبئة رصيد الحساب: {abs:N2} ج.م";
        }
        else
        {
            Status = "حساب الطرف متزن بالكامل (لا يوجد رصيد مستحق)";
        }
    }

    [RelayCommand]
    public void SelectVoucher(VoucherRow? voucher)
    {
        if (voucher is null) return;
        SelectedVoucher = voucher;
        IsDetailsDrawerOpen = true;
    }

    [RelayCommand]
    public void CloseDetailsDrawer()
    {
        IsDetailsDrawerOpen = false;
        SelectedVoucher = null;
    }

    [RelayCommand]
    public async Task PostAsync()
    {
        if (Party is null)
        {
            Status = "⚠️ يرجى اختيار العميل أو المورد أولاً";
            return;
        }

        if (Account is null)
        {
            Status = "⚠️ يرجى اختيار حساب الخزينة أو البنك المستهدف";
            return;
        }

        var amount = TryAmount();

        if (amount <= 0m)
        {
            Status = "⚠️ يرجى كتابة مبلغ أكبر من صفر للترحيل";
            return;
        }

        using (var check = _services.CreateScope())
        {
            var checkCtx = check.ServiceProvider.GetRequiredService<DaftarContext>();

            var balance = (await checkCtx.PartyAccountTransactions
                    .Where(t => t.PartyId == Party.Id)
                    .ToListAsync())
                .Sum(t => t.Amount);

            var owed = DirectionValue == PaymentDirection.In ? balance : -balance;

            if (owed > 0m && amount > owed)
            {
                Status = $"المبلغ أكبر من المستحق ({owed:N2})";
                return;
            }
        }

        Busy = true;

        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var today = DateOnly.FromDateTime(DateTime.Now);

            var payment = new Payment
            {
                Direction = DirectionValue,
                PartyId = Party.Id,
                CashAccountId = Account.Id,
                Method = MethodValue,
                Amount = amount,
                Reference = string.IsNullOrWhiteSpace(Reference) ? null : Reference.Trim(),
                DocDate = today,
            };

            ctx.Payments.Add(payment);
            await ctx.SaveChangesAsync();

            var result = await new PaymentPostingService(ctx)
                .PostAsync(payment.Id, userId: 1, today);

            Status = DirectionValue == PaymentDirection.In
                ? $"تم تحصيل {amount:N2} ج.م بنجاح — {result.DocNo}"
                : $"تم صرف {amount:N2} ج.م بنجاح — {result.DocNo}";

            Amount = string.Empty;
            Reference = string.Empty;
            IsActionDialogOpen = false;
            Recalculate();

            await ReloadTreasuryBalancesAsync(ctx);
            await LoadRecentAsync(ctx);
            await RefreshOutstandingAsync(ctx);
        }
        catch (Exception ex)
        {
            _services.Report("Payments.PostAsync", ex);
            Status = $"فشل الحفظ: {ex.Message}";
        }
        finally
        {
            Busy = false;
            Recalculate();
        }
    }

    [RelayCommand]
    public async Task PostMovementAsync()
    {
        if (MovementAccount is null)
        {
            Status = "⚠️ يرجى اختيار حساب الخزينة أو البنك للمصروف / الحركة";
            return;
        }

        var amount = TryMovementAmount();
        if (amount <= 0m)
        {
            Status = "⚠️ يرجى كتابة مبلغ أكبر من صفر للمصروف / الحركة";
            return;
        }

        Busy = true;
        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var today = DateOnly.FromDateTime(DateTime.Now);

            var kind = SelectedMovementKind?.Value ?? CashTransactionKind.Expense;
            var category = kind == CashTransactionKind.Expense ? SelectedExpenseCategory?.Value : null;

            await new PaymentPostingService(ctx).RecordCashMovementAsync(
                MovementAccount.Id,
                kind,
                amount,
                userId: 1,
                today,
                category: category,
                note: string.IsNullOrWhiteSpace(MovementNotes) ? null : MovementNotes.Trim());

            Status = $"تم تسجيل الحركة بقيمة {amount:N2} ج.م في {MovementAccount.Name} بنجاح ✓";
            MovementAmount = string.Empty;
            MovementNotes = string.Empty;
            IsActionDialogOpen = false;

            await ReloadTreasuryBalancesAsync(ctx);
            await LoadRecentAsync(ctx);
        }
        catch (Exception ex)
        {
            _services.Report("Payments.PostMovementAsync", ex);
            Status = $"فشل تسجيل المصروف: {ex.Message}";
        }
        finally
        {
            Busy = false;
            Recalculate();
        }
    }

    [RelayCommand]
    public async Task PostTransferAsync()
    {
        if (TransferFromAccount is null)
        {
            Status = "⚠️ يرجى تحديد حساب المصدر (المحول منه)";
            return;
        }

        if (TransferToAccount is null)
        {
            Status = "⚠️ يرجى تحديد حساب الوجهة (المحول إليه)";
            return;
        }

        if (TransferFromAccount.Id == TransferToAccount.Id)
        {
            Status = "⚠️ لا يمكن التحويل لنفس الحساب، اختر حسابين مختلفين";
            return;
        }

        var amount = TryTransferAmount();
        if (amount <= 0m)
        {
            Status = "⚠️ يرجى كتابة مبلغ أكبر من صفر للتحويل المالي";
            return;
        }

        Busy = true;
        try
        {
            using var scope = _services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
            var today = DateOnly.FromDateTime(DateTime.Now);

            await new PaymentPostingService(ctx).TransferBetweenAccountsAsync(
                TransferFromAccount.Id,
                TransferToAccount.Id,
                amount,
                userId: 1,
                today);

            Status = $"تم تحويل {amount:N2} ج.م من {TransferFromAccount.Name} إلى {TransferToAccount.Name} بنجاح ✓";
            TransferAmount = string.Empty;
            TransferNotes = string.Empty;
            IsActionDialogOpen = false;

            await ReloadTreasuryBalancesAsync(ctx);
            await LoadRecentAsync(ctx);
        }
        catch (Exception ex)
        {
            _services.Report("Payments.PostTransferAsync", ex);
            Status = $"فشل التحويل: {ex.Message}";
        }
        finally
        {
            Busy = false;
            Recalculate();
        }
    }
}
