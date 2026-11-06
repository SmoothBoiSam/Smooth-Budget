using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Core.Tests;

public class LeftToSpendCalculatorTests
{
    // November 2026 budget month: Fri 11-06 through Sun 12-06 (31 days).
    private static readonly BudgetMonthRange Nov = new BudgetMonthCalendar().MonthOf(2026, 11);
    private static readonly DateOnly Today = new(2026, 11, 20); // 17 days remaining, counting today

    private static readonly Guid Groceries = Guid.NewGuid();
    private static readonly Guid EatingOut = Guid.NewGuid();
    private static readonly Guid Gas = Guid.NewGuid();
    private static readonly Guid Rent = Guid.NewGuid();
    private static readonly Guid CardPayment = Guid.NewGuid();
    private static readonly Guid Savings = Guid.NewGuid();

    private static readonly BudgetCategory[] Categories =
    [
        new(Groceries, CategoryType.Variable, 450_00),
        new(EatingOut, CategoryType.Variable, 150_00),
        new(Gas, CategoryType.Variable, 160_00),
        new(Rent, CategoryType.FixedBill, 1_650_00),
        new(CardPayment, CategoryType.DebtPayment, 300_00),
        new(Savings, CategoryType.SavingsTransfer, 100_00),
    ];

    private static LeftToSpendResult Calc(IEnumerable<BudgetTransaction> txns, long carried = 0, DateOnly? today = null) =>
        LeftToSpendCalculator.Calculate(Nov, today ?? Today, Categories, txns, carried);

    private static BudgetTransaction Spend(Guid? category, long cents, int day = 10, int month = 11, bool pending = false, bool transfer = false, decimal split = 1m) =>
        new(category, new DateOnly(2026, month, day), cents, transfer, pending, split);

    [Fact]
    public void No_spending_leaves_the_whole_variable_budget()
    {
        var r = Calc([]);
        Assert.Equal(760_00, r.LeftToSpendCents);
        Assert.Equal(760_00, r.TotalVariableLimitCents);
        Assert.Equal(0, r.TotalVariableSpentCents);
        Assert.Equal(LeftToSpendStatus.Healthy, r.Status);
    }

    [Fact]
    public void Sums_limit_minus_spent_across_variable_categories()
    {
        var r = Calc([Spend(Groceries, 82_17), Spend(Groceries, 64_30), Spend(EatingOut, 18_45), Spend(Gas, 44_10)]);
        Assert.Equal(760_00 - 82_17 - 64_30 - 18_45 - 44_10, r.LeftToSpendCents);
        Assert.Equal(82_17 + 64_30 + 18_45 + 44_10, r.TotalVariableSpentCents);
        Assert.Equal(146_47, r.Categories.Single(c => c.CategoryId == Groceries).SpentCents);
    }

    [Fact]
    public void Fixed_bills_debt_payments_and_savings_do_not_reduce_left_to_spend()
    {
        var r = Calc([Spend(Rent, 1_650_00), Spend(CardPayment, 300_00), Spend(Savings, 100_00)]);
        Assert.Equal(760_00, r.LeftToSpendCents);
    }

    [Fact]
    public void Pending_transactions_count_immediately()
    {
        var r = Calc([Spend(EatingOut, 25_00, pending: true)]);
        Assert.Equal(735_00, r.LeftToSpendCents);
    }

    [Fact]
    public void Transfers_are_never_spending_even_when_categorized_as_variable()
    {
        var r = Calc([Spend(Groceries, 300_00, transfer: true), Spend(null, 500_00, transfer: true)]);
        Assert.Equal(760_00, r.LeftToSpendCents);
        Assert.Equal(0, r.UncategorizedSpendingCount);
    }

    [Fact]
    public void Refunds_reduce_spent()
    {
        var r = Calc([Spend(Groceries, 100_00), Spend(Groceries, -12_00)]);
        Assert.Equal(88_00, r.Categories.Single(c => c.CategoryId == Groceries).SpentCents);
        Assert.Equal(672_00, r.LeftToSpendCents);
    }

    [Fact]
    public void Refunds_never_push_a_category_below_zero_so_Left_to_Spend_is_never_inflated()
    {
        // A mislabeled $810 deposit in Eating out must not add $810 to what's left to spend.
        var r = Calc([Spend(EatingOut, 30_00), Spend(EatingOut, -810_00), Spend(Groceries, 100_00)]);
        Assert.Equal(0, r.Categories.Single(c => c.CategoryId == EatingOut).SpentCents);
        Assert.Equal(150_00, r.Categories.Single(c => c.CategoryId == EatingOut).RemainingCents);
        Assert.Equal(760_00 - 100_00, r.LeftToSpendCents);
        Assert.Equal(100_00, r.TotalVariableSpentCents);
    }

    [Fact]
    public void Only_transactions_inside_the_budget_month_count_and_the_boundaries_are_inclusive()
    {
        var r = Calc(
        [
            Spend(Groceries, 1_00, day: 5),            // 11-05: October budget month
            Spend(Groceries, 10_00, day: 6),           // 11-06: first day, counts
            Spend(Groceries, 100_00, day: 6, month: 12), // 12-06: last day, counts
            Spend(Groceries, 1_000_00, day: 7, month: 12), // 12-07: December budget month
        ]);
        Assert.Equal(110_00, r.TotalVariableSpentCents);
    }

