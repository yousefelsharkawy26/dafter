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

/// <summary>
/// المقبوضات: collecting from a customer or paying a supplier, either against a specific invoice
/// or as a lump sum against whatever that party owes.
///
/// This is the screen that makes a partial payment finishable. Money taken at the till settles a
/// sale on the spot; anything left after that is settled here later, which is the normal way a
/// distributor's van collects.
/// </summary>
public partial class PaymentsViewModel(IServiceProvider services) : ObservableObject
{
    private readonly IServiceProvider _services = services;

    public ObservableCollection<PartyOption> Parties { get; } = [];

    public ObservableCollection<VoucherRow> Recent { get; } = [];

    /// <summary>Which way the money moves. Buying from a party is a payment out.</summary>
    [ObservableProperty]
    private DirectionOption? _selectedDirection;

    [ObservableProperty]
    private PartyOption? _party;

    [ObservableProperty]
    private string _amount = string.Empty;

    [ObservableProperty]
    private string _reference = string.Empty;

    [ObservableProperty]
    private MethodOption? _selectedMethod;

    [ObservableProperty]
    private AccountRow? _account;

    [ObservableProperty]
    private string _status = "اختار الطرف والمبلغ";

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private bool _canPost;

    /// <summary>What the chosen party currently owes us, or that we owe them.</summary>
    [ObservableProperty]
    private string _outstanding = string.Empty;

    public IReadOnlyList<DirectionOption> Directions { get; } =
    [
        new(PaymentDirection.In, "تحصيل من عميل (داخل ليك)"),
        new(PaymentDirection.Out, "دفع لمورد (خارج منك)"),
    ];

    public IReadOnlyList<MethodOption> Methods { get; } =
        Enum.GetValues<PaymentMethod>().Select(m => new MethodOption(m, m.Label())).ToList();

    public ObservableCollection<AccountRow> Accounts { get; } = [];

    /// <summary>The chosen direction as a plain enum, for the domain and the guards.</summary>
    private PaymentDirection DirectionValue => SelectedDirection?.Value ?? PaymentDirection.In;

    /// <summary>The chosen method as a plain enum.</summary>
    private PaymentMethod MethodValue => SelectedMethod?.Value ?? PaymentMethod.Cash;

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
            Parties.Add(new PartyOption(party.Id, party.Name, null));
        }

        // Deliberately not defaulted: collecting money from the wrong customer because the first
        // row happened to be selected is not a recoverable mistake.
        Party = keep is null ? null : Parties.FirstOrDefault(p => p.Id == keep);
        SelectedDirection ??= Directions[0];

        // Cash is the common case and a safe default; the account list follows from it.
        SelectedMethod ??= Methods.First(m => m.Value == PaymentMethod.Cash);

        await ReloadAccountsAsync(ctx);
        await LoadRecentAsync(ctx);
        await RefreshOutstandingAsync(ctx);
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
    }

    private async Task LoadRecentAsync(DaftarContext ctx)
    {
        Recent.Clear();

        // Looked up up front so the projection stays a single query.
        var names = await ctx.Parties.ToDictionaryAsync(p => p.Id, p => p.Name);
        var accounts = await ctx.CashAccounts.ToDictionaryAsync(a => a.Id, a => a.Name);

        var rows = await ctx.Payments
            .AsNoTracking()
            .OrderByDescending(p => p.DocDate)
            .ThenByDescending(p => p.Id)
            .Take(60)
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
                p.Status == DocumentStatus.Posted));
        }
    }

    /// <summary>How much the chosen party still owes, on the side being paid.</summary>
    private async Task RefreshOutstandingAsync(DaftarContext ctx)
    {
        if (Party is null)
        {
            Outstanding = string.Empty;
            return;
        }

        var balance = (await ctx.PartyAccountTransactions
                .Where(t => t.PartyId == Party.Id)
                .ToListAsync())
            .Sum(t => t.Amount);

        Outstanding = balance switch
        {
            > 0m => $"مدين لك {balance:N2}",
            < 0m => $"أنت مدين له {balance * -1:N2}",
            _ => "مفيش رصيد مفتوح",
        };
    }

    partial void OnPartyChanged(PartyOption? value) => _ = RefreshOutstandingSafeAsync();

    partial void OnSelectedDirectionChanged(DirectionOption? value)
    {
        Status = "اختار الطرف والمبلغ";
        _ = RefreshOutstandingSafeAsync();
    }

    partial void OnSelectedMethodChanged(MethodOption? value) => _ = ReloadAccountsSafeAsync();

    private async Task RefreshOutstandingSafeAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            await RefreshOutstandingAsync(scope.ServiceProvider.GetRequiredService<DaftarContext>());
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

    private void Recalculate() => CanPost = Party is not null && Account is not null && TryAmount() > 0m;

    private decimal TryAmount()
        => decimal.TryParse(Amount, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;

    partial void OnAmountChanged(string value) => Recalculate();

    partial void OnAccountChanged(AccountRow? value) => Recalculate();

    [RelayCommand]
    private async Task PostAsync()
    {
        if (Party is null || Account is null)
            return;

        var amount = TryAmount();

        if (amount <= 0m)
        {
            Status = "اكتب مبلغ أكبر من صفر";
            return;
        }

        // Never collect more than is owed: the rest would be an advance, not a settlement.
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

            var result = await new Infrastructure.Services.PaymentPostingService(ctx)
                .PostAsync(payment.Id, userId: 1, today);

            Status = DirectionValue == PaymentDirection.In
                ? $"تم تحصيل {amount:N2} — {result.DocNo}"
                : $"تم دفع {amount:N2} — {result.DocNo}";

            Amount = string.Empty;
            Reference = string.Empty;
            Recalculate();

            await LoadRecentAsync(ctx);
            await RefreshOutstandingAsync(ctx);
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

public sealed record DirectionOption(PaymentDirection Value, string Label);

/// <summary>One posted or draft voucher, as shown in the history.</summary>
public sealed record VoucherRow(
    int Id,
    string DocNo,
    string Party,
    string Account,
    PaymentDirection Direction,
    PaymentMethod Method,
    decimal Amount,
    DateOnly DocDate,
    bool IsPosted)
{
    /// <summary>Which way the money moved, in words.</summary>
    public string DirectionLabel
        => Direction == PaymentDirection.In ? "تحصيل" : "دفع";

    public string MethodLabel => Method.Label();

    /// <summary>Signed for the column: money in shows positive.</summary>
    public decimal SignedAmount => Direction == PaymentDirection.In ? Amount : -Amount;

    /// <summary>Two decimals and a thousands separator, the way money is read here.</summary>
    public string AmountText => SignedAmount.ToString("N2", CultureInfo.CurrentCulture);
}
