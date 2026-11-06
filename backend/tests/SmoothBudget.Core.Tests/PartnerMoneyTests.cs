using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Tests;

/// <summary>SPEC section 3 partner examples and Appendix D transfers. Names invented.</summary>
public class PartnerMoneyTests
{
    private static readonly DateOnly Nov3 = new(2026, 11, 3);
    private static SplitPurchase Split(long cents, string merchant = "FRESH MARKET", int day = 3, decimal share = 0.5m) =>
        new(Guid.NewGuid(), new DateOnly(2026, 11, day), merchant, cents, share);

    [Fact]
    public void A_120_grocery_run_split_50_50_means_she_owes_60()
    {
        var s = Split(120_00);
        Assert.Equal(60_00, s.PartnerShareCents);
        Assert.Equal(60_00, PartnerMoney.Balance([s], []).OwedToOwnerCents);
    }

    [Fact]
    public void Paying_back_moves_the_balance_both_ways()
    {
        var splits = new[] { Split(120_00), Split(80_00, "TAQUERIA UNO", 8) };
        var transfers = new[]
        {
            new PartnerTransfer(Guid.NewGuid(), new DateOnly(2026, 11, 10), -60_00, TransactionKind.PartnerSettleUp), // she paid back
            new PartnerTransfer(Guid.NewGuid(), new DateOnly(2026, 11, 12), 25_00, TransactionKind.PartnerSettleUp),  // he paid her back
            new PartnerTransfer(Guid.NewGuid(), new DateOnly(2026, 11, 21), -800_00, TransactionKind.PartnerContribution), // rent: not settle-up
        };
        var b = PartnerMoney.Balance(splits, transfers);
        Assert.Equal((100_00L, 60_00L, 25_00L), (b.SplitSharesCents, b.SettledByHerCents, b.SettledByOwnerCents));
        Assert.Equal(100_00 - 60_00 + 25_00, b.OwedToOwnerCents);
    }

    [Fact]
    public void Marking_settled_starts_the_balance_over()
    {
        var b = PartnerMoney.Balance([Split(120_00, day: 3), Split(40_00, day: 15)], [], settledThrough: new DateOnly(2026, 11, 10));
        Assert.Equal(20_00, b.OwedToOwnerCents);
    }

    [Fact]
    public void Her_800_looks_like_her_contribution()
    {
        var g = PartnerMoney.Guess(new PartnerTransfer(Guid.NewGuid(), new DateOnly(2026, 11, 21), -800_00, TransactionKind.PartnerContribution), [], "JANE PARTNER");
        Assert.Equal((PartnerPurpose.Contribution, false), (g.Purpose, g.Unsure));
        Assert.Equal("Looks like Jane's regular contribution toward bills.", g.Reason);
    }

    [Fact]
    public void Her_half_of_a_recent_split_purchase_is_matched()
    {
        var groceries = Split(120_00);
        var g = PartnerMoney.Guess(new PartnerTransfer(Guid.NewGuid(), new DateOnly(2026, 11, 5), -60_00, TransactionKind.PartnerContribution), [groceries], "JANE PARTNER");
        Assert.Equal((PartnerPurpose.SettleUp, false, groceries.Id), (g.Purpose, g.Unsure, g.MatchedPurchaseId));
        Assert.Equal("Matches Jane's share of FRESH MARKET $120.00 on Nov 3.", g.Reason);
    }

    [Fact]
    public void A_small_unmatched_amount_is_an_unsure_settle_up_and_money_out_asks_what_it_was()
    {
        var small = PartnerMoney.Guess(new PartnerTransfer(Guid.NewGuid(), Nov3, -23_50, TransactionKind.PartnerContribution), [Split(120_00, day: 1)], "JANE PARTNER");
        Assert.Equal((PartnerPurpose.SettleUp, true), (small.Purpose, small.Unsure));
        var sent = PartnerMoney.Guess(new PartnerTransfer(Guid.NewGuid(), Nov3, 40_00, TransactionKind.Purchase), [], "JANE PARTNER");
        Assert.Equal((PartnerPurpose.MyShareOfHers, true), (sent.Purpose, sent.Unsure));
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
