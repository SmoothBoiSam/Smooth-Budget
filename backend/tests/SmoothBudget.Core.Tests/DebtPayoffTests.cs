using SmoothBudget.Core.Debt;

namespace SmoothBudget.Core.Tests;

/// <summary>
/// Milestone 5 "done when": payoff results match known amortization examples to the cent. Interest is
/// balance × APR ÷ 12, rounded to the cent each month; payments come after interest.
/// </summary>
public class DebtPayoffTests
{
    private static DebtInput D(string name, long cents, int aprBps, long min) => new(Guid.NewGuid(), name, cents, aprBps, min);

    [Fact]
    public void A_10000_loan_at_6_percent_over_60_months_matches_the_amortization_table()
    {
        // The textbook example: $10,000 at 6% for 5 years is $193.33 a month and $1,599.68 of interest.
        var loan = D("Loan", 10_000_00, 600, 193_33);
        var r = DebtPayoff.Simulate([loan], 0, DebtStrategy.Avalanche);

        Assert.Equal((60, false, 1_599_68L, 11_599_68L), (r.Months, r.NeverPaysOff, r.TotalInterestCents, r.TotalPaidCents));
        // First rows of the table: interest 50.00 then 49.28; balance 9,856.67 then 9,712.62.
        Assert.Equal((50_00L, 9_856_67L), (r.Schedule[0].InterestCents, r.Schedule[0].TotalBalanceCents));
        Assert.Equal((49_28L, 9_712_62L), (r.Schedule[1].InterestCents, r.Schedule[1].TotalBalanceCents));
        // The last payment is smaller: $193.21, including $0.96 of interest.
        Assert.Equal((96L, 193_21L, 0L), (r.Schedule[^1].InterestCents, r.Schedule[^1].PaidCents, r.Schedule[^1].TotalBalanceCents));
    }

    [Fact]
    public void A_5000_card_at_18_percent_paying_150_a_month_takes_47_months()
    {
        var card = D("Card", 5_000_00, 1800, 150_00);
        var r = DebtPayoff.Simulate([card], 0, DebtStrategy.Avalanche);

        Assert.Equal((47, 1_983_61L), (r.Months, r.TotalInterestCents));
        Assert.Equal((75_00L, 4_925_00L), (r.Schedule[0].InterestCents, r.Schedule[0].TotalBalanceCents));
        Assert.Equal((73_88L, 4_848_88L), (r.Schedule[1].InterestCents, r.Schedule[1].TotalBalanceCents)); // 4,925 × 1.5% = 73.875, rounded up
        Assert.Equal(83_61L, r.Schedule[^1].PaidCents);
    }

    [Fact]
    public void A_1000_balance_at_12_percent_paying_100_takes_11_months()
    {
        var r = DebtPayoff.Simulate([D("Card", 1_000_00, 1200, 100_00)], 0, DebtStrategy.Avalanche);
        Assert.Equal((11, 58_98L, 58_98L), (r.Months, r.TotalInterestCents, r.Schedule[^1].PaidCents));
    }

    [Fact]
    public void Extra_money_on_top_of_the_minimum_pays_off_sooner_and_costs_less()
    {
        var card = D("Card", 5_000_00, 1800, 150_00);
        var plain = DebtPayoff.Simulate([card], 0, DebtStrategy.Avalanche);
        var extra = DebtPayoff.Simulate([card], 100_00, DebtStrategy.Avalanche);
        Assert.Equal(250_00, extra.MonthlyPaymentCents);
        Assert.True(extra.Months < plain.Months);
        Assert.True(extra.TotalInterestCents < plain.TotalInterestCents);
    }

    // Two debts, worked by hand: A $1,000 at 24% (min $50), B $500 at 12% (min $25), $100 extra = $175 a month.
    private static readonly DebtInput A = new(Guid.Parse("00000000-0000-0000-0000-00000000000a"), "A", 1_000_00, 2400, 50_00);
    private static readonly DebtInput B = new(Guid.Parse("00000000-0000-0000-0000-00000000000b"), "B", 500_00, 1200, 25_00);

