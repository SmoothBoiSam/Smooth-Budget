using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Core.Tests;

/// <summary>The automatic budget fits the month's plan to the money coming in (DECISIONS.md 136).</summary>
public class BudgetFitterTests
{
    private static readonly Guid Rent = Guid.NewGuid(), Card = Guid.NewGuid(), Save = Guid.NewGuid(),
        Groceries = Guid.NewGuid(), Gas = Guid.NewGuid(), EatingOut = Guid.NewGuid(), Shopping = Guid.NewGuid();

    private static ProposedLimit L(Guid id, string name, CategoryType type, long cents, bool discretionary = false) =>
        new(id, name, type, cents, cents, cents, $"Usually {cents / 100}.", [cents], discretionary ? CategoryPriority.Low : CategoryPriority.Medium);

    /// <summary>Bills 1,650 + card minimums 160 + savings habit 100 + groceries 400 + gas 150 + eating out 200 + shopping 100 = 2,760.</summary>
    private static BudgetProposal Plan(long income) => new(3, income, "Payday forecast.", [], [
        L(Rent, "Rent", CategoryType.FixedBill, 1_650_00),
        L(Card, "Credit card payment", CategoryType.DebtPayment, 160_00),
        L(Save, "Savings", CategoryType.SavingsTransfer, 100_00),
        L(Groceries, "Groceries", CategoryType.Variable, 400_00),
        L(Gas, "Gas", CategoryType.Variable, 150_00),
        L(EatingOut, "Eating out", CategoryType.Variable, 200_00, discretionary: true),
        L(Shopping, "Shopping", CategoryType.Variable, 100_00, discretionary: true),
    ], 2_760_00, income - 2_760_00, income < 2_760_00, 0);

    private static readonly Dictionary<Guid, long> NothingSpent = [];
    private static long Limit(FittedBudget f, Guid id) => f.Proposal.Categories.Single(c => c.CategoryId == id).ProposedCents;

    [Fact]
    public void Enough_money_changes_nothing()
    {
        var f = BudgetFitter.Fit(Plan(3_000_00), NothingSpent, new HashSet<Guid>(), savingsFromGoals: false);
        Assert.Equal((0L, 0L, 240_00L), (f.ShortCents, f.SqueezedCents, f.Proposal.UnallocatedCents));
    }

    [Fact]
    public void Nice_to_have_spending_shrinks_first_in_proportion_then_the_savings_habit()
    {
        // $250 short: it all comes off eating out and shopping (2 to 1, like their room), so the savings habit stays (DECISIONS.md 182).
        var f = BudgetFitter.Fit(Plan(2_510_00), NothingSpent, new HashSet<Guid>(), savingsFromGoals: false);
        Assert.Equal((0L, 250_00L), (f.ShortCents, f.SqueezedCents));
        Assert.Equal((100_00L, 33_00L, 17_00L), (Limit(f, Save), Limit(f, EatingOut), Limit(f, Shopping)));
        Assert.Equal((400_00L, 150_00L, 1_650_00L, 160_00L), (Limit(f, Groceries), Limit(f, Gas), Limit(f, Rent), Limit(f, Card)));
        Assert.Contains("Squeezed by $167.00 (from $200.00)", f.Proposal.Categories.Single(c => c.CategoryId == EatingOut).Explanation);
        Assert.Equal(2_510_00, f.Proposal.AllocatedCents);
    }

    [Fact]
    public void Everyday_spending_goes_last_and_below_its_floor_only_when_the_money_still_runs_short()
    {
        // $800 short: nice to have 300 + savings 100 + everyday down to 80% (groceries 320, gas 120: 110 more) = 510.
        // DECISIONS.md 226: the last 290 comes off everyday spending too (in proportion, 211 and 79), so the limits never
        // add up to more than the month's money.
        var f = BudgetFitter.Fit(Plan(1_960_00), NothingSpent, new HashSet<Guid>(), savingsFromGoals: false);
        Assert.Equal((109_00L, 41_00L, 0L, 0L), (Limit(f, Groceries), Limit(f, Gas), Limit(f, EatingOut), Limit(f, Shopping)));
        Assert.Equal((0L, 800_00L), (f.ShortCents, f.SqueezedCents));
        Assert.False(f.Proposal.SpendsMoreThanIncome);
        Assert.EndsWith("Squeezed by $291.00 (from $400.00) to fit this month's money.", f.Proposal.Categories.Single(c => c.CategoryId == Groceries).Explanation);

        // Only bills and card minimums costing more than the month brings in is a shortfall.
        var bare = BudgetFitter.Fit(Plan(1_700_00), NothingSpent, new HashSet<Guid>(), savingsFromGoals: false);
        Assert.Equal((0L, 0L, 110_00L), (Limit(bare, Groceries), Limit(bare, Gas), bare.ShortCents));
    }

