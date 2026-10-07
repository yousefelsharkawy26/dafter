using Daftar.Domain.Finance;
using Daftar.Domain.Parties;
using Xunit;

namespace Daftar.Domain.Tests;

public class LedgerTests
{
    private static PartyAccountTransaction Ledger(PartyLedgerKind kind, decimal amount)
        => new() { PartyId = 1, Kind = kind, Amount = amount, BusinessDate = new DateOnly(2026, 10, 5), SourceType = "Sale" };

    [Theory]
    // Signed from our side: positive means the party owes us.
    [InlineData(PartyLedgerKind.Sale, 100)]           // the customer now owes us
    [InlineData(PartyLedgerKind.SaleReturn, -100)]     // they gave goods back
    [InlineData(PartyLedgerKind.Purchase, -100)]       // we now owe the supplier
    [InlineData(PartyLedgerKind.PurchaseReturn, 100)] // supplier gave goods back
    [InlineData(PartyLedgerKind.PaymentIn, -100)]      // customer paid: reduces what they owe
    [InlineData(PartyLedgerKind.PaymentOut, 100)]     // we paid: reduces what we owe
    public void SignMustAgreeWithTheLedgerKind(PartyLedgerKind kind, decimal amount)
    {
        // The correct sign is accepted.
        Ledger(kind, amount).Validate();

        // Flipping it is refused: Sale positive, Purchase negative, and so on.
        Assert.Throws<Shared.DomainException>(() => Ledger(kind, -amount).Validate());
    }

    [Fact]
    public void PartyNetBalance_NetsSalesAndPayments()
    {
        // A customer buys 100 on credit, then pays 40.
        var rows = new[] { Ledger(PartyLedgerKind.Sale, 100m), Ledger(PartyLedgerKind.PaymentIn, -40m) };

        // Still owes 60.
        Assert.Equal(60m, rows.Sum(r => r.Amount));
    }

    [Fact]
    public void PartyWithCreditLimit_BlocksWhenOverTheLimit()
    {
        var customer = new Party
        {
            Code = "C1", Name = "Shop", IsCustomer = true,
            CustomerType = CustomerType.Supermarket,
            CreditLimit = 500m,
        };

        Assert.True(customer.CanTakeCredit(500m));
        Assert.False(customer.CanTakeCredit(500.01m));
    }

    [Fact]
    public void PaymentAmountMustBePositive()
    {
        var payment = new Payment
        {
            PartyId = 1, CashAccountId = 1, Amount = 0m, DocDate = new DateOnly(2026, 10, 5),
        };

        Assert.Throws<Shared.DomainException>(payment.Validate);
    }

    [Fact]
    public void PaymentDeltaSigns_SenseIsCarriedByTheDirection()
    {
        var receipt = new Payment { Direction = PaymentDirection.In, Amount = 50m, PartyId = 1, CashAccountId = 1 };
        var payment = new Payment { Direction = PaymentDirection.Out, Amount = 50m, PartyId = 1, CashAccountId = 1 };

        // Receiving money adds to the account but reduces what the customer owes us.
        Assert.Equal(50m, receipt.CashDelta);
        Assert.Equal(-50m, receipt.PartyBalanceDelta);

        // Paying out: cash leaves, and what we owe the party also drops. That balance is
        // negative, so reducing it moves it up, which is why the delta is positive.
        Assert.Equal(-50m, payment.CashDelta);
        Assert.Equal(50m, payment.PartyBalanceDelta);
    }

    [Fact]
    public void PaymentCannotSettleBothASaleAndAPurchase()
    {
        var payment = new Payment
        {
            PartyId = 1, CashAccountId = 1, Amount = 10m,
            SaleId = 1, PurchaseId = 1,
            DocDate = new DateOnly(2026, 10, 5),
        };

        Assert.Throws<Shared.DomainException>(payment.Validate);
    }

    [Fact]
    public void CashTransactionRejectsZeroAmount()
    {
        var tx = new CashTransaction { CashAccountId = 1, Kind = CashTransactionKind.Collection, Amount = 0m };

        Assert.Throws<Shared.DomainException>(tx.Validate);
    }
}