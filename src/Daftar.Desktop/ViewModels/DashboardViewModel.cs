using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daftar.Domain.Catalog;
using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Domain.Finance;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Daftar.Desktop.Diagnostics;

namespace Daftar.ViewModels;

/// <summary>
/// What the manager needs at a glance: who owes money, today's takings, and low stock.
/// </summary>
public partial class DashboardViewModel(IServiceProvider services) : ObservableObject
{
    private readonly IServiceProvider _services = services;
    private readonly Daftar.Services.INavigationService? _navigation = services.GetService<Daftar.Services.INavigationService>();

    [RelayCommand]
    public void OpenTab(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            _navigation?.Navigate(key);
        }
    }

    /// <summary>Parties who owe us.</summary>
    public ObservableCollection<ReceivableRow> Receivables { get; } = [];

    /// <summary>Suppliers we owe: the other end of the same ledger.</summary>
    public ObservableCollection<PayableRow> Payables { get; } = [];

    public ObservableCollection<RecentSaleRow> RecentSales { get; } = [];

    [ObservableProperty] private string _todayCash = "0.00";
    [ObservableProperty] private string _todaySales = "0.00";
    [ObservableProperty] private string _todaySalesCount = "0";
    [ObservableProperty] private string _totalReceivables = "0.00";
    [ObservableProperty] private string _totalPayables = "0.00";
    [ObservableProperty] private string _netPosition = "0.00";
    [ObservableProperty] private string _totalOverdue = "0.00";
    [ObservableProperty] private int _lowStockCount;
    [ObservableProperty] private string _busyText = "جاري التحميل…";
    [ObservableProperty] private bool _loaded;

    [RelayCommand]
    public async Task LoadAsync()
    {
        using var scope = _services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();

        try
        {
            await LoadCoreAsync(ctx);
            BusyText = string.Empty;
            Loaded = true;
        }
        catch (Exception ex)
        {
            _services.Report("Dashboard.LoadAsync", ex);
            BusyText = $"خطأ: {ex.Message}";
        }
    }

    private async Task LoadCoreAsync(DaftarContext ctx)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        // One net balance per party, signed from our side: positive means they owe us,
        // negative means we owe them. A purchase puts a supplier on the negative side, so the
        // two lists are the same numbers read from opposite ends.
        Receivables.Clear();
        Payables.Clear();

        var ledger = await ctx.PartyAccountTransactions.ToListAsync();
        var balances = ledger
            .GroupBy(t => t.PartyId)
            .Select(g => (PartyId: g.Key, Balance: Rounding.Amount(g.Sum(t => t.Amount))))
            .Where(x => x.Balance != 0m)
            .ToList();

        var parties = await ctx.Parties.ToDictionaryAsync(p => p.Id, p => p);

        foreach (var (partyId, balance) in balances.OrderByDescending(x => x.Balance))
        {
            if (parties.GetValueOrDefault(partyId) is not { } party)
                continue;

            var rows = ledger.Where(t => t.PartyId == partyId).ToList();

            // Ageing is measured from the document that created the debt, which is a sale for a
            // customer and a purchase for a supplier.
            var owingKind = balance > 0m ? PartyLedgerKind.Sale : PartyLedgerKind.Purchase;

            var oldestDue = rows
                .Where(t => t.Kind == owingKind && t.DueDate is not null && t.DueDate < today)
                .Select(t => t.DueDate!.Value)
                .DefaultIfEmpty()
                .Min();

            int? daysOverdue = oldestDue == DateOnly.MinValue
                ? null
                : today.DayNumber - oldestDue.DayNumber;

            if (balance > 0m)
            {
                Receivables.Add(new ReceivableRow(party.Name, balance, daysOverdue));
            }
            else
            {
                Payables.Add(new PayableRow(party.Name, -balance, daysOverdue));
            }
        }

        var receivableTotal = Rounding.Amount(Receivables.Sum(r => r.Balance));
        var payableTotal = Rounding.Amount(Payables.Sum(p => p.Balance));

        TotalReceivables = Money(receivableTotal);
        TotalPayables = Money(payableTotal);

        // What the business is actually ahead by: what is owed to us less what we owe.
        NetPosition = Money(receivableTotal - payableTotal);


        // Overdue: any balance where the oldest due date has passed.
        TotalOverdue = Money(Receivables.Where(r => r.DaysOverdue > 0).Sum(r => r.Balance));

        // --- today's takings ---
        var todaysSales = await ctx.Sales
            .Where(s => s.Status == DocumentStatus.Posted && s.DocDate == today)
            .ToListAsync();

        TodaySales = Money(todaysSales.Where(s => s.Type == Domain.Sales.SaleType.Invoice).Sum(s => s.Total));
        TodaySalesCount = todaysSales.Count(s => s.Type == Domain.Sales.SaleType.Invoice).ToString(CultureInfo.InvariantCulture);

        // Cash actually collected today.
        var cash = await ctx.CashTransactions
            .Where(t => t.BusinessDate == today
                     && t.Kind == CashTransactionKind.Collection)
            .ToListAsync();
        TodayCash = Money(Rounding.Amount(cash.Sum(t => t.Amount)));

        RecentSales.Clear();
        foreach (var sale in await ctx.Sales
                     .Include(s => s.Items)
                     .Where(s => s.Status == DocumentStatus.Posted
                              && s.Type == Domain.Sales.SaleType.Invoice)
                     .OrderByDescending(s => s.Id)
                     .Take(8)
                     .ToListAsync())
        {
            RecentSales.Add(new RecentSaleRow(
                sale.DocNo ?? "—",
                parties.GetValueOrDefault(sale.PartyId)?.Name ?? "—",
                sale.Total,
                sale.Margin));
        }

        // --- low stock ---
        LowStockCount = await LowStockCountAsync(ctx);
    }

    private static async Task<int> LowStockCountAsync(DaftarContext ctx)
    {
        var balances = await ctx.StockBalances
            .Include(b => b.Lot)
            .ToListAsync();

        var onHand = balances
            .Where(b => b.Lot!.Status == LotStatus.Active)
            .GroupBy(b => b.Lot!.ProductId)
            .Select(g => (ProductId: g.Key, Quantity: g.Sum(b => b.Quantity)))
            .ToList();

        var thresholds = await ctx.Products
            .Where(p => p.ReorderLevel != null)
            .ToDictionaryAsync(p => p.Id, p => p.ReorderLevel!.Value);

        return onHand.Count(x =>
            thresholds.TryGetValue(x.ProductId, out var level) && x.Quantity <= level);
    }

    /// <summary>Plain decimal formatting: no currency symbol, so it reads as a number.</summary>
    public static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);
}

public sealed record ReceivableRow(string Customer, decimal Balance, int? DaysOverdue);

/// <summary>A supplier we owe, shown positive.</summary>
public sealed record PayableRow(string Supplier, decimal Balance, int? DaysOverdue);


public sealed record RecentSaleRow(string DocNo, string Customer, decimal Total, decimal Margin);