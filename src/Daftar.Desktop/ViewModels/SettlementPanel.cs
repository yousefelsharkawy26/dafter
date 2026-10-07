using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Daftar.Domain.Finance;
using Daftar.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.ViewModels;

/// <summary>A settlement choice with its Arabic label, so the screen needs no converter.</summary>
public sealed record SettlementOption(SettlementChoice Value, string Label);

/// <summary>A payment method with its Arabic label.</summary>
public sealed record MethodOption(PaymentMethod Value, string Label);

/// <summary>
/// The payment half of posting a document, shared by الاستلام and البيع so both behave the same
/// way: pick how much is settled now, how it was paid, and which account it landed in.
/// </summary>
public partial class SettlementPanel : ObservableObject
{
    private readonly IServiceProvider _services;

    public IReadOnlyList<SettlementOption> Choices { get; } =
    [
        new(SettlementChoice.Full, "مدفوع بالكامل"),
        new(SettlementChoice.Partial, "دفع جزئي"),
        new(SettlementChoice.Deferred, "آجل (مفيش دفع دلوقتي)"),
    ];

    public IReadOnlyList<MethodOption> Methods { get; } =
        Enum.GetValues<PaymentMethod>().Select(m => new MethodOption(m, m.Label())).ToList();

    /// <summary>Accounts available for the chosen method, refreshed when the method changes.</summary>
    public ObservableCollection<AccountRow> Accounts { get; } = [];

    [ObservableProperty]
    private SettlementOption? _choice;

    [ObservableProperty]
    private MethodOption? _method;

    [ObservableProperty]
    private AccountRow? _account;

    [ObservableProperty]
    private string _partialAmount = string.Empty;

    [ObservableProperty]
    private string _problem = string.Empty;

    public SettlementPanel(IServiceProvider services)
    {
        _services = services;

        // Seeded here rather than left null: an empty dropdown reads as a broken screen, and
        // receiving goods without paying is the common case on a wholesale run.
        Choice = Choices.First(c => c.Value == SettlementChoice.Deferred);
        Method = Methods.First(m => m.Value == PaymentMethod.Cash);
    }

    /// <summary>Shown only for a partial payment.</summary>
    public bool IsPartial => Settlement == SettlementChoice.Partial;

    /// <summary>Method and account only matter when money is actually being handed over.</summary>
    public bool IsActive => Settlement != SettlementChoice.Deferred;

    /// <summary>The choice as a plain enum, for the domain and for the rules below.</summary>
    public SettlementChoice Settlement => Choice?.Value ?? SettlementChoice.Deferred;

    /// <summary>The chosen method as a plain enum.</summary>
    public PaymentMethod MethodValue => Method?.Value ?? PaymentMethod.Cash;

    partial void OnChoiceChanged(SettlementOption? value)
    {
        OnPropertyChanged(nameof(Settlement));
        OnPropertyChanged(nameof(IsPartial));
        OnPropertyChanged(nameof(IsActive));
    }

    partial void OnMethodChanged(MethodOption? value) => _ = ReloadAccountsSafeAsync();

    /// <summary>Loads the accounts the current method can be received into.</summary>
    public async Task LoadAsync()
    {
        using var scope = _services.CreateScope();
        await FillAccountsAsync(scope.ServiceProvider.GetRequiredService<DaftarContext>());
    }

    private async Task FillAccountsAsync(DaftarContext ctx)
    {
        var allowed = MethodValue.AccountsFor();

        var rows = await ctx.CashAccounts
            .Where(a => a.IsActive && allowed.Contains(a.Type))
            .OrderBy(a => a.Name)
            .Select(a => new AccountRow(a.Id, a.Name, a.Type))
            .ToListAsync();

        Accounts.Clear();
        foreach (var row in rows)
            Accounts.Add(row);

        // Keep the chosen account when it is still allowed, otherwise fall back to the first.
        Account = Account is not null && rows.Any(r => r.Id == Account.Id)
            ? Account
            : rows.FirstOrDefault();
    }

    private async Task ReloadAccountsSafeAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            var keep = Account?.Id;

            await FillAccountsAsync(scope.ServiceProvider.GetRequiredService<DaftarContext>());

            if (keep is not null && Accounts.Any(a => a.Id == keep))
                Account = Accounts.FirstOrDefault(a => a.Id == keep);
        }
        catch
        {
            // Losing the account list must not take the screen down; the problem text covers it.
        }
    }

    /// <summary>
    /// What the current choice comes to for a document worth the given total.
    ///
    /// Pure on purpose: this runs while totals recalculate, and a screen that recomputes on every
    /// keystroke must not raise a validation error as a side effect.
    /// </summary>
    public decimal AmountFor(decimal total) => Settlement switch
    {
        SettlementChoice.Full => total,
        SettlementChoice.Partial => System.Math.Min(ParsePartial(), total),
        _ => 0m,
    };

    private decimal ParsePartial() => decimal.TryParse(
        PartialAmount,
        System.Globalization.NumberStyles.Any,
        System.Globalization.CultureInfo.InvariantCulture,
        out var parsed)
        ? parsed
        : 0m;

    /// <summary>
    /// Resolves what to pay and where, reporting what is missing in <see cref="Problem"/> rather
    /// than throwing, so the screen can say what to fix instead of failing to save.
    /// </summary>
    public bool TryResolve(decimal total, out decimal amount, out int accountId)
    {
        amount = 0m;
        accountId = 0;
        Problem = string.Empty;

        if (Settlement == SettlementChoice.Deferred)
            return true;

        amount = AmountFor(total);

        if (amount <= 0m)
        {
            Problem = total <= 0m
                ? "مفيش إجمالي يتحسب عليه دفع"
                : "اكتب مبلغ الدفع الجزئي أكبر من صفر";
            return false;
        }

        if (Account is null)
        {
            Problem = "اختار الحساب اللي الفلوس هتوصله";
            return false;
        }

        accountId = Account.Id;
        return true;
    }

    /// <summary>Writes the voucher for whatever was settled, if anything.</summary>
    public async Task ApplyAsync(
        DaftarContext ctx,
        PaymentDirection direction,
        int partyId,
        int? saleId,
        int? purchaseId,
        decimal total,
        DateOnly today,
        int userId)
    {
        if (!TryResolve(total, out var amount, out var accountId) || amount <= 0m)
            return;

        var payment = new Payment
        {
            Direction = direction,
            PartyId = partyId,
            CashAccountId = accountId,
            Method = MethodValue,
            Amount = amount,
            SaleId = saleId,
            PurchaseId = purchaseId,
            DocDate = today,
        };

        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();

        await new Infrastructure.Services.PaymentPostingService(ctx)
            .PostAsync(payment.Id, userId, today);
    }

    /// <summary>Returns the panel to "nothing paid" after a successful post.</summary>
    public void Reset()
    {
        Choice = Choices.First(c => c.Value == SettlementChoice.Deferred);
        PartialAmount = string.Empty;
        Problem = string.Empty;
    }
}
