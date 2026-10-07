using Daftar.Domain.Parties;
using Daftar.Domain.Shared;
using Xunit;

namespace Daftar.Domain.Tests;

public class RoundingAndPartyTests
{
    [Theory]
    [InlineData(2.005, 2.01)]   // AwayFromZero, not banker's rounding
    [InlineData(2.004, 2.00)]
    [InlineData(-2.005, -2.01)]
    [InlineData(1.115, 1.12)]
    public void Amount_RoundsAwayFromZero(double input, double expected)
        => Assert.Equal((decimal)expected, Rounding.Amount((decimal)input));

    [Fact]
    public void Amount_SplitsWithoutLosingOrGainingACent()
    {
        // The classic case: 100.00 into 3 parts must re-sum to exactly 100.00.
        var total = Rounding.Amount(100m);

        var third = Rounding.Amount(total / 3m);
        var parts = new[] { third, third, Rounding.Amount(total - third - third) };

        Assert.Equal(total, parts.Sum());
        Assert.Equal(100.00m, parts.Sum());
    }

    [Fact]
    public void LineTotal_AppliesLineDiscountBeforeRounding()
        => Assert.Equal(180.00m, Rounding.LineTotal(quantity: 2m, unitPrice: 100m, discountAmount: 20m));

    [Fact]
    public void Party_MustBeCustomerOrSupplier()
    {
        var party = new Party { Code = "P1", Name = "Nobody" };

        Assert.Throws<DomainException>(party.Validate);
    }

    [Fact]
    public void Party_AsCustomerNeedsACustomerType()
    {
        var party = new Party { Code = "P1", Name = "Shop", IsCustomer = true };

        Assert.Throws<DomainException>(party.Validate);

        party.CustomerType = CustomerType.Supermarket;
        party.Validate();
    }

    [Fact]
    public void Party_WithZeroCreditLimitIsCashOnly()
    {
        var cash = new Party { Code = "P1", Name = "Shop", IsCustomer = true, CustomerType = CustomerType.Retail };
        var credit = new Party { Code = "P2", Name = "Store", IsCustomer = true, CustomerType = CustomerType.Supermarket, CreditLimit = 500m };

        Assert.False(cash.CanTakeCredit(0.01m));
        Assert.True(credit.CanTakeCredit(400m));
        Assert.False(credit.CanTakeCredit(501m));
    }

    [Fact]
    public void Party_PendingIsCashOnly_EvenWithCreditLimit()
    {
        var party = new Party
        {
            Code = "P3",
            Name = "New distributor customer",
            IsCustomer = true,
            CustomerType = CustomerType.Individual,
            CreditLimit = 1000m,
            Status = PartyStatus.Pending,
        };

        Assert.False(party.CanTakeCredit(1m));
    }
}