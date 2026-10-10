using System;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Domain.Finance;
using Daftar.Domain.Parties;
using Daftar.Infrastructure;
using Daftar.Infrastructure.Security;
using Daftar.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// المقبوضات, which is what finishes a partial payment: money taken at the till settles a sale on
/// the spot, and whatever is left is collected here later.
/// </summary>
public sealed class PaymentsScreenTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"daftar-pay-{Guid.NewGuid():N}.db");
    private ServiceProviderFixture _fixture = null!;

    private static readonly DateOnly DocDate = new(2026, 10, 7);

    public async Task InitializeAsync()
    {
        _fixture = await ServiceProviderFixture.CreateAsync(_dbPath);
        using var scope = _fixture.Services.CreateScope();
        await DaftarDatabase.InitializeAsync(
            scope.ServiceProvider.GetRequiredService<DaftarContext>(),
            PasswordHasher.Hash("admin"));

        var ctx = scope.ServiceProvider.GetRequiredService<DaftarContext>();
        ctx.Parties.Add(new Party
        {
            Code = "C-PAY",
            Name = "عميل دافع",
            IsCustomer = true,
            CustomerType = CustomerType.Retail,
        });

        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync()
    { _fixture.Dispose(); return Task.CompletedTask; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private async Task<int> OpenDebtAsync(decimal amount)
    {
        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();
        var party = await ctx.Parties.SingleAsync(p => p.Code == "C-PAY");

        ctx.PartyAccountTransactions.Add(new PartyAccountTransaction
        {
            PartyId = party.Id,
            OccurredAt = DateTime.UtcNow,
            BusinessDate = DocDate,
            Kind = PartyLedgerKind.Sale,
            Amount = amount,
            SourceType = "Sale",
            CreatedBy = 1,
        });

        await ctx.SaveChangesAsync();
        return party.Id;
    }

    private async Task<decimal> BalanceAsync(int partyId)
    {
        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();
        var rows = await ctx.PartyAccountTransactions.Where(t => t.PartyId == partyId).ToListAsync();
        return rows.Sum(r => r.Amount);
    }

    private static async Task WaitForAsync(Func<bool> until)
    {
        for (var i = 0; i < 40 && !until(); i++)
            await Task.Delay(25);
    }

    [Fact]
    public async Task Collecting_Part_Of_An_Open_Bill_Leaves_The_Rest_Owed()
    {
        var partyId = await OpenDebtAsync(500m);

        var vm = new PaymentsViewModel(_fixture.Services);
        await vm.LoadAsync();

        vm.SelectedDirection = vm.Directions.First(d => d.Value == PaymentDirection.In);
        vm.Party = vm.Parties.First(p => p.Id == partyId);
        vm.SelectedMethod = vm.Methods.First(m => m.Value == PaymentMethod.Wallet);
        await WaitForAsync(() => vm.Accounts.Count > 0);

        vm.Account = vm.Accounts.First();
        vm.Amount = "200";

        await vm.PostCommand.ExecuteAsync(null);

        Assert.Equal(300m, await BalanceAsync(partyId));
        Assert.Contains("تم تحصيل", vm.Status);
    }

    [Fact]
    public async Task Collecting_Over_What_Is_Owed_Is_Refused()
    {
        var partyId = await OpenDebtAsync(100m);

        var vm = new PaymentsViewModel(_fixture.Services);
        await vm.LoadAsync();

        vm.SelectedDirection = vm.Directions.First(d => d.Value == PaymentDirection.In);
        vm.Party = vm.Parties.First(p => p.Id == partyId);
        await WaitForAsync(() => vm.Accounts.Count > 0);
        vm.Account = vm.Accounts.First();

        vm.Amount = "900";

        await vm.PostCommand.ExecuteAsync(null);

        Assert.Contains("أكبر من المستحق", vm.Status);
        Assert.Equal(100m, await BalanceAsync(partyId));

        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();
        Assert.Empty(await ctx.Payments.ToListAsync());
    }

    [Fact]
    public async Task A_Wallet_Collection_Goes_To_A_Wallet_Account()
    {
        var partyId = await OpenDebtAsync(300m);

        var vm = new PaymentsViewModel(_fixture.Services);
        await vm.LoadAsync();

        vm.SelectedDirection = vm.Directions.First(d => d.Value == PaymentDirection.In);
        vm.Party = vm.Parties.First(p => p.Id == partyId);
        vm.SelectedMethod = vm.Methods.First(m => m.Value == PaymentMethod.Wallet);
        await WaitForAsync(() => vm.Accounts.Count > 0);

        vm.Account = vm.Accounts.First();
        vm.Amount = "300";

        await vm.PostCommand.ExecuteAsync(null);

        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();

        var payment = await ctx.Payments.SingleAsync();
        Assert.Equal(PaymentMethod.Wallet, payment.Method);

        var cashTx = await ctx.CashTransactions.SingleAsync();
        Assert.Equal(300m, cashTx.Amount); // money received adds to the account
        Assert.Equal(0m, await BalanceAsync(partyId));
    }

    [Fact]
    public async Task The_Screen_Reports_What_The_Party_Still_Owes()
    {
        var partyId = await OpenDebtAsync(750m);

        var vm = new PaymentsViewModel(_fixture.Services);
        await vm.LoadAsync();

        vm.SelectedDirection = vm.Directions.First(d => d.Value == PaymentDirection.In);
        vm.Party = vm.Parties.First(p => p.Id == partyId);
        await WaitForAsync(() => vm.Outstanding.Length > 0);

        Assert.Contains("750", vm.Outstanding);
    }

    [Fact]
    public async Task Posted_Vouchers_Show_In_The_History()
    {
        var partyId = await OpenDebtAsync(400m);

        var vm = new PaymentsViewModel(_fixture.Services);
        await vm.LoadAsync();

        vm.SelectedDirection = vm.Directions.First(d => d.Value == PaymentDirection.In);
        vm.Party = vm.Parties.First(p => p.Id == partyId);
        await WaitForAsync(() => vm.Accounts.Count > 0);
        vm.Account = vm.Accounts.First();
        vm.Amount = "400";

        await vm.PostCommand.ExecuteAsync(null);

        var voucher = Assert.Single(vm.Recent);

        Assert.False(string.IsNullOrWhiteSpace(voucher.DocNo));
        Assert.Equal("تحصيل", voucher.DirectionLabel);
        Assert.Equal(400m, voucher.SignedAmount);
        Assert.True(voucher.IsPosted);
    }

    [Fact]
    public async Task Nothing_Posts_Without_A_Party_And_An_Account()
    {
        var vm = new PaymentsViewModel(_fixture.Services);
        await vm.LoadAsync();

        vm.Amount = "100";

        Assert.False(vm.CanPost);

        await vm.PostCommand.ExecuteAsync(null);

        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();
        Assert.Empty(await ctx.Payments.ToListAsync());
    }

    [Fact]
    public async Task Treasury_Balances_And_Total_Liquidity_Reflect_Transactions()
    {
        var partyId = await OpenDebtAsync(500m);

        var vm = new PaymentsViewModel(_fixture.Services);
        await vm.LoadAsync();

        Assert.True(vm.TreasuryAccounts.Count >= 3);
        var initialLiquidity = vm.TotalLiquidity;

        vm.SelectedDirection = vm.Directions.First(d => d.Value == PaymentDirection.In);
        vm.Party = vm.Parties.First(p => p.Id == partyId);
        vm.SelectedMethod = vm.Methods.First(m => m.Value == PaymentMethod.Cash);
        await WaitForAsync(() => vm.Accounts.Count > 0);
        vm.Account = vm.Accounts.First(a => a.Name == "الكاشير");
        vm.Amount = "500";

        await vm.PostCommand.ExecuteAsync(null);

        Assert.Equal(initialLiquidity + 500m, vm.TotalLiquidity);
        var cashierCard = vm.TreasuryAccounts.First(a => a.Name == "الكاشير");
        Assert.Equal(500m, cashierCard.Balance);
        Assert.Equal(500m, vm.TodayReceiptsTotal);
        Assert.Equal(1, vm.TodayReceiptsCount);
    }

    [Fact]
    public async Task Fill_Full_Outstanding_Fills_Exact_Party_Balance()
    {
        var partyId = await OpenDebtAsync(635.50m);

        var vm = new PaymentsViewModel(_fixture.Services);
        await vm.LoadAsync();

        vm.SelectedDirection = vm.Directions.First(d => d.Value == PaymentDirection.In);
        vm.Party = vm.Parties.First(p => p.Id == partyId);
        await WaitForAsync(() => vm.Outstanding.Length > 0);

        vm.FillFullOutstandingCommand.Execute(null);

        Assert.Equal(635.50m, decimal.Parse(vm.Amount));
    }

    [Fact]
    public async Task Post_Movement_Expense_Deducts_Cash_And_Records_Movement()
    {
        var vm = new PaymentsViewModel(_fixture.Services);
        await vm.LoadAsync();

        var cashier = vm.AllAccounts.First(a => a.Name == "الكاشير");
        vm.MovementAccount = cashier;
        vm.SelectedMovementKind = vm.MovementKinds.First(k => k.Value == CashTransactionKind.Expense);
        vm.SelectedExpenseCategory = vm.ExpenseCategories.First(c => c.Value == ExpenseCategory.Utilities);
        vm.MovementAmount = "150";
        vm.MovementNotes = "فاتورة كهرباء المحل";

        Assert.True(vm.CanPostMovement);

        await vm.PostMovementCommand.ExecuteAsync(null);

        Assert.Contains("تم تسجيل", vm.Status);

        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();
        var tx = await ctx.CashTransactions.OrderByDescending(t => t.Id).FirstOrDefaultAsync();
        Assert.NotNull(tx);
        Assert.Equal(-150m, tx.Amount);
        Assert.Equal(CashTransactionKind.Expense, tx.Kind);
        Assert.Equal(ExpenseCategory.Utilities, tx.Category);
    }

    [Fact]
    public async Task Post_Transfer_Between_Accounts_Moves_Funds()
    {
        var vm = new PaymentsViewModel(_fixture.Services);
        await vm.LoadAsync();

        // Deposit 1000 initial cash
        var cashier = vm.AllAccounts.First(a => a.Name == "الكاشير");
        var wallet = vm.AllAccounts.First(a => a.Name == "انستاباي");

        vm.MovementAccount = cashier;
        vm.SelectedMovementKind = vm.MovementKinds.First(k => k.Value == CashTransactionKind.Deposit);
        vm.MovementAmount = "1000";
        await vm.PostMovementCommand.ExecuteAsync(null);

        // Now transfer 400 from Cashier to Wallet
        vm.TransferFromAccount = cashier;
        vm.TransferToAccount = wallet;
        vm.TransferAmount = "400";
        vm.TransferNotes = "تحويل كاش إلى انستاباي";

        Assert.True(vm.CanPostTransfer);

        await vm.PostTransferCommand.ExecuteAsync(null);

        Assert.Contains("تم تحويل", vm.Status);

        var ctx = _fixture.Services.CreateScope().ServiceProvider.GetRequiredService<DaftarContext>();
        var cashierBal = (await ctx.CashTransactions.Where(t => t.CashAccountId == cashier.Id).ToListAsync()).Sum(t => t.Amount);
        var walletBal = (await ctx.CashTransactions.Where(t => t.CashAccountId == wallet.Id).ToListAsync()).Sum(t => t.Amount);

        Assert.Equal(600m, cashierBal);
        Assert.Equal(400m, walletBal);
    }

    [Fact]
    public async Task Filter_Recent_Vouchers_By_Direction_And_Search_Text()
    {
        var partyId = await OpenDebtAsync(500m);

        var vm = new PaymentsViewModel(_fixture.Services);
        await vm.LoadAsync();

        // 1. Post Inflow
        vm.SelectedDirection = vm.Directions.First(d => d.Value == PaymentDirection.In);
        vm.Party = vm.Parties.First(p => p.Id == partyId);
        await WaitForAsync(() => vm.Accounts.Count > 0);
        vm.Account = vm.Accounts.First();
        vm.Amount = "200";
        await vm.PostCommand.ExecuteAsync(null);

        // 2. Post Outflow
        vm.SelectedDirection = vm.Directions.First(d => d.Value == PaymentDirection.Out);
        vm.Party = vm.Parties.First(p => p.Id == partyId);
        vm.Amount = "50";
        await vm.PostCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Recent.Count);

        // Filter: Inflows only (Index 1)
        vm.HistoryFilterIndex = 1;
        Assert.Single(vm.FilteredRecent);
        Assert.True(vm.FilteredRecent[0].IsInflow);

        // Filter: Outflows only (Index 2)
        vm.HistoryFilterIndex = 2;
        Assert.Single(vm.FilteredRecent);
        Assert.False(vm.FilteredRecent[0].IsInflow);

        // Filter: All (Index 0) + Search Text
        vm.HistoryFilterIndex = 0;
        vm.SearchText = "دافع";
        Assert.Equal(2, vm.FilteredRecent.Count);

        vm.SearchText = "لا_يوجد_طرف_بهذا_الاسم";
        Assert.Empty(vm.FilteredRecent);
    }
}