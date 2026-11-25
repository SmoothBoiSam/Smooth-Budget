using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Core.Tests;

/// <summary>DECISIONS.md 327: setting money aside for a big expense. All amounts invented.</summary>
public class PlannedExpensesTests
{
    private static readonly DateOnly[] Paydays = [new(2027, 2, 22), new(2027, 3, 8), new(2027, 3, 22), new(2027, 4, 7)];
    private static readonly PlannedExpenseInput Repair = new(Guid.NewGuid(), "Test repair", 1_000_00, new DateOnly(2027, 3, 25), Paydays[0]);
    private static BudgetMonthRange MonthOf(int month) => new(new DateOnly(2027, month, 1), new DateOnly(2027, month, DateTime.DaysInMonth(2027, month)), new DateOnly(2027, month, 1));

    [Fact]
    public void Each_check_up_to_the_due_date_sets_aside_an_even_part_and_the_last_takes_the_odd_cents()
    {
        var parts = PlannedExpenses.Parts(Repair.AmountCents, Repair.DueOn, Paydays);
        Assert.Equal([new(Paydays[0], 333_33), new(Paydays[1], 333_33), new(Paydays[2], 333_34)], parts);
        // Due before the next check: the check you're on sets it all aside.
        Assert.Equal([new(Paydays[0], 1_000_00)], PlannedExpenses.Parts(1_000_00, new DateOnly(2027, 3, 1), Paydays));
    }

    [Fact]
    public void A_check_keeps_what_it_and_the_checks_before_it_set_aside_less_what_has_been_paid()
    {
        var parts = PlannedExpenses.Parts(900_00, Repair.DueOn, Paydays);
        Assert.Equal((300_00L, 300_00L), PlannedExpenses.AtCheck(parts, Paydays[0], 0));
        Assert.Equal((600_00L, 300_00L), PlannedExpenses.AtCheck(parts, Paydays[1], 0));
        Assert.Equal((900_00L, 300_00L), PlannedExpenses.AtCheck(parts, Paydays[2], 0));
        Assert.Equal((500_00L, 300_00L), PlannedExpenses.AtCheck(parts, Paydays[2], 400_00));
        Assert.Equal((0L, 0L), PlannedExpenses.AtCheck(parts, Paydays[2], 900_00));
    }

    [Fact]
    public void Months_before_it_plan_their_parts_and_the_month_its_paid_plans_the_cost_with_earlier_money_counted_in()
    {
        var parts = PlannedExpenses.Parts(900_00, Repair.DueOn, Paydays);
        var repair = Repair with { AmountCents = 900_00 };
        var march = new DateOnly(2027, 3, 1);

        var feb = PlannedExpenses.Month(repair, parts, MonthOf(2), march, 0, 0);
        Assert.Equal((300_00L, 0L), (feb.LimitCents, feb.FromEarlierCents));
        Assert.Contains("leaves less for extra card payments and savings", feb.Explanation);

        var mar = PlannedExpenses.Month(repair, parts, MonthOf(3), march, 0, 0);
        Assert.Equal((900_00L, 300_00L), (mar.LimitCents, mar.FromEarlierCents));

        // Paid early, in February: that month's line is the cost, and nothing more is set aside after.
        var early = PlannedExpenses.Month(repair, parts, MonthOf(2), march, 0, 900_00);
        Assert.Equal((900_00L, 0L), (early.LimitCents, early.FromEarlierCents));
        var after = PlannedExpenses.Month(repair, parts, MonthOf(3), march, 900_00, 0);
        Assert.Equal((0L, 0L), (after.LimitCents, after.FromEarlierCents));

        // A month after it was due plans only what was spent then.
        Assert.Equal(0L, PlannedExpenses.Month(repair, parts, MonthOf(4), march, 0, 0).LimitCents);
    }
}
