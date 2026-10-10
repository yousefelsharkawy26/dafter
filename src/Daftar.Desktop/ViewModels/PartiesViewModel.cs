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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.ViewModels;

public sealed record PartyListItem(
    int Id,
    string Code,
    string Name,
    string? Phone,
    string? Address,
    string? TaxNumber,
    bool IsCustomer,
    bool IsSupplier,
    string RoleDisplay,
    decimal Balance,
    string BalanceText,
    string BalanceColor,
    PartyStatus Status,
    decimal CreditLimit = 0m)
{
    public string CreditLimitDisplay => CreditLimit > 0m ? $"{CreditLimit:N2} ج.م" : "نقدي فقط";
}

public sealed record PartyTransactionRow(
    int Id,
    string DateText,
    string KindDisplay,
    decimal Amount,
    string AmountText,
    string AmountColor,
    string? SourceType,
    string? Note);

public partial class PartiesViewModel(IServiceProvider services) : ObservableObject
{
    private readonly IServiceProvider _services = services;

    public ObservableCollection<PartyListItem> FilteredParties { get; } = [];
    private readonly List<PartyListItem> _allParties = [];

    public ObservableCollection<PartyTransactionRow> Transactions { get; } = [];

    [ObservableProperty]
    private PartyListItem? _selectedParty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>0 = الكل, 1 = عملاء فقط, 2 = موردين فقط</summary>
    [ObservableProperty]
    private int _filterRole;

    [RelayCommand]
    public void SetFilterRole(int role) => FilterRole = role;

    [ObservableProperty]
    private bool _isPartyDialogOpen;

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private int? _editingPartyId;

    [ObservableProperty]
    private string _dialogTitle = "إضافة طرف جديد";

    [ObservableProperty]
    private string _dialogErrorMessage = string.Empty;

    [ObservableProperty]
    private string _saveButtonText = "إضافة الطرف";

    [ObservableProperty]
    private string _formCode = string.Empty;

    [ObservableProperty]
    private string _formName = string.Empty;

    [ObservableProperty]
    private string _formPhone = string.Empty;

    [ObservableProperty]
    private string _formAddress = string.Empty;

    [ObservableProperty]
    private string _formTaxNumber = string.Empty;

    [ObservableProperty]
    private bool _formIsCustomer = true;

    [ObservableProperty]
    private bool _formIsSupplier;

    [ObservableProperty]
    private string _formCreditLimit = "0";

    // Backwards compatibility properties
    public string NewCode { get => FormCode; set => FormCode = value; }
    public string NewName { get => FormName; set => FormName = value; }
    public string NewPhone { get => FormPhone; set => FormPhone = value; }
    public string NewAddress { get => FormAddress; set => FormAddress = value; }
    public string NewTaxNumber { get => FormTaxNumber; set => FormTaxNumber = value; }
    public bool NewIsCustomer { get => FormIsCustomer; set => FormIsCustomer = value; }
    public bool NewIsSupplier { get => FormIsSupplier; set => FormIsSupplier = value; }
    public string NewCreditLimit { get => FormCreditLimit; set => FormCreditLimit = value; }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public PartiesViewModel() : this(App.Services!) { }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        await LoadPartiesAsync();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnFilterRoleChanged(int value) => ApplyFilter();

    partial void OnSelectedPartyChanged(PartyListItem? value)
    {
        if (value is not null)
            _ = LoadTransactionsAsync(value.Id);
        else
            Transactions.Clear();
    }

