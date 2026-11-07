using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Tests;

/// <summary>Money between you and your partner beyond her fixed contribution (DECISIONS.md 100). Names invented.</summary>
public class PartnerMoneyTests
{
    private static readonly Guid Groceries = Guid.NewGuid();
    private static readonly RecentPurchase FreshMarket = new(new DateOnly(2026, 11, 10), "FRESH MARKET", 120_00, Groceries);

    [Fact]
    public void Exactly_half_of_a_recent_purchase_suggests_its_category()
    {
        var s = PartnerMoney.Suggest(new DateOnly(2026, 11, 12), -60_00, [FreshMarket], "JANE PARTNER");
        Assert.Equal((Groceries, "Looks like half of FRESH MARKET $120.00 on Nov 10."), (s.CategoryId!.Value, s.Reason));
    }

    [Fact]
    public void Odd_cents_still_match_half()
    {
        var odd = FreshMarket with { AmountCents = 85_01 };
        Assert.Equal(Groceries, PartnerMoney.Suggest(new DateOnly(2026, 11, 12), -42_50, [odd], "JANE PARTNER").CategoryId);
    }

    [Fact]
    public void Anything_else_just_asks()
    {
        var other = PartnerMoney.Suggest(new DateOnly(2026, 11, 12), -23_50, [FreshMarket], "JANE PARTNER");
        Assert.Equal(((Guid?)null, "From Jane: what was it for?"), (other.CategoryId, other.Reason));
        Assert.Null(PartnerMoney.Suggest(new DateOnly(2026, 12, 20), -60_00, [FreshMarket], "JANE PARTNER").CategoryId); // too long after
        Assert.Equal("Sent to Jane: what was it for?", PartnerMoney.Suggest(new DateOnly(2026, 11, 12), 30_00, [FreshMarket], "JANE PARTNER").Reason);
    }

    [Theory]
    [InlineData("Zelle payment to Jane 1234567", 40_00, true)]
    [InlineData("Zelle payment to JANE PARTNER JPM99", 40_00, true)]
    [InlineData("Zelle payment to Janet Other 1234", 40_00, false)]
    [InlineData("Zelle payment from JANE PARTNER 1234", -40_00, false)] // money in: not "to"
    [InlineData("JANE'S BAKERY", 40_00, false)]
    public void Recognizes_money_sent_to_her(string description, long cents, bool expected) =>
        Assert.Equal(expected, PartnerMoney.IsToPartner(description, cents, "Jane Partner"));
}