    [Fact]
    public void In_a_tight_month_the_cards_get_their_minimums_after_savings_and_before_everyday_spending()
    {
        // DECISIONS.md 226: the card line holds the $160 minimums and Card interest ($40) sits on top. The minimums include
        // the interest, so in a pinch the $40 goes: after nice-to-have spending and savings, before groceries and gas.
        var interest = Guid.NewGuid();
        var plan = Plan(2_350_00);
        plan = plan with
        {
            CardMinimumsCents = 160_00, AllocatedCents = 2_800_00,
            Categories = [.. plan.Categories, L(interest, "Card interest", CategoryType.FixedBill, 40_00)],
        };
        // $450 short: 300 + 100 + 40 interest, then 10 off everyday spending (8 from groceries, 2 from gas).
        var f = BudgetFitter.Fit(plan, NothingSpent, new HashSet<Guid>(), savingsFromGoals: false);
        Assert.Equal((0L, 160_00L, 392_00L, 148_00L), (Limit(f, interest), Limit(f, Card), Limit(f, Groceries), Limit(f, Gas)));
        Assert.Contains("this month the cards get their minimums, $160.00", f.Proposal.Categories.Single(c => c.CategoryId == interest).Explanation);

        // $25 of interest already charged this month stays, and it counts toward the minimums: the card line gives the
        // rest. Without the minimums known, the card lines stay as they are.
        var charged = BudgetFitter.Fit(plan, new Dictionary<Guid, long> { [interest] = 25_00 }, new HashSet<Guid>(), savingsFromGoals: false);
        Assert.Equal((25_00L, 135_00L), (Limit(charged, interest), Limit(charged, Card)));
        var unknown = BudgetFitter.Fit(plan with { CardMinimumsCents = null }, NothingSpent, new HashSet<Guid>(), savingsFromGoals: false);
        Assert.Equal(40_00, Limit(unknown, interest));
    }

    [Fact]
    public void Must_have_spending_is_squeezed_only_after_important_spending_is_at_its_floor()
    {
        // Groceries marked must have, gas left important (DECISIONS.md 182). $480 short: nice to have 300, the savings habit 100,
        // then gas gives its 30 (down to 80%, 120) before groceries gives 50 of its 80.
        var plan = Plan(2_280_00);
        plan = plan with { Categories = plan.Categories.Select(c => c.CategoryId == Groceries ? c with { Priority = CategoryPriority.High } : c).ToList() };
        var f = BudgetFitter.Fit(plan, NothingSpent, new HashSet<Guid>(), savingsFromGoals: false);
        Assert.Equal((120_00L, 350_00L), (Limit(f, Gas), Limit(f, Groceries)));
        Assert.Equal((0L, 480_00L), (f.ShortCents, f.SqueezedCents));

        // $360 short: nice-to-have spending (300) and the savings habit (60) cover it, so gas and groceries stay whole.
        var light = BudgetFitter.Fit(Plan(2_400_00) with { Categories = plan.Categories }, NothingSpent, new HashSet<Guid>(), savingsFromGoals: false);
        Assert.Equal((150_00L, 400_00L), (Limit(light, Gas), Limit(light, Groceries)));
    }

    [Fact]
    public void Nothing_goes_below_what_is_already_spent_and_your_own_limits_stay()
    {
        var spent = new Dictionary<Guid, long> { [EatingOut] = 180_00 };
        var f = BudgetFitter.Fit(Plan(2_510_00), spent, new HashSet<Guid> { Shopping }, savingsFromGoals: false);
        // Eating out can only give 20 (180 spent); shopping is yours; savings 100; everyday spending gives 110 down to its
        // 80% floors (groceries 320, gas 120), and the last $20 below them (DECISIONS.md 226).
        Assert.Equal((0L, 180_00L, 100_00L), (Limit(f, Save), Limit(f, EatingOut), Limit(f, Shopping)));
        Assert.Equal((250_00L, 0L), (f.SqueezedCents, f.ShortCents));
        Assert.Equal((305_00L, 115_00L), (Limit(f, Groceries), Limit(f, Gas)));
    }

