using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Tests;

/// <summary>Milestone 6: recurring items are found, and a price change is flagged. Merchants invented.</summary>
public class RecurringDetectorTests
{
    private static readonly Guid Card = Guid.NewGuid(), Checking = Guid.NewGuid(), OtherCard = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 6);

    private static RecurringTxn T(int year, int month, int day, long cents, string merchant, Guid? account = null, CategoryType? type = null) =>
        new(Guid.NewGuid(), new DateOnly(year, month, day), cents, merchant, account ?? Card, null, type);

    private static IEnumerable<RecurringTxn> Monthly(string merchant, long cents, int day, int fromMonth, int toMonth, Guid? account = null) =>
        Enumerable.Range(fromMonth, toMonth - fromMonth + 1).Select(m => T(2026, m, day, cents, merchant, account));

    [Fact]
    public void A_streaming_price_increase_is_flagged()
    {
        var txns = Monthly("STREAMCO.COM", 15_49, 3, 5, 9).Append(T(2026, 10, 3, 17_99, "STREAMCO.COM")).ToList();
        var s = Assert.Single(RecurringDetector.Detect(txns, Today));

        Assert.Equal(("Streamco", Frequency.Monthly, RecurringKind.Subscription, RecurringStatus.PriceChanged), (s.Name, s.Frequency, s.Kind, s.Status));
        Assert.Equal((17_99L, 15_49L, new DateOnly(2026, 10, 3)), (s.AmountCents, s.PreviousAmountCents!.Value, s.PriceChangedOn!.Value));
        Assert.Equal((new DateOnly(2026, 11, 3), 6), (s.NextExpected, s.Occurrences));
        Assert.Equal(17_99, s.MonthlyCents);
    }

    [Fact]
    public void A_utility_bill_that_varies_month_to_month_is_still_a_bill()
    {
        long[] amounts = [92_10, 81_44, 104_90, 131_02, 118_75, 88_60];
        var txns = amounts.Select((a, i) => T(2026, 5 + i, 12 + i % 3, a, "CITY ELECTRIC 5550001 WEB PMT", Checking)).ToList();
        var s = Assert.Single(RecurringDetector.Detect(txns, Today));
        Assert.Equal(("City Electric", Frequency.Monthly, RecurringKind.Bill, RecurringStatus.Active), (s.Name, s.Frequency, s.Kind, s.Status));
        Assert.Null(s.PreviousAmountCents); // varying isn't a price change
    }

    [Fact]
    public void Grocery_runs_are_not_a_bill()
    {
        var rng = new Random(7);
        var txns = Enumerable.Range(0, 30).Select(i => T(2026, 1, 1, 0, "CORNER GROCERY #12") with
        {
            Date = new DateOnly(2026, 4, 1).AddDays(i * 7 + rng.Next(-1, 2)),
            AmountCents = 40_00 + rng.Next(0, 9_000),
        }).ToList();
        Assert.Empty(RecurringDetector.Detect(txns, Today));
    }

    [Fact]
    public void A_monthly_dinner_out_at_different_prices_is_not_a_bill()
    {
        long[] amounts = [16_97, 19_19, 18_45, 17_60, 20_12];
        var txns = amounts.Select((a, i) => T(2026, 6 + i, 9, a, "TAQUERIA UNO", type: CategoryType.Variable)).ToList();
        Assert.Empty(RecurringDetector.Detect(txns, Today));
    }

    [Fact]
    public void Store_numbers_dont_split_a_merchant()
    {
        var txns = new[] { T(2026, 8, 15, 49_99, "FITCLUB #0042"), T(2026, 9, 15, 49_99, "FITCLUB #0042"), T(2026, 10, 15, 49_99, "FITCLUB #0107") };
        Assert.Equal("FITCLUB", Assert.Single(RecurringDetector.Detect(txns, Today)).MerchantKey);
    }

    [Fact]
    public void Two_subscriptions_from_one_merchant_are_told_apart_by_amount()
    {
        var txns = Monthly("APPSTORE.COM/BILL", 2_99, 4, 6, 10).Concat(Monthly("APPSTORE.COM/BILL", 9_99, 19, 6, 10)).ToList();
        var found = RecurringDetector.Detect(txns, Today);
        Assert.Equal([2_99L, 9_99L], found.Select(s => s.AmountCents).Order());
        Assert.All(found, s => Assert.False(s.PossibleDuplicate));
    }

    [Fact]
    public void The_same_subscription_on_two_cards_is_a_possible_duplicate()
    {
        var txns = Monthly("STREAMCO.COM", 15_49, 3, 6, 10).Concat(Monthly("STREAMCO.COM", 15_49, 17, 6, 10, OtherCard)).ToList();
        var found = RecurringDetector.Detect(txns, Today);
        Assert.Equal(2, found.Count);
        Assert.All(found, s => Assert.True(s.PossibleDuplicate));
    }

    [Fact]
    public void A_charge_that_stopped_is_possibly_canceled()
    {
        var s = Assert.Single(RecurringDetector.Detect(Monthly("MUSICBOX", 10_99, 8, 3, 7).ToList(), Today));
        Assert.Equal((RecurringStatus.PossiblyCanceled, new DateOnly(2026, 8, 8)), (s.Status, s.NextExpected));
    }

    [Fact]
    public void A_charge_due_but_within_its_grace_period_is_still_active()
    {
        // Last on Sep 30, so Oct 30 was expected; on Nov 6 it's 7 days late, inside the 11-day grace for monthly.
        var s = Assert.Single(RecurringDetector.Detect(Monthly("MUSICBOX", 10_99, 30, 6, 9).ToList(), Today));
        Assert.Equal(RecurringStatus.Active, s.Status);
    }

    [Fact]
    public void A_yearly_renewal_needs_only_two_charges()
    {
        var txns = new[] { T(2025, 3, 14, 99_00, "CLOUDDRIVE"), T(2026, 3, 14, 99_00, "CLOUDDRIVE") };
        var s = Assert.Single(RecurringDetector.Detect(txns, Today));
        Assert.Equal((Frequency.Yearly, 8_25L, new DateOnly(2027, 3, 14)), (s.Frequency, s.MonthlyCents, s.NextExpected));
    }

    [Fact]
    public void Two_one_off_purchases_a_month_apart_are_not_enough()
    {
        Assert.Empty(RecurringDetector.Detect([T(2026, 9, 2, 30_00, "GADGET SHOP"), T(2026, 10, 2, 30_00, "GADGET SHOP")], Today));
    }

    [Fact]
    public void Insurance_premiums_are_bills_and_exact_streaming_is_a_subscription_even_in_a_bill_category()
    {
        var found = RecurringDetector.Detect(
            Monthly("SHIELDCO INS.PREM", 11_88, 26, 6, 10).Concat(Monthly("CITY POWER & LIGHT", 68_40, 21, 6, 10)).Concat(Enumerable.Range(6, 5).Select(m => T(2026, m, 12, 15_49, "STREAMCO", type: CategoryType.FixedBill))).ToList(), Today);
        Assert.Equal(RecurringKind.Bill, found.Single(s => s.MerchantKey.StartsWith("SHIELDCO")).Kind);
        Assert.Equal(RecurringKind.Bill, found.Single(s => s.MerchantKey.StartsWith("CITY POWER")).Kind); // exact, but a utility
        Assert.Equal(RecurringKind.Subscription, found.Single(s => s.MerchantKey == "STREAMCO").Kind);
    }

    [Fact]
    public void Rent_is_a_bill_even_at_an_exact_amount()
    {
        var s = Assert.Single(RecurringDetector.Detect(Monthly("MAPLE COURT APARTMENTS", 1_650_00, 1, 6, 10).ToList(), Today));
        Assert.Equal(RecurringKind.Bill, s.Kind);
    }

    [Theory]
    [InlineData(Frequency.Weekly, 25_00, 108_33)]   // 25 × 52 ÷ 12
    [InlineData(Frequency.Biweekly, 60_00, 130_00)] // 60 × 26 ÷ 12
    [InlineData(Frequency.Quarterly, 90_00, 30_00)]
    [InlineData(Frequency.Yearly, 139_00, 11_58)]   // 11.583 → 11.58
    public void Monthly_equivalents(Frequency f, long cents, long monthly) => Assert.Equal(monthly, RecurringDetector.MonthlyCents(cents, f));

    /// <summary>A store that bills two subscriptions, one of which went from 10.99 to 11.99 in July (DECISIONS.md 181).</summary>
    private static List<RecurringTxn> TwoAppSubscriptions() =>
        Monthly("APPLE.COM/BILL", 2_99, 14, 1, 10)
            .Concat(Monthly("APPLE.COM/BILL", 10_99, 20, 1, 6))
            .Concat(Monthly("APPLE.COM/BILL", 11_99, 20, 7, 10)).ToList();

    [Fact]
    public void A_new_price_at_a_store_that_bills_several_things_is_offered_as_the_same_bill()
    {
        var found = RecurringDetector.Detect(TwoAppSubscriptions(), Today);
        Assert.Equal(3, found.Count); // tracked per price, so the new price is a bill of its own

        var (older, newer) = Assert.Single(RecurringDetector.SameBillCandidates(found));
        Assert.Equal((10_99L, new DateOnly(2026, 6, 20)), (older.AmountCents, older.LastDate));
        Assert.Equal((11_99L, new DateOnly(2026, 7, 20)), (newer.AmountCents, newer.FirstDate));
    }

    [Fact]
    public void Bills_that_overlap_or_differ_a_lot_are_not_offered_as_one()
    {
        // Both still going: two things, not a price change.
        var both = Monthly("APPLE.COM/BILL", 2_99, 14, 1, 10).Concat(Monthly("APPLE.COM/BILL", 10_99, 20, 1, 10)).ToList();
        Assert.Empty(RecurringDetector.SameBillCandidates(RecurringDetector.Detect(both, Today)));

        // One stopped and a much bigger one began: more than 3× the price.
        var swap = Monthly("APPLE.COM/BILL", 2_99, 14, 1, 10).Concat(Monthly("APPLE.COM/BILL", 0_99, 20, 1, 6))
            .Concat(Monthly("APPLE.COM/BILL", 9_99, 20, 7, 10)).ToList();
        Assert.Empty(RecurringDetector.SameBillCandidates(RecurringDetector.Detect(swap, Today)));

        // A gap of months between them.
        var later = Monthly("APPLE.COM/BILL", 2_99, 14, 1, 10).Concat(Monthly("APPLE.COM/BILL", 10_99, 20, 1, 3))
            .Concat(Monthly("APPLE.COM/BILL", 11_99, 20, 7, 10)).ToList();
        Assert.Empty(RecurringDetector.SameBillCandidates(RecurringDetector.Detect(later, Today)));
    }

    /// <summary>
    /// A cloud-storage plan at one store that bills several things: 25.95 through June, then a higher price with a few
    /// cents of tax that differs each month, so no exact price repeats and it never shows up as a bill of its own.
    /// </summary>
    private static List<RecurringTxn> RaisedWithTax(params long[] after) =>
        Monthly("APPLE.COM/BILL", 2_99, 14, 1, 10)
            .Concat(Monthly("APPLE.COM/BILL", 11_99, 20, 1, 10))
            .Concat(Monthly("APPLE.COM/BILL", 25_95, 11, 1, 6))
            .Concat(after.Select((a, i) => T(2026, 7 + i, 11, a, "APPLE.COM/BILL"))).ToList();

    [Fact]
    public void Keep_looking_finds_a_bill_at_its_new_price()
    {
        var txns = RaisedWithTax(28_31, 28_37, 28_33, 28_35);
        var found = RecurringDetector.Detect(txns, Today);
        var stopped = Assert.Single(found, s => s.AmountCents == 25_95);
        Assert.Equal(RecurringStatus.PossiblyCanceled, stopped.Status); // the new charges aren't a bill of their own

        var look = RecurringDetector.KeepLooking(stopped, txns, found, Today);
        Assert.Equal(4, look.Found);
        Assert.Empty(look.Absorbed);
        var s = look.Series;
        Assert.Equal((RecurringStatus.PriceChanged, 28_35L, 25_95L, new DateOnly(2026, 7, 11)), (s.Status, s.AmountCents, s.PreviousAmountCents!.Value, s.PriceChangedOn!.Value));
        Assert.Equal((new DateOnly(2026, 10, 11), new DateOnly(2026, 11, 11), 10), (s.LastDate, s.NextExpected, s.Occurrences));
        // The 2.99 and 11.99 subscriptions that ran alongside it are left alone.
        var twoNinetyNine = found.Single(f => f.AmountCents == 2_99).TransactionIds;
        var elevenNinetyNine = found.Single(f => f.AmountCents == 11_99).TransactionIds;
        Assert.DoesNotContain(s.TransactionIds, id => twoNinetyNine.Contains(id) || elevenNinetyNine.Contains(id));
    }

    [Fact]
    public void Keep_looking_with_nothing_found_yet_stops_calling_it_stopped()
    {
        var txns = RaisedWithTax();
        var found = RecurringDetector.Detect(txns, Today);
        var look = RecurringDetector.KeepLooking(found.Single(s => s.AmountCents == 25_95), txns, found, Today);
        Assert.Equal((0, RecurringStatus.Active, 25_95L), (look.Found, look.Series.Status, look.Series.AmountCents));
    }

    [Fact]
    public void Keep_looking_flags_it_again_if_the_new_charges_stop_too()
    {
        var txns = RaisedWithTax(28_31, 28_37);
        var found = RecurringDetector.Detect(txns, Today);
        var look = RecurringDetector.KeepLooking(found.Single(s => s.AmountCents == 25_95), txns, found, Today);
        Assert.Equal((2, RecurringStatus.PossiblyCanceled, new DateOnly(2026, 8, 11)), (look.Found, look.Series.Status, look.Series.LastDate));
    }

    [Fact]
    public void Keep_looking_takes_in_the_bill_its_new_price_became()
    {
        var txns = TwoAppSubscriptions();
        var found = RecurringDetector.Detect(txns, Today);
        var old = found.Single(s => s.AmountCents == 10_99);
        var look = RecurringDetector.KeepLooking(old, txns, found, Today);
        Assert.Equal(found.Single(s => s.AmountCents == 11_99).Key, Assert.Single(look.Absorbed));
        Assert.Equal((4, 11_99L, 10_99L, 10), (look.Found, look.Series.AmountCents, look.Series.PreviousAmountCents!.Value, look.Series.Occurrences));
        Assert.DoesNotContain(look.Series.TransactionIds, found.Single(s => s.AmountCents == 2_99).TransactionIds.Contains);
    }
}
