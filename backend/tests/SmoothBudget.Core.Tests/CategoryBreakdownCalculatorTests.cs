using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Core.Tests;

public class CategoryBreakdownCalculatorTests
{
    private static readonly BudgetMonthRange Nov = new BudgetMonthCalendar().MonthOf(2026, 11);

    private static readonly Guid Groceries = Guid.NewGuid();
    private static readonly Guid EatingOut = Guid.NewGuid();
    private static readonly Guid Rent = Guid.NewGuid();
    private static readonly Guid Streaming = Guid.NewGuid();
    private static readonly Guid CardPayment = Guid.NewGuid();
    private static readonly Guid RainyDay = Guid.NewGuid();

    private static readonly BudgetCategory[] Categories =
    [
        new(Groceries, CategoryType.Variable, 450_00),
        new(EatingOut, CategoryType.Variable, 150_00),
        new(Rent, CategoryType.FixedBill, 1_650_00),
        new(Streaming, CategoryType.FixedBill, 35_00),
        new(CardPayment, CategoryType.DebtPayment, 300_00),
        new(RainyDay, CategoryType.SavingsTransfer, 100_00),
    ];

    private static BudgetTransaction T(Guid? cat, long cents, int day = 10, int month = 11, bool transfer = false, bool pending = false, decimal split = 1m) =>
        new(cat, new DateOnly(2026, month, day), cents, transfer, pending, split);

    private static CategoryStatus Line(IEnumerable<CategoryStatus> lines, Guid id) => lines.Single(l => l.CategoryId == id);

    [Fact]
    public void Every_category_gets_a_line_even_with_no_activity()
    {
        var lines = CategoryBreakdownCalculator.Calculate(Nov, Categories, []);
        Assert.Equal(Categories.Length, lines.Count);
        Assert.All(lines, l => Assert.Equal((0L, 0, l.LimitCents), (l.SpentCents, l.TransactionCount, l.RemainingCents)));
    }

    [Fact]
    public void Spending_categories_sum_purchases_minus_refunds_including_pending_and_owner_share()
    {
        var lines = CategoryBreakdownCalculator.Calculate(Nov, Categories,
        [
            T(Groceries, 82_17), T(Groceries, 64_30, pending: true), T(Groceries, -10_00), T(Groceries, 120_00, split: 0.5m),
        ]);
        var g = Line(lines, Groceries);
        Assert.Equal(82_17 + 64_30 - 10_00 + 60_00, g.SpentCents);
        Assert.Equal(450_00 - g.SpentCents, g.RemainingCents);
        Assert.Equal(4, g.TransactionCount);
    }

    [Fact]
    public void A_category_never_goes_below_zero_spent()
    {
        var lines = CategoryBreakdownCalculator.Calculate(Nov, Categories, [T(EatingOut, 30_00), T(EatingOut, -810_00)]);
        Assert.Equal((0L, 150_00L, 2), (Line(lines, EatingOut).SpentCents, Line(lines, EatingOut).RemainingCents, Line(lines, EatingOut).TransactionCount));
    }

    [Fact]
    public void Transfers_never_count_toward_spending_or_bills()
    {
        var lines = CategoryBreakdownCalculator.Calculate(Nov, Categories, [T(Groceries, 50_00, transfer: true), T(Rent, 50_00, transfer: true)]);
        Assert.Equal(0, Line(lines, Groceries).SpentCents);
        Assert.Equal(0, Line(lines, Rent).SpentCents);
    }

    [Fact]
    public void Card_payments_and_savings_transfers_count_toward_their_own_categories()
    {
        var lines = CategoryBreakdownCalculator.Calculate(Nov, Categories,
            [T(CardPayment, 300_00, transfer: true), T(RainyDay, 100_00, transfer: true)]);
        Assert.Equal(300_00, Line(lines, CardPayment).SpentCents);
        Assert.Equal(100_00, Line(lines, RainyDay).SpentCents);
    }

    [Fact]
    public void Bills_count_what_was_paid()
    {
        var lines = CategoryBreakdownCalculator.Calculate(Nov, Categories, [T(Rent, 1_650_00), T(Streaming, 15_49)]);
        Assert.Equal((1_650_00L, 0L), (Line(lines, Rent).SpentCents, Line(lines, Rent).RemainingCents));
        Assert.Equal(35_00 - 15_49, Line(lines, Streaming).RemainingCents);
    }

    [Fact]
    public void Only_transactions_inside_the_budget_month_count()
    {
        var lines = CategoryBreakdownCalculator.Calculate(Nov, Categories,
            [T(Groceries, 1_00, day: 5), T(Groceries, 10_00, day: 6), T(Groceries, 100_00, day: 6, month: 12), T(Groceries, 1_000_00, day: 7, month: 12)]);
        Assert.Equal(110_00, Line(lines, Groceries).SpentCents);
    }