    [Fact]
    public void Avalanche_sends_the_extra_to_the_highest_APR()
    {
        var r = DebtPayoff.Simulate([A, B], 100_00, DebtStrategy.Avalanche);
        // Month 1: A 1,000 + 20.00 interest − 50 − 100 = 870.00; B 500 + 5.00 − 25 = 480.00.
        Assert.Equal((87_000L, 48_000L, 25_00L), (r.Schedule[0].BalancesCents[A.Id], r.Schedule[0].BalancesCents[B.Id], r.Schedule[0].InterestCents));
        // Month 2: A 870 + 17.40 − 150 = 737.40; B 480 + 4.80 − 25 = 459.80.
        Assert.Equal((73_740L, 45_980L), (r.Schedule[1].BalancesCents[A.Id], r.Schedule[1].BalancesCents[B.Id]));
        Assert.True(r.Debts.Single(d => d.Id == A.Id).PaidOffInMonth < r.Debts.Single(d => d.Id == B.Id).PaidOffInMonth);
    }

    [Fact]
    public void Snowball_sends_the_extra_to_the_smallest_balance()
    {
        var r = DebtPayoff.Simulate([A, B], 100_00, DebtStrategy.Snowball);
        // Month 1: A 1,020 − 50 = 970.00; B 505 − 25 − 100 = 380.00.
        Assert.Equal((97_000L, 38_000L), (r.Schedule[0].BalancesCents[A.Id], r.Schedule[0].BalancesCents[B.Id]));
        Assert.True(r.Debts.Single(d => d.Id == B.Id).PaidOffInMonth < r.Debts.Single(d => d.Id == A.Id).PaidOffInMonth);
    }

    [Fact]
    public void Avalanche_never_costs_more_interest_than_snowball_here_and_the_payment_stays_level()
    {
        var avalanche = DebtPayoff.Simulate([A, B], 100_00, DebtStrategy.Avalanche);
        var snowball = DebtPayoff.Simulate([A, B], 100_00, DebtStrategy.Snowball);
        Assert.True(avalanche.TotalInterestCents < snowball.TotalInterestCents);
        // Freed-up minimums roll over: every month but the last pays the full $175.
        Assert.All(avalanche.Schedule.SkipLast(1), m => Assert.Equal(175_00, m.PaidCents));
        // Every dollar is accounted for: paid = what was owed + interest.
        Assert.Equal(1_500_00 + avalanche.TotalInterestCents, avalanche.TotalPaidCents);
        Assert.Equal(1_500_00 + snowball.TotalInterestCents, snowball.TotalPaidCents);
    }

    [Fact]
    public void Payments_smaller_than_the_interest_never_pay_off()
    {
        var r = DebtPayoff.Simulate([D("Card", 5_000_00, 2400, 50_00)], 0, DebtStrategy.Avalanche); // $100 interest a month
        Assert.True(r.NeverPaysOff);
        Assert.True(r.Months < DebtPayoff.HorizonMonths); // stops early instead of running 50 years
    }

    [Fact]
    public void Zero_percent_and_a_minimum_bigger_than_the_balance_are_handled()
    {
        var r = DebtPayoff.Simulate([D("Promo", 300_00, 0, 100_00), D("Tiny", 20_00, 2000, 35_00)], 0, DebtStrategy.Avalanche);
        Assert.Equal(0, r.Debts.Single(d => d.Name == "Promo").InterestCents);
        Assert.Equal(1, r.Debts.Single(d => d.Name == "Tiny").PaidOffInMonth); // 20.00 + 0.33 interest, paid in full
        // $135 a month: Tiny takes 20.33 and the leftover 14.67 rolls to Promo: 300 − 100 − 14.67 = 185.33, then 50.33, then paid.
        var promo = r.Debts.Single(d => d.Name == "Promo").Id;
        Assert.Equal((185_33L, 50_33L), (r.Schedule[0].BalancesCents[promo], r.Schedule[1].BalancesCents[promo]));
        Assert.Equal(3, r.Debts.Single(d => d.Name == "Promo").PaidOffInMonth);
    }

    [Fact]
    public void Nothing_owed_means_nothing_to_do()
    {
        var r = DebtPayoff.Simulate([D("Paid", 0, 2000, 25_00)], 0, DebtStrategy.Avalanche);
        Assert.Equal((0, false, 0L), (r.Months, r.NeverPaysOff, r.TotalInterestCents));
    }

    [Theory]
    [InlineData(1_000_00, 2499, 20_83)] // 1,000 × 24.99% ÷ 12 = 20.825 → 20.83
    [InlineData(4_925_00, 1800, 73_88)]
    [InlineData(1, 100, 0)]
    public void Monthly_interest_rounds_half_away_from_zero(long balance, int aprBps, long expected) =>
        Assert.Equal(expected, DebtPayoff.MonthlyInterest(balance, aprBps));
}