    [RelayCommand]
    public async Task LoadPartiesAsync()
    {
        IsBusy = true;
        StatusMessage = "جاري التحميل...";

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var parties = await db.Parties
                .AsNoTracking()
                .OrderBy(p => p.Name)
                .ToListAsync();

            var balances = await db.PartyAccountTransactions
                .AsNoTracking()
                .GroupBy(t => t.PartyId)
                .Select(g => new { PartyId = g.Key, Balance = g.Sum(t => t.Amount) })
                .ToDictionaryAsync(g => g.PartyId, g => g.Balance);

            _allParties.Clear();

            foreach (var p in parties)
            {
                var bal = balances.GetValueOrDefault(p.Id, 0m);
                var roleText = (p.IsCustomer, p.IsSupplier) switch
                {
                    (true, true) => "عميل ومورد",
                    (true, false) => "عميل",
                    (false, true) => "مورد",
                    _ => "طرف",
                };

                var (balText, balColor) = bal switch
                {
                    > 0 => ($"عليه: {bal:N2}", "#004E8C"),
                    < 0 => ($"له: {-bal:N2}", "#B83B1D"),
                    _ => ("0.00", "#555555"),
                };

                _allParties.Add(new PartyListItem(
                    p.Id,
                    p.Code,
                    p.Name,
                    p.Phone,
                    p.Address,
                    p.TaxNumber,
                    p.IsCustomer,
                    p.IsSupplier,
                    roleText,
                    bal,
                    balText,
                    balColor,
                    p.Status,
                    p.CreditLimit));
            }

            ApplyFilter();

            if (SelectedParty is null && FilteredParties.Count > 0)
                SelectedParty = FilteredParties[0];

            StatusMessage = $"إجمالي الأطراف: {_allParties.Count}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"خطأ في التحميل: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        FilteredParties.Clear();
        var search = SearchText.Trim().ToLowerInvariant();

        foreach (var p in _allParties)
        {
            if (FilterRole == 1 && !p.IsCustomer)
                continue;
            if (FilterRole == 2 && !p.IsSupplier)
                continue;

            if (!string.IsNullOrEmpty(search))
            {
                var matches = p.Name.ToLowerInvariant().Contains(search)
                              || p.Code.ToLowerInvariant().Contains(search)
                              || (p.Phone?.Contains(search) ?? false);
                if (!matches)
                    continue;
            }

            FilteredParties.Add(p);
        }
    }

