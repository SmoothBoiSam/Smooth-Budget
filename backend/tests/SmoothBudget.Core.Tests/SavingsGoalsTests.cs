using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Core.Tests;

/// <summary>Milestone 8: progress and projected completion date from account balances. All data invented.</summary>
public class SavingsGoalsTests
{
    private static readonly Guid Savings = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 7);
    private static readonly GoalInput RainyDay = new(Guid.NewGuid(), "Rainy-day fund", 1_000_00, Savings, null, null, true, 0);
    private static readonly GoalInput Trip = new(Guid.NewGuid(), "Trip", 2_000_00, Savings, null, null, false, 1);

    [Fact]
    public void Progress_comes_from_the_balance_and_the_date_from_your_pace()
    {
        var s = Assert.Single(SavingsGoals.Status([RainyDay], new Dictionary<Guid, long> { [Savings] = 250_00 }, new Dictionary<Guid, long> { [Savings] = 100_00 }, Today));
        Assert.Equal((250_00L, 750_00L, 25, false), (s.SavedCents, s.RemainingCents, s.Percent, s.Reached));
        Assert.Equal((100_00L, "pace", 8, new DateOnly(2027, 7, 7)), (s.MonthlyCents, s.MonthlySource, s.MonthsToGo!.Value, s.ProjectedDate!.Value)); // 750 / 100 = 7.5 → 8
    }

    [Fact]
    public void A_bigger_balance_updates_progress_and_brings_the_date_closer()
    {
        var before = SavingsGoals.Status([RainyDay], new Dictionary<Guid, long> { [Savings] = 250_00 }, new Dictionary<Guid, long> { [Savings] = 100_00 }, Today)[0];
        var after = SavingsGoals.Status([RainyDay], new Dictionary<Guid, long> { [Savings] = 600_00 }, new Dictionary<Guid, long> { [Savings] = 100_00 }, Today)[0];
        Assert.Equal((60, 4), (after.Percent, after.MonthsToGo!.Value));
        Assert.True(after.ProjectedDate < before.ProjectedDate);
    }

    [Fact]
    public void Goals_sharing_an_account_fill_in_priority_order_and_the_pace_goes_to_the_first_unfinished_one()
    {
        var s = SavingsGoals.Status([Trip, RainyDay], new Dictionary<Guid, long> { [Savings] = 1_300_00 }, new Dictionary<Guid, long> { [Savings] = 200_00 }, Today);
        var rainy = s.Single(g => g.Name == "Rainy-day fund");
        var trip = s.Single(g => g.Name == "Trip");
        Assert.Equal((1_000_00L, true, (int?)null), (rainy.SavedCents, rainy.Reached, rainy.MonthsToGo));
        Assert.Equal((300_00L, 200_00L, 9), (trip.SavedCents, trip.MonthlyCents, trip.MonthsToGo!.Value)); // 1,700 / 200 = 8.5 → 9
    }

    [Fact]
    public void A_monthly_amount_you_set_wins_and_a_goal_by_hand_uses_what_you_entered()
    {
        var mine = RainyDay with { MonthlyCents = 250_00 };
        var byHand = new GoalInput(Guid.NewGuid(), "Car", 5_000_00, null, 1_200_00, 400_00, false, 2);
        var s = SavingsGoals.Status([mine, byHand], new Dictionary<Guid, long> { [Savings] = 500_00 }, new Dictionary<Guid, long> { [Savings] = 100_00 }, Today);
        Assert.Equal((250_00L, "you", 2), (s[0].MonthlyCents, s[0].MonthlySource, s[0].MonthsToGo!.Value));
        Assert.Equal((1_200_00L, 24, 10), (s[1].SavedCents, s[1].Percent, s[1].MonthsToGo!.Value)); // 3,800 / 400 = 9.5 → 10
    }

    [Fact]
    public void No_monthly_amount_means_no_date_yet()
    {
        var s = SavingsGoals.Status([RainyDay], new Dictionary<Guid, long> { [Savings] = 100_00 }, new Dictionary<Guid, long>(), Today)[0];
        Assert.Equal(("none", (int?)null, (DateOnly?)null), (s.MonthlySource, s.MonthsToGo, s.ProjectedDate));
    }

    [Fact]
    public void Pace_is_the_median_of_net_monthly_deposits()
    {
        var cal = new BudgetMonthCalendar();
        BudgetMonthRange[] months = [cal.MonthOf(2026, 8), cal.MonthOf(2026, 9), cal.MonthOf(2026, 10)];
        (DateOnly, long)[] txns =
        [
            (new DateOnly(2026, 8, 10), -100_00), (new DateOnly(2026, 9, 10), -150_00), (new DateOnly(2026, 9, 20), 50_00), // Sept nets 100
            (new DateOnly(2026, 10, 10), -300_00),
        ];
        Assert.Equal(100_00, SavingsGoals.Pace(txns, months)); // 100, 100, 300 → 100
    }

    [Fact]
    public void History_works_back_from_todays_balance()
    {
        (DateOnly, long)[] txns = [(new DateOnly(2026, 9, 10), -100_00), (new DateOnly(2026, 10, 10), -150_00), (new DateOnly(2026, 10, 20), 40_00)];
        var h = SavingsGoals.History(510_00, txns, [new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 31)]);
        Assert.Equal([300_00L, 400_00L, 510_00L], h.Select(p => p.BalanceCents)); // 510 − 110 net in October = 400; − 100 in September = 300
    }
}