    [Fact]
    public void Every_line_is_at_least_what_was_already_spent_or_paid_this_month()
    {
        // DECISIONS.md 173, 268: rent went up to $1,800, $300 already went to the card and groceries are $50 over; that money
        // is gone, so the plan holds it. The $340 more takes all $300 of nice-to-have spending, then $40 of the savings habit.
        var spent = new Dictionary<Guid, long> { [Rent] = 1_800_00, [Card] = 300_00, [Groceries] = 450_00 };
        var f = BudgetFitter.Fit(Plan(2_760_00), spent, new HashSet<Guid>(), savingsFromGoals: false);
        Assert.Equal((1_800_00L, 300_00L, 450_00L), (Limit(f, Rent), Limit(f, Card), Limit(f, Groceries)));
        Assert.Equal((60_00L, 0L), (Limit(f, Save), Limit(f, EatingOut) + Limit(f, Shopping)));
        Assert.Equal((0L, 340_00L), (f.ShortCents, f.SqueezedCents));
        Assert.EndsWith("Raised to $1,800.00, what's already been paid this month.", f.Proposal.Categories.Single(c => c.CategoryId == Rent).Explanation);
        Assert.EndsWith("Raised to $450.00, what's already been spent this month.", f.Proposal.Categories.Single(c => c.CategoryId == Groceries).Explanation);
        // DECISIONS.md 223: the unsqueezed plan keeps the raise but not the squeeze, even when both happen.
        var before = f.Unsqueezed!.Categories;
        Assert.Equal((1_800_00L, 200_00L, 100_00L),
            (before.Single(c => c.CategoryId == Rent).ProposedCents, before.Single(c => c.CategoryId == EatingOut).ProposedCents, before.Single(c => c.CategoryId == Shopping).ProposedCents));

        // A limit you set is raised too, so the others make room; the automatic budget never writes it, so yours stays.
        var mine = BudgetFitter.Fit(Plan(3_000_00), spent, new HashSet<Guid> { Rent }, savingsFromGoals: false);
        Assert.Equal(1_800_00, Limit(mine, Rent));
    }

    [Fact]
    public void Savings_from_goals_are_left_to_the_generator()
    {
        // With a goals plan the generator only funds savings from a leftover, so the fitter doesn't touch that line.
        var f = BudgetFitter.Fit(Plan(2_660_00), NothingSpent, new HashSet<Guid>(), savingsFromGoals: true);
        Assert.Equal((100_00L, 200_00L), (Limit(f, Save), Limit(f, EatingOut) + Limit(f, Shopping))); // the $100 came off discretionary spending
    }

    [Fact]
    public void Payday_income_and_your_limits_feed_the_generator()
    {
        var month = new BudgetMonthCalendar().MonthContaining(new DateOnly(2026, 10, 20));
        var cats = new List<GeneratorCategory>
        {
            new(Groceries, "Groceries", CategoryType.Variable, CategoryPriority.Medium, 0), new(EatingOut, "Eating out", CategoryType.Variable, CategoryPriority.Low, 0),
        };
        var txns = new List<BudgetTransaction>
        {
            new(Groceries, new DateOnly(2026, 10, 10), 300_00), new(EatingOut, new DateOnly(2026, 10, 12), 150_00),
            new(null, new DateOnly(2026, 10, 22), -2_000_00, Kind: SmoothBudget.Core.Transactions.TransactionKind.Income),
        };
        var p = BudgetGenerator.Propose([month], cats, txns, hasCardDebt: false,
            overrides: new BudgetOverrides(1_234_00, "Your Payday forecast.", new Dictionary<Guid, long> { [EatingOut] = 99_00 }));
        Assert.Equal((1_234_00L, "Your Payday forecast."), (p.IncomeCents, p.IncomeExplanation));
        Assert.Equal((300_00L, 99_00L), (p.Categories[0].ProposedCents, p.Categories[1].ProposedCents));
        Assert.Equal(CategoryPriority.Low, p.Categories[1].Priority);
        Assert.Equal("You set this limit, so it stays as you set it.", p.Categories[1].Explanation);
    }
}
