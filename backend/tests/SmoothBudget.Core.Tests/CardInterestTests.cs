using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Debt;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Tests;

/// <summary>Card interest is a bill planned from what you owe, never spending (DECISIONS.md 159). All data invented.</summary>
public class CardInterestTests
{
    private static readonly BudgetMonthCalendar Cal = new();
    private static readonly BudgetMonthRange[] ThreeMonths = [Cal.MonthOf(2026, 8), Cal.MonthOf(2026, 9), Cal.MonthOf(2026, 10)];
    private static readonly Guid Groceries = Guid.NewGuid(), Interest = Guid.NewGuid();
    private static readonly GeneratorCategory[] Cats =
    [
        new(Groceries, "Groceries", CategoryType.Variable, CategoryPriority.Medium, 400_00),
        new(Interest, "Card interest", CategoryType.FixedBill, CategoryPriority.Medium, 0),
    ];
    private static readonly DebtInput Visa = new(Guid.NewGuid(), "Visa", 2_000_00, 2499, 60_00);
    private static readonly DebtInput Store = new(Guid.NewGuid(), "Store Card", 800_00, 1999, 35_00);

    private static List<BudgetTransaction> History() =>
        ThreeMonths.SelectMany((m, i) => new BudgetTransaction[]
        {
            new(Groceries, m.Start.AddDays(3), 400_00),
            new(Interest, m.Start.AddDays(20), 30_00 + i * 5_00, OnCreditCard: true), // 30, 35, 40
            new(null, m.Start.AddDays(1), -3_000_00, Kind: TransactionKind.Income),
        }).ToList();

    [Theory]
    [InlineData("PURCHASE INTEREST CHARGE", null, 38_12, true)]
    [InlineData("INTEREST CHARGE ON PURCHASES", null, 12_00, true)]
    [InlineData("CHASE CARD", "BANK_FEES_INTEREST_CHARGE", 20_00, true)]
    [InlineData("INTEREST PAYMENT", "INCOME_INTEREST_EARNED", -1_25, false)] // earned on savings: money in
    [InlineData("LATE FEE", null, 40_00, false)]
    public void Interest_charges_are_told_apart_from_fees_and_interest_earned(string merchant, string? label, long cents, bool charge) =>
        Assert.Equal(charge, CardInterest.IsCharge(merchant, null, label, cents));

    [Fact]
    public void Interest_matches_its_own_bill_theme_and_fees_stay_spending()
    {
        Assert.Equal(("Card interest", CategoryType.FixedBill), (SpendingThemes.Match("PURCHASE INTEREST CHARGE")!.Name, SpendingThemes.Match("PURCHASE INTEREST CHARGE")!.Type));
        Assert.Equal("Fees & interest", SpendingThemes.Match("LATE FEE")!.Name);
    }

    [Fact]
    public void New_interest_charges_file_under_the_interest_bill()
    {
        var ids = new Dictionary<string, Guid> { ["Card interest"] = Interest, ["Fees & interest"] = Guid.NewGuid() };
        var g = AutoCategorizer.Guess(new CategorizationInput("PURCHASE INTEREST CHARGE", "Fees & Adjustments"), null, ids);
        Assert.Equal(Interest, g.CategoryId);
        var plaid = AutoCategorizer.Guess(new CategorizationInput("CHASE CARD", "BANK_FEES_INTEREST_CHARGE", "BANK_FEES"), null, ids);
        Assert.Equal(Interest, plaid.CategoryId);
    }

    [Fact]
    public void Expected_interest_is_each_balance_at_its_apr_over_twelve()
    {
        var e = CardInterest.Expected([Store, Visa, Visa with { Id = Guid.NewGuid(), Name = "Promo", AprBps = 0 }]);
        // $2,000 × 24.99% ÷ 12 = $41.65; $800 × 19.99% ÷ 12 = $13.326… → $13.33. A 0% card adds nothing.
        Assert.Equal([("Visa", 41_65L), ("Store Card", 13_33L)], e.Select(x => (x.Card.Name, x.Cents)).ToList());
    }

