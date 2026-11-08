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
    public void The_sweep_never_sends_money_that_isnt_there()
    {
        // DECISIONS.md 177-178: 100 under, but $30 is still unfiled and rent came in 40 over a plan that used all
        // 2,250 coming in. Only 30 is truly left over, and the close still equals the final Left to Spend.
        BudgetTransaction[] txns = [T(Groceries, 400_00), T(EatingOut, 100_00), T(Rent, 1_690_00), new(null, new DateOnly(2026, 11, 12), 30_00)];
        var r = MonthCloseCalculator.Close(Nov, Cats, txns, 0, moneyInCents: 2_250_00);
        Assert.Equal((30_00L, 0L), (r.SurplusCents, r.DeficitCents));
        Assert.Equal(LeftToSpendCalculator.Calculate(Nov, Nov.End, Cats, txns, 0, moneyInCents: 2_250_00).LeftToSpendCents, r.ClosingNetCents);
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
            t.Add(In(m, null, -760_00 - i * 100_00, kind: TransactionKind.Income)); // totals 3260.86, 3360.86, 3460.86
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
        Assert.Equal(3_360_86, p.IncomeCents);
        Assert.Equal(438_00 + 151_00 + 1_650_00 + 350_00 + 100_00, p.AllocatedCents);
        Assert.Equal(p.IncomeCents - p.AllocatedCents, p.UnallocatedCents);
        Assert.False(p.SpendsMoreThanIncome);
    }

    [Fact]
    public void With_known_cards_debt_is_the_minimums_plus_whats_left_to_the_highest_APR()
    {
        var visa = new SmoothBudget.Core.Debt.DebtInput(Guid.NewGuid(), "Visa", 2_000_00, 2499, 60_00);
        var mc = new SmoothBudget.Core.Debt.DebtInput(Guid.NewGuid(), "Mastercard", 800_00, 1999, 35_00);
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: true, cardDebts: [mc, visa]);

        // Everything else: groceries 438 + eating out 136 (trimmed) + rent 1,650 + savings 100 = 2,324.00.
        // Income 3,360.86 − 2,324.00 − minimums 95.00 = 941.86 extra.
        var card = p.Categories.Single(c => c.CategoryId == Card);
        Assert.Equal(95_00 + 941_86, card.ProposedCents);
        Assert.Equal("Your card minimums add up to $95.00, plus $941.86 left over after everything else, as extra to Visa (24.99% APR, the highest).", card.Explanation);
        Assert.Equal(0, p.UnallocatedCents);
    }

    [Fact]
    public void An_extra_set_on_the_debt_tab_drives_the_card_payment()
    {
        var visa = new SmoothBudget.Core.Debt.DebtInput(Guid.NewGuid(), "Visa", 2_000_00, 2499, 60_00);
        var mc = new SmoothBudget.Core.Debt.DebtInput(Guid.NewGuid(), "Mastercard", 800_00, 1999, 35_00);
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: true, cardDebts: [mc, visa], overrides: new BudgetOverrides(DebtExtraCents: 200_00));
        var card = p.Categories.Single(c => c.CategoryId == Card);
        Assert.Equal(95_00 + 200_00, card.ProposedCents); // minimums + your extra, not all 941.86 left over
        Assert.Contains("the extra you set on the Debt tab", card.Explanation);
        Assert.Equal(741_86, p.UnallocatedCents);

        // More than the month has left: only what's left.
        var big = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: true, cardDebts: [mc, visa], overrides: new BudgetOverrides(DebtExtraCents: 5_000_00));
        Assert.Equal(95_00 + 941_86, big.Categories.Single(c => c.CategoryId == Card).ProposedCents);
    }

    [Fact]
    public void A_bill_whose_price_rose_is_proposed_at_the_new_price()
    {
        // Rent was $1,650 for three months; the detected bill says it's $1,700 now.
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: false, currentBills: [new CurrentBill(Rent, "Maple Court", 1_700_00)]);
        var rent = p.Categories.Single(c => c.CategoryId == Rent);
        Assert.Equal((1_650_00L, 1_700_00L), (rent.MedianCents, rent.ProposedCents));
        Assert.Equal("Current price: Maple Court $1,700.00 a month (your 3-month median was $1,650.00).", rent.Explanation);
    }

    [Fact]
    public void A_bill_that_stopped_is_no_longer_planned()
    {
        // DECISIONS.md 174: rent was $1,650 a month and that bill has stopped, so the line drops to what's left of the usual.
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: false, currentBills: [new CurrentBill(Rent, "Maple Court", 1_650_00, Stopped: true)]);
        var rent = p.Categories.Single(c => c.CategoryId == Rent);
        Assert.Equal(0, rent.ProposedCents);
        Assert.Equal("Maple Court has stopped, so nothing is planned here.", rent.Explanation);

        // A stopped bill next to one still running: only the running one counts.
        var both = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: false,
            currentBills: [new CurrentBill(Rent, "Maple Court", 1_650_00, Stopped: true), new CurrentBill(Rent, "Oak Street", 1_500_00)]);
        Assert.Equal(1_500_00, both.Categories.Single(c => c.CategoryId == Rent).ProposedCents);
    }

    // Milestone 8: what's left goes in your priority order. Everything else: groceries 438 + eating out 136 (trimmed)
    // + rent 1,650 = 2,224.00; income 3,360.86 − 2,224.00 − Visa minimum 60.00 = 1,076.86 left.
    private static readonly SmoothBudget.Core.Debt.DebtInput Visa = new(Guid.NewGuid(), "Visa", 2_000_00, 2499, 60_00);
    private static SavingsPlan Plan(long remaining, IReadOnlyList<PriorityStep>? order = null, long? larger = null) =>
        new(order ?? SavingsPlan.DefaultOrder, "Rainy-day fund", remaining, 1_000_00 - remaining, 1_000_00, larger);

    [Fact]
    public void The_starter_fund_is_funded_first_then_whats_left_goes_to_debt()
    {
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: true, cardDebts: [Visa], savings: Plan(750_00));
        var savings = p.Categories.Single(c => c.CategoryId == Savings);
        var card = p.Categories.Single(c => c.CategoryId == Card);
        Assert.Equal((750_00L, 60_00L + 326_86L), (savings.ProposedCents, card.ProposedCents));
        Assert.Equal("$750.00 toward your rainy-day fund ($250.00 of $1,000.00 saved), first in your priorities.", savings.Explanation);
        Assert.Contains("plus $326.86 left over after your rainy-day fund", card.Explanation);
        Assert.Equal(0, p.UnallocatedCents);
    }

    [Fact]
    public void Once_the_starter_fund_is_full_everything_left_goes_to_debt()
    {
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: true, cardDebts: [Visa], savings: Plan(0));
        Assert.Equal(0, p.Categories.Single(c => c.CategoryId == Savings).ProposedCents);
        Assert.Equal(60_00 + 1_076_86, p.Categories.Single(c => c.CategoryId == Card).ProposedCents);
        Assert.Equal("Your rainy-day fund is full.", p.Categories.Single(c => c.CategoryId == Savings).Explanation);
    }

    [Fact]
    public void The_order_is_yours_to_change()
    {
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: true, cardDebts: [Visa],
            savings: Plan(750_00, [PriorityStep.Debt, PriorityStep.StarterFund, PriorityStep.Savings]));
        Assert.Equal((0L, 60_00L + 1_076_86L), (p.Categories.Single(c => c.CategoryId == Savings).ProposedCents, p.Categories.Single(c => c.CategoryId == Card).ProposedCents));
    }

    [Fact]
    public void Without_card_debt_the_starter_fund_then_larger_savings_share_whats_left()
    {
        // No card debt: eating out isn't trimmed (151) and the card line keeps its median (350), so
        // 3,360.86 − (438 + 151 + 1,650 + 350) = 771.86 left: 300 to the starter fund, 200 to larger goals.
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: false, savings: Plan(300_00, larger: 200_00));
        Assert.Equal(300_00 + 200_00, p.Categories.Single(c => c.CategoryId == Savings).ProposedCents);
        Assert.Equal(271_86, p.UnallocatedCents); // beyond what you set for larger goals: shown, not hidden
    }

    [Fact]
    public void When_nothing_is_left_the_debt_proposal_is_just_the_minimums()
    {
        var visa = new SmoothBudget.Core.Debt.DebtInput(Guid.NewGuid(), "Visa", 2_000_00, 2499, 60_00);
        var lowIncome = History().Where(t => t.Kind != TransactionKind.Income).ToList();
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, lowIncome, hasCardDebt: true, cardDebts: [visa]);
        Assert.Equal(60_00, p.Categories.Single(c => c.CategoryId == Card).ProposedCents);
        Assert.True(p.SpendsMoreThanIncome);
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