    [Fact]
    public void Overspending_one_category_reduces_the_shared_total()
    {
        // Eating out is $22.35 over; that comes out of what is left for everything else.
        var r = Calc([Spend(EatingOut, 172_35), Spend(Groceries, 100_00)]);
        Assert.Equal(760_00 - 172_35 - 100_00, r.LeftToSpendCents);
        Assert.Equal(1, r.OverBudgetCategoryCount);
        Assert.Equal(22_35, r.OverBudgetCents);
        Assert.Equal(-22_35, r.Categories.Single(c => c.CategoryId == EatingOut).RemainingCents);
    }

    [Fact]
    public void Over_budget_line_counts_and_sums_every_overspent_category()
    {
        var r = Calc([Spend(EatingOut, 160_00), Spend(Gas, 175_50), Spend(Groceries, 450_00)]);
        Assert.Equal(2, r.OverBudgetCategoryCount); // exactly at the limit is not over
        Assert.Equal(10_00 + 15_50, r.OverBudgetCents);
    }

    [Fact]
    public void Carried_in_deficit_is_subtracted()
    {
        var r = Calc([Spend(Groceries, 100_00)], carried: 42_50);
        Assert.Equal(760_00 - 100_00 - 42_50, r.LeftToSpendCents);
        Assert.Equal(42_50, r.CarriedInDeficitCents);
    }

    [Fact]
    public void Negative_carried_in_deficit_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Calc([], carried: -1));
    }

    [Fact]
    public void Left_to_spend_can_go_negative_and_turns_red()
    {
        var r = Calc([Spend(Groceries, 700_00), Spend(Gas, 100_00)], carried: 10_00);
        Assert.Equal(-50_00, r.LeftToSpendCents);
        Assert.Equal(LeftToSpendStatus.Overspent, r.Status);
        Assert.Equal(0, r.SafeToSpendPerDayCents);
    }

    [Fact]
    public void Split_transactions_count_only_the_owners_share()
    {
        // SPEC section 3: a $120 split purchase counts as $60.
        var r = Calc([Spend(Groceries, 120_00, split: 0.5m)]);
        Assert.Equal(60_00, r.TotalVariableSpentCents);
    }

    [Theory]
    [InlineData(1_01, 0.5, 51)]      // 50.5 cents rounds away from zero
    [InlineData(-1_01, 0.5, -51)]    // refunds round symmetrically
    [InlineData(10_00, 0.333, 3_33)]
    [InlineData(10_00, 0, 0)]
    public void Owner_share_rounds_half_away_from_zero(long amount, double split, long expected)
    {
        Assert.Equal(expected, LeftToSpendCalculator.OwnerShare(Spend(Groceries, amount, split: (decimal)split)));
    }

    [Fact]
    public void Split_factor_outside_0_to_1_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Calc([Spend(Groceries, 10_00, split: 1.5m)]));
    }

    [Fact]
    public void Safe_to_spend_per_day_divides_by_days_remaining_and_rounds_down()
    {
        // 478.67 / 17 = 28.157... -> $28.15
        var r = Calc([Spend(Groceries, 281_33)]);
        Assert.Equal(478_67, r.LeftToSpendCents);
        Assert.Equal(17, r.DaysRemaining);
        Assert.Equal(28_15, r.SafeToSpendPerDayCents);
    }

    [Fact]
    public void On_the_last_day_the_whole_amount_is_safe_today()
    {
        var r = Calc([], today: new DateOnly(2026, 12, 6));
        Assert.Equal(1, r.DaysRemaining);
        Assert.Equal(760_00, r.SafeToSpendPerDayCents);
    }

    [Fact]
    public void Today_outside_the_budget_month_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Calc([], today: new DateOnly(2026, 12, 7)));
    }

    [Fact]
    public void Uncategorized_purchases_are_counted_but_not_spent_so_they_are_never_silently_missing()
    {
        var r = Calc(
        [
            Spend(null, 15_00),                // uncategorized purchase
            Spend(Guid.NewGuid(), 9_00),       // category we do not know about
            Spend(null, -1_700_86),            // paycheck: money in, not an uncategorized purchase
        ]);
        Assert.Equal(2, r.UncategorizedSpendingCount);
        Assert.Equal(760_00, r.LeftToSpendCents);
    }

    [Theory]
    [InlineData(114_00, LeftToSpendStatus.Healthy)] // exactly 15% of 760.00
    [InlineData(113_99, LeftToSpendStatus.Low)]
    [InlineData(1, LeftToSpendStatus.Low)]
    [InlineData(0, LeftToSpendStatus.Low)]
    [InlineData(-1, LeftToSpendStatus.Overspent)]
    public void Status_is_green_yellow_red_around_the_15_percent_threshold(long left, LeftToSpendStatus expected)
    {
        Assert.Equal(expected, LeftToSpendCalculator.StatusFor(left, 760_00));
    }

    [Fact]
    public void Status_with_no_variable_budget()
    {
        Assert.Equal(LeftToSpendStatus.Low, LeftToSpendCalculator.StatusFor(0, 0));
        Assert.Equal(LeftToSpendStatus.Overspent, LeftToSpendCalculator.StatusFor(-5, 0));
    }
}