    private async Task LoadTransactionsAsync(int partyId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            var rows = await db.PartyAccountTransactions
                .AsNoTracking()
                .Where(t => t.PartyId == partyId)
                .OrderByDescending(t => t.OccurredAt)
                .Take(50)
                .ToListAsync();

            Transactions.Clear();

            foreach (var r in rows)
            {
                var kindText = r.Kind switch
                {
                    PartyLedgerKind.Sale => "فاتورة بيع",
                    PartyLedgerKind.Purchase => "فاتورة شراء",
                    PartyLedgerKind.PaymentIn => "سند قبض",
                    PartyLedgerKind.PaymentOut => "سند صرف",
                    PartyLedgerKind.SaleReturn => "مرتجع بيع",
                    PartyLedgerKind.PurchaseReturn => "مرتجع شراء",
                    PartyLedgerKind.Opening => "رصيد افتتاحي",
                    PartyLedgerKind.Adjustment => "تسوية",
                    _ => r.Kind.ToString(),
                };

                var (amtText, amtColor) = r.Amount switch
                {
                    > 0 => ($"+{r.Amount:N2}", "#0078D7"),
                    < 0 => ($"{r.Amount:N2}", "#D24726"),
                    _ => ("0.00", "#666666"),
                };

                Transactions.Add(new PartyTransactionRow(
                    r.Id,
                    r.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    kindText,
                    r.Amount,
                    amtText,
                    amtColor,
                    r.SourceType,
                    r.Note));
            }
        }
        catch
        {
            // Ignore transaction load errors
        }
    }

    [RelayCommand]
    public void OpenAddPartyDialog()
    {
        IsEditMode = false;
        EditingPartyId = null;
        DialogTitle = "إضافة طرف جديد";
        SaveButtonText = "إضافة الطرف";
        DialogErrorMessage = string.Empty;

        FormCode = string.Empty;
        FormName = string.Empty;
        FormPhone = string.Empty;
        FormAddress = string.Empty;
        FormTaxNumber = string.Empty;
        FormIsCustomer = true;
        FormIsSupplier = false;
        FormCreditLimit = "0";

        IsPartyDialogOpen = true;
    }

    [RelayCommand]
    public void OpenEditPartyDialog(object? param = null)
    {
        var target = param as PartyListItem ?? SelectedParty;
        if (target is null)
        {
            StatusMessage = "يرجى تحديد طرف للتعديل";
            return;
        }

        IsEditMode = true;
        EditingPartyId = target.Id;
        DialogTitle = $"تعديل بيانات: {target.Name}";
        SaveButtonText = "حفظ التعديلات";
        DialogErrorMessage = string.Empty;

        FormCode = target.Code;
        FormName = target.Name;
        FormPhone = target.Phone ?? string.Empty;
        FormAddress = target.Address ?? string.Empty;
        FormTaxNumber = target.TaxNumber ?? string.Empty;
        FormIsCustomer = target.IsCustomer;
        FormIsSupplier = target.IsSupplier;
        FormCreditLimit = target.CreditLimit.ToString(CultureInfo.InvariantCulture);

        IsPartyDialogOpen = true;
    }

    [RelayCommand]
    public void ClosePartyDialog()
    {
        IsPartyDialogOpen = false;
        DialogErrorMessage = string.Empty;
    }

    [RelayCommand]
    public async Task SavePartyAsync()
    {
        if (string.IsNullOrWhiteSpace(FormName))
        {
            DialogErrorMessage = "يرجى كتابة اسم الطرف";
            return;
        }

        if (!FormIsCustomer && !FormIsSupplier)
        {
            DialogErrorMessage = "يجب تحديد عميل أو مورد";
            return;
        }

        decimal creditLimit = 0m;
        if (!string.IsNullOrWhiteSpace(FormCreditLimit)
            && !decimal.TryParse(FormCreditLimit, NumberStyles.Number, CultureInfo.InvariantCulture, out creditLimit))
        {
            DialogErrorMessage = "حد الائتمان غير صحيح";
            return;
        }

        IsBusy = true;
        DialogErrorMessage = string.Empty;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DaftarContext>();

            if (IsEditMode && EditingPartyId.HasValue)
            {
                var party = await db.Parties.FindAsync(EditingPartyId.Value);
                if (party is null)
                {
                    DialogErrorMessage = "الطرف غير موجود في قاعدة البيانات";
                    return;
                }

                if (!string.IsNullOrWhiteSpace(FormCode))
                {
                    party.Code = FormCode.Trim();
                }

                party.Name = FormName.Trim();
                party.Phone = string.IsNullOrWhiteSpace(FormPhone) ? null : FormPhone.Trim();
                party.Address = string.IsNullOrWhiteSpace(FormAddress) ? null : FormAddress.Trim();
                party.TaxNumber = string.IsNullOrWhiteSpace(FormTaxNumber) ? null : FormTaxNumber.Trim();
                party.IsCustomer = FormIsCustomer;
                party.IsSupplier = FormIsSupplier;
                if (party.IsCustomer && party.CustomerType is null)
                {
                    party.CustomerType = CustomerType.Individual;
                }
                else if (!party.IsCustomer)
                {
                    party.CustomerType = null;
                }

                party.CreditLimit = creditLimit;
                party.UpdatedAt = DateTime.UtcNow;
                party.UpdatedBy = 1;

                party.Validate();
                await db.SaveChangesAsync();

                StatusMessage = $"تم تحديث بيانات الطرف: {party.Name}";
                IsPartyDialogOpen = false;
                await LoadPartiesAsync();
                SelectedParty = FilteredParties.FirstOrDefault(p => p.Id == party.Id);
            }
            else
            {
                // Auto-generate code if empty
                var code = FormCode.Trim();
                if (string.IsNullOrEmpty(code))
                {
                    var seq = await db.DocumentSequences
                        .FirstOrDefaultAsync(s => s.DocType == DocumentType.Party && s.Year == 0);

                    if (seq is not null)
                    {
                        code = $"{seq.Prefix}{seq.NextNumber.ToString().PadLeft(seq.Padding, '0')}";
                        seq.NextNumber++;
                    }
                    else
                    {
                        code = $"P-{DateTime.UtcNow.Ticks % 1000000:D6}";
                    }
                }

                var party = new Party
                {
                    Code = code,
                    Name = FormName.Trim(),
                    Phone = string.IsNullOrWhiteSpace(FormPhone) ? null : FormPhone.Trim(),
                    Address = string.IsNullOrWhiteSpace(FormAddress) ? null : FormAddress.Trim(),
                    TaxNumber = string.IsNullOrWhiteSpace(FormTaxNumber) ? null : FormTaxNumber.Trim(),
                    IsCustomer = FormIsCustomer,
                    IsSupplier = FormIsSupplier,
                    CustomerType = FormIsCustomer ? CustomerType.Individual : null,
                    CreditLimit = creditLimit,
                    Status = PartyStatus.Active,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = 1,
                };

                party.Validate();

                db.Parties.Add(party);
                await db.SaveChangesAsync();

                StatusMessage = $"تمت إضافة الطرف: {party.Name}";
                IsPartyDialogOpen = false;
                await LoadPartiesAsync();
                SelectedParty = FilteredParties.FirstOrDefault(p => p.Id == party.Id);
            }
        }
        catch (Exception ex)
        {
            DialogErrorMessage = $"خطأ: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public Task AddPartyAsync() => SavePartyAsync();
}
