using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Tests;

public class MonthCloseCalculatorTests
{
    private static readonly BudgetMonthRange Nov = new BudgetMonthCalendar().MonthOf(2026, 11);
    private static readonly Guid Groceries = Guid.NewGuid(), EatingOut = Guid.NewGuid(), Rent = Guid.NewGuid();
    private static readonly BudgetCategory[] Cats =
    [
        new(Groceries, CategoryType.Variable, 450_00),
        new(EatingOut, CategoryType.Variable, 150_00),
        new(Rent, CategoryType.FixedBill, 1_650_00),
    ];

    private static BudgetTransaction T(Guid c, long cents, int day = 10) => new(c, new DateOnly(2026, 11, day), cents);

    [Fact]
    public void Underspending_becomes_a_surplus_and_nothing_carries()
    {
        var r = MonthCloseCalculator.Close(Nov, Cats, [T(Groceries, 400_00), T(EatingOut, 100_00), T(Rent, 1_650_00)], 0);
        Assert.Equal((100_00L, 0L, 100_00L, 100_00L, 0L), (r.TotalUnspentCents, r.TotalOverspentCents, r.ClosingNetCents, r.SurplusCents, r.DeficitCents));
    }

    [Fact]
    public void Overspending_one_category_is_netted_against_underspending_in_another()
    {
        // Groceries 50 under, eating out 80 over: net 30 over -> carries.
        var r = MonthCloseCalculator.Close(Nov, Cats, [T(Groceries, 400_00), T(EatingOut, 230_00)], 0);
        Assert.Equal((50_00L, 80_00L, -30_00L, 0L, 30_00L), (r.TotalUnspentCents, r.TotalOverspentCents, r.ClosingNetCents, r.SurplusCents, r.DeficitCents));
        Assert.Equal(-80_00, r.Categories.Single(c => c.CategoryId == EatingOut).RemainingCents); // each category keeps its own over/under
    }

    [Fact]
    public void A_deficit_carried_in_is_paid_back_before_anything_is_a_surplus()
    {
        // 100 under this month, but 42.50 came in as a deficit: only 57.50 is truly left over.
        var r = MonthCloseCalculator.Close(Nov, Cats, [T(Groceries, 400_00), T(EatingOut, 100_00)], 42_50);
        Assert.Equal((57_50L, 0L), (r.SurplusCents, r.DeficitCents));

        var worse = MonthCloseCalculator.Close(Nov, Cats, [T(Groceries, 450_00), T(EatingOut, 150_00)], 42_50);
        Assert.Equal((0L, 42_50L), (worse.SurplusCents, worse.DeficitCents)); // an even month keeps the debt
    }

    [Fact]
    public void Closing_net_equals_the_final_left_to_spend()
    {
        BudgetTransaction[] txns = [T(Groceries, 419_52), T(EatingOut, 172_35)];
        var close = MonthCloseCalculator.Close(Nov, Cats, txns, 42_50);
        var lts = LeftToSpendCalculator.Calculate(Nov, Nov.End, Cats, txns, 42_50);
        Assert.Equal(lts.LeftToSpendCents, close.ClosingNetCents);
    }

    [Fact]
    public void Bills_debt_and_savings_never_affect_the_close()
    {
        var r = MonthCloseCalculator.Close(Nov, Cats, [T(Rent, 1_700_00)], 0);
        Assert.Equal(600_00, r.SurplusCents);
    }
}

public class SweepPlannerTests
{
    [Fact]
    public void Default_sends_everything_to_the_card()
    {
        var items = SweepPlanner.Plan(119_03, SweepPreference.Default, "Demo Visa", "Demo Savings");
        Assert.Equal((SweepKind.Debt, 119_03L, "Demo Visa"), (items.Single().Kind, items.Single().AmountCents, items.Single().Destination));
    }

    [Fact]
    public void Savings_preference_sends_everything_to_savings()
    {
        var items = SweepPlanner.Plan(100_00, new SweepPreference(SweepTarget.Savings), "Demo Visa", "Demo Savings");
        Assert.Equal((SweepKind.Savings, 100_00L), (items.Single().Kind, items.Single().AmountCents));
    }

    [Fact]
    public void Split_rounds_savings_down_and_gives_the_odd_cent_to_debt()
    {
        var items = SweepPlanner.Plan(100_01, new SweepPreference(SweepTarget.Split, 25), "Demo Visa", "Demo Savings");
        Assert.Equal(25_00, items.Single(i => i.Kind == SweepKind.Savings).AmountCents);
        Assert.Equal(75_01, items.Single(i => i.Kind == SweepKind.Debt).AmountCents);
        Assert.Equal(100_01, items.Sum(i => i.AmountCents));
    }

    [Fact]
    public void Missing_destinations_fall_back_to_the_other_kind()
    {
        Assert.Equal(SweepKind.Savings, SweepPlanner.Plan(10_00, SweepPreference.Default, null, "Demo Savings").Single().Kind);
        Assert.Equal(SweepKind.Debt, SweepPlanner.Plan(10_00, new SweepPreference(SweepTarget.Savings), "Demo Visa", null).Single().Kind);
        Assert.Equal("debt or savings", SweepPlanner.Plan(10_00, SweepPreference.Default, null, null).Single().Destination);
    }

    [Fact]
    public void No_surplus_no_items()
    {
        Assert.Empty(SweepPlanner.Plan(0, SweepPreference.Default, "Demo Visa", "Demo Savings"));
    }
}