    [Fact]
    public void The_interest_line_is_planned_from_what_you_owe_now()
    {
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: true, cardDebts: [Visa, Store]);
        var line = p.Categories.Single(c => c.CategoryId == Interest);
        Assert.Equal(54_98, line.ProposedCents);
        Assert.Equal("Expected from what you owe now: Visa $2,000.00 at 24.99% ≈ $41.65 + Store Card $800.00 at 19.99% ≈ $13.33.", line.Explanation);
    }

    [Fact]
    public void With_no_card_debt_no_interest_is_planned()
    {
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: false, cardDebts: []);
        Assert.Equal(0, p.Categories.Single(c => c.CategoryId == Interest).ProposedCents);
    }

    [Fact]
    public void Without_aprs_the_interest_line_uses_what_interest_usually_is()
    {
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: true, cardDebts: []);
        var line = p.Categories.Single(c => c.CategoryId == Interest);
        Assert.Equal(35_00, line.ProposedCents);
        Assert.Contains("Debt tab", line.Explanation);
    }

    [Fact]
    public void A_limit_you_set_on_the_interest_line_stays()
    {
        var p = BudgetGenerator.Propose(ThreeMonths, Cats, History(), hasCardDebt: true, cardDebts: [Visa],
            overrides: new BudgetOverrides(LockedLimits: new Dictionary<Guid, long> { [Interest] = 60_00 }));
        Assert.Equal(60_00, p.Categories.Single(c => c.CategoryId == Interest).ProposedCents);
    }

    [Fact]
    public void Interest_in_the_card_minimums_is_not_planned_twice()
    {
        // DECISIONS.md 172: the Visa's $60 minimum includes about $41.65 of interest, already on the Card interest line.
        var card = Guid.NewGuid();
        GeneratorCategory[] cats = [.. Cats, new(card, "Credit card payment", CategoryType.DebtPayment, CategoryPriority.Medium, 0)];
        var p = BudgetGenerator.Propose(ThreeMonths, cats, History(), hasCardDebt: true, cardDebts: [Visa],
            overrides: new BudgetOverrides(DebtExtraCents: 100_00));
        var line = p.Categories.Single(c => c.CategoryId == card);
        Assert.Equal(18_35 + 100_00, line.ProposedCents); // the $18.35 of the minimum that isn't interest, plus the extra
        Assert.StartsWith("Your card minimums add up to $60.00. $41.65 of that is interest, already on the Card interest line, so $18.35 here, plus $100.00 extra", line.Explanation);
        // Interest line + card line = the minimum + the extra.
        Assert.Equal(60_00 + 100_00, line.ProposedCents + p.Categories.Single(c => c.CategoryId == Interest).ProposedCents);

        // With no extra set, everything left over goes to the card and the plan uses exactly the month's money, not more.
        var all = BudgetGenerator.Propose(ThreeMonths, cats, History(), hasCardDebt: true, cardDebts: [Visa]);
        Assert.Equal((all.IncomeCents, 0L), (all.AllocatedCents, all.UnallocatedCents));
        Assert.Equal(3_000_00 - 400_00 - 41_65, all.Categories.Single(c => c.CategoryId == card).ProposedCents);
    }

    [Fact]
    public void Interest_on_the_bill_line_never_counts_toward_left_to_spend()
    {
        var month = Cal.MonthOf(2026, 11);
        var r = LeftToSpendCalculator.Calculate(month, month.Start.AddDays(5),
            [new BudgetCategory(Groceries, CategoryType.Variable, 400_00), new BudgetCategory(Interest, CategoryType.FixedBill, 41_65)],
            [new BudgetTransaction(Interest, month.Start.AddDays(2), 41_65, OnCreditCard: true)], 0);
        Assert.Equal(400_00, r.LeftToSpendCents);
    }
}