    [Theory]
    [InlineData(0, CategoryHealth.OnTrack)]
    [InlineData(382_50, CategoryHealth.OnTrack)] // exactly 15% of 450 left
    [InlineData(382_51, CategoryHealth.Low)]
    [InlineData(450_00, CategoryHealth.Low)]     // nothing left, not over
    [InlineData(450_01, CategoryHealth.Over)]
    public void Spending_category_health_uses_the_15_percent_rule(long spent, CategoryHealth expected)
    {
        Assert.Equal(expected, new CategoryStatus(Groceries, CategoryType.Variable, 450_00, spent, 1).Health);
    }

    [Fact]
    public void A_fully_paid_bill_is_on_track_not_low()
    {
        Assert.Equal(CategoryHealth.OnTrack, new CategoryStatus(Rent, CategoryType.FixedBill, 1_650_00, 1_650_00, 1).Health);
        Assert.Equal(CategoryHealth.Over, new CategoryStatus(Rent, CategoryType.FixedBill, 1_650_00, 1_700_00, 1).Health);
    }

    [Fact]
    public void Spending_lines_add_up_to_left_to_spend_before_the_carried_deficit()
    {
        BudgetTransaction[] txns =
        [
            T(Groceries, 82_17), T(EatingOut, 172_35), T(Groceries, -12_00), T(Rent, 1_650_00),
            T(CardPayment, 300_00, transfer: true), T(null, 15_00), T(EatingOut, 20_00, transfer: true),
        ];
        var lines = CategoryBreakdownCalculator.Calculate(Nov, Categories, txns);
        var lts = LeftToSpendCalculator.Calculate(Nov, new DateOnly(2026, 11, 20), Categories, txns, carriedInDeficitCents: 42_50);

        var variableRemaining = lines.Where(l => l.Type == CategoryType.Variable).Sum(l => l.RemainingCents);
        Assert.Equal(lts.LeftToSpendCents, variableRemaining - 42_50);
        Assert.Equal(lts.TotalVariableSpentCents, lines.Where(l => l.Type == CategoryType.Variable).Sum(l => l.SpentCents));
    }

    // ---- Nothing counts twice (DECISIONS.md 85) ------------------------------------------------------

    private static BudgetTransaction OnCard(Guid? cat, long cents) => new(cat, new DateOnly(2026, 11, 12), cents, OnCreditCard: true);
    private static BudgetTransaction Payment(long cents, bool cardSide = false) =>
        new(cardSide ? null : CardPayment, new DateOnly(2026, 11, 20), cardSide ? -cents : cents, IsTransfer: true,
            Kind: SmoothBudget.Core.Transactions.TransactionKind.CardPayment, OnCreditCard: cardSide);

    [Fact]
    public void A_card_payment_that_covers_this_months_card_purchases_isnt_counted_again()
    {
        var lines = CategoryBreakdownCalculator.Calculate(Nov, Categories,
            [OnCard(Groceries, 200_00), OnCard(EatingOut, 60_00), Payment(260_00), Payment(260_00, cardSide: true)]);
        Assert.Equal(200_00, Line(lines, Groceries).SpentCents);
        Assert.Equal(60_00, Line(lines, EatingOut).SpentCents);
        Assert.Equal(0, Line(lines, CardPayment).SpentCents); // it just paid for the groceries and dinners
    }

    [Fact]
    public void Only_what_you_pay_beyond_card_purchases_pays_down_the_balance()
    {
        var lines = CategoryBreakdownCalculator.Calculate(Nov, Categories,
            [OnCard(Groceries, 200_00), OnCard(Streaming, 15_00), new(Groceries, new DateOnly(2026, 11, 15), 40_00), Payment(500_00)]);
        Assert.Equal(500_00 - 215_00, Line(lines, CardPayment).SpentCents); // debit-card groceries aren't on the card
        Assert.Equal(240_00, Line(lines, Groceries).SpentCents);
    }

    [Fact]
    public void Split_card_purchases_cover_only_your_share()
    {
        var lines = CategoryBreakdownCalculator.Calculate(Nov, Categories,
            [new(Groceries, new DateOnly(2026, 11, 12), 120_00, SplitFactor: 0.5m, OnCreditCard: true), Payment(120_00)]);
        Assert.Equal(60_00, Line(lines, Groceries).SpentCents);
        Assert.Equal(60_00, Line(lines, CardPayment).SpentCents); // her half is in the settle-up balance, not your budget
    }

    [Fact]
    public void Filing_both_sides_of_a_transfer_counts_it_once()
    {
        var lines = CategoryBreakdownCalculator.Calculate(Nov, Categories,
        [
            T(RainyDay, 100_00, transfer: true), T(RainyDay, -100_00, transfer: true), // checking -> savings, both sides filed
            new(CardPayment, new DateOnly(2026, 11, 20), 300_00, IsTransfer: true), new(CardPayment, new DateOnly(2026, 11, 20), -300_00, IsTransfer: true),
        ]);
        Assert.Equal(100_00, Line(lines, RainyDay).SpentCents);
        Assert.Equal(300_00, Line(lines, CardPayment).SpentCents);
    }
}