public class BudgetGeneratorTests
{
    private static readonly BudgetMonthCalendar Cal = new();
    private static readonly BudgetMonthRange[] ThreeMonths = [Cal.MonthOf(2026, 8), Cal.MonthOf(2026, 9), Cal.MonthOf(2026, 10)];
    private static readonly Guid Groceries = Guid.NewGuid(), EatingOut = Guid.NewGuid(), Rent = Guid.NewGuid(), Card = Guid.NewGuid(), Savings = Guid.NewGuid();

    private static readonly GeneratorCategory[] Cats =
    [
        new(Groceries, "Groceries", CategoryType.Variable, false, 400_00),
        new(EatingOut, "Eating out", CategoryType.Variable, true, 150_00),
        new(Rent, "Rent", CategoryType.FixedBill, false, 1_650_00),
        new(Card, "Credit card payment", CategoryType.DebtPayment, false, 300_00),
        new(Savings, "Rainy-day fund", CategoryType.SavingsTransfer, false, 100_00),
    ];

    private static BudgetTransaction In(BudgetMonthRange m, Guid? cat, long cents, bool transfer = false, TransactionKind kind = TransactionKind.Purchase) =>
        new(cat, m.Start.AddDays(3), cents, transfer, false, 1m, kind);

    private static List<BudgetTransaction> History(bool oneOffSplurge = false)
    {
        var t = new List<BudgetTransaction>();
        long[] groceries = [410_25, 455_10, 438_00];
        long[] eating = [120_00, oneOffSplurge ? 600_00 : 180_00, 150_40];
        for (var i = 0; i < 3; i++)
        {
            var m = ThreeMonths[i];
            t.Add(In(m, Groceries, groceries[i]));
            t.Add(In(m, EatingOut, eating[i]));
            t.Add(In(m, Rent, 1_650_00));
            t.Add(In(m, Card, 300_00 + i * 50_00, transfer: true)); // 300, 350, 400
            t.Add(In(m, Savings, 100_00, transfer: true));
            t.Add(In(m, null, -1_700_86, kind: TransactionKind.Income));
            t.Add(In(m, null, -800_00, kind: TransactionKind.Income));
            t.Add(In(m, null, -760_00 - i * 100_00, kind: TransactionKind.Income)); // totals 3332.86, 3432.86, 3532.86
        }
        return t;
    }

    [Fact]
    public void Proposes_medians_with_explanations()
    {
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: false);

        Assert.Equal(3, p.MonthsUsed);
        var g = p.Categories.Single(c => c.CategoryId == Groceries);
        Assert.Equal((438_00L, 438_00L), (g.MedianCents, g.ProposedCents));
        Assert.Equal("Your 3-month median is $438.00.", g.Explanation);
        Assert.Equal([410_25, 455_10, 438_00], g.MonthlyCents);

        Assert.Equal(1_650_00, p.Categories.Single(c => c.CategoryId == Rent).ProposedCents);
        Assert.Equal(350_00, p.Categories.Single(c => c.CategoryId == Card).ProposedCents); // card payments count toward the debt category
        Assert.Equal(100_00, p.Categories.Single(c => c.CategoryId == Savings).ProposedCents);
    }

    [Fact]
    public void A_one_off_splurge_does_not_inflate_the_suggestion()
    {
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(oneOffSplurge: true), hasCardDebt: false);
        Assert.Equal(151_00, p.Categories.Single(c => c.CategoryId == EatingOut).ProposedCents); // median 150.40, rounded up to the dollar
    }

    [Fact]
    public void Discretionary_categories_are_trimmed_while_there_is_card_debt()
    {
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: true);
        var e = p.Categories.Single(c => c.CategoryId == EatingOut);
        Assert.Equal(136_00, e.ProposedCents); // 150.40 * 90% = 135.36 -> $136
        Assert.Contains("trimmed 10% while you have card debt", e.Explanation);
        Assert.Equal(438_00, p.Categories.Single(c => c.CategoryId == Groceries).ProposedCents); // not discretionary
    }

    [Fact]
    public void Income_is_the_median_month_and_the_remainder_is_explicit()
    {
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: false);
        Assert.Equal(3_432_86, p.IncomeCents);
        Assert.Equal(438_00 + 151_00 + 1_650_00 + 350_00 + 100_00, p.AllocatedCents);
        Assert.Equal(p.IncomeCents - p.AllocatedCents, p.UnallocatedCents);
        Assert.False(p.SpendsMoreThanIncome);
    }

    [Fact]
    public void Warns_when_the_budget_spends_more_than_income()
    {
        var lowIncome = History().Where(t => t.Kind != TransactionKind.Income).ToList();
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, lowIncome, hasCardDebt: false);
        Assert.True(p.SpendsMoreThanIncome);
        Assert.True(p.UnallocatedCents < 0);
    }

    [Fact]
    public void Uncategorized_purchases_in_the_history_are_counted_so_the_user_can_fix_them()
    {
        var t = History();
        t.Add(In(ThreeMonths[1], null, 25_00));
        Assert.Equal(1, BudgetGenerator.Propose(ThreeMonths, Cats, t, false).UncategorizedCount);
    }

    [Fact]
    public void Without_history_current_limits_are_kept()
    {
        var p = BudgetGenerator.Propose([], Cats, [], false);
        Assert.Equal(0, p.MonthsUsed);
        Assert.All(p.Categories, c => Assert.Equal(c.CurrentLimitCents, c.ProposedCents));
    }

    [Theory]
    [InlineData(new long[] { 3, 1, 2 }, 2)]
    [InlineData(new long[] { 1, 2, 3, 4 }, 3)] // 2.5 rounds half up
    [InlineData(new long[] { 0, 0, 100 }, 0)]
    [InlineData(new long[] { }, 0)]
    public void Median(long[] values, long expected)
    {
        Assert.Equal(expected, BudgetGenerator.Median(values));
    }
}
