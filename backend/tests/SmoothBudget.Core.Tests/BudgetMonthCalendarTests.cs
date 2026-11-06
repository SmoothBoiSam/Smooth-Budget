using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Core.Tests;

public class BudgetMonthCalendarTests
{
    private static readonly BudgetMonthCalendar Calendar = new();

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void November_2026_starts_Friday_the_6th_because_the_7th_is_a_Saturday()
    {
        // SPEC section 3: "November's budget runs 11-06 through 12-06."
        Assert.Equal(DayOfWeek.Saturday, D(2026, 11, 7).DayOfWeek);
        var nov = Calendar.MonthOf(2026, 11);
        Assert.Equal(D(2026, 11, 6), nov.Start);
        Assert.Equal(D(2026, 12, 6), nov.End);
    }

    [Fact]
    public void A_weekday_7th_starts_on_the_7th()
    {
        Assert.Equal(DayOfWeek.Monday, D(2026, 12, 7).DayOfWeek);
        Assert.Equal(D(2026, 12, 7), Calendar.StartOf(2026, 12));
    }

    [Fact]
    public void A_Sunday_7th_moves_to_the_Monday_after()
    {
        Assert.Equal(DayOfWeek.Sunday, D(2027, 2, 7).DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, D(2027, 3, 7).DayOfWeek);
        var feb = Calendar.MonthOf(2027, 2);
        Assert.Equal(D(2027, 2, 8), feb.Start);
        Assert.Equal(D(2027, 3, 7), feb.End); // March also starts on Monday the 8th
    }

    [Fact]
    public void Month_ends_the_day_before_the_next_start_across_a_year_boundary()
    {
        var dec = Calendar.MonthOf(2026, 12);
        Assert.Equal(D(2026, 12, 7), dec.Start);
        Assert.Equal(D(2027, 1, 6), dec.End); // Jan 7 2027 is a Thursday
    }

    [Theory]
    [InlineData(2026, 11, 5, 2026, 10, 7, 2026, 11, 5)]   // last day of the October budget month
    [InlineData(2026, 11, 6, 2026, 11, 6, 2026, 12, 6)]   // first day of November (Friday start)
    [InlineData(2026, 11, 7, 2026, 11, 6, 2026, 12, 6)]   // the Saturday 7th is already inside November
    [InlineData(2026, 12, 6, 2026, 11, 6, 2026, 12, 6)]   // last day of November
    [InlineData(2027, 2, 7, 2027, 1, 7, 2027, 2, 7)]      // a Sunday 7th still belongs to the previous month
    [InlineData(2027, 2, 8, 2027, 2, 8, 2027, 3, 7)]
    [InlineData(2027, 1, 1, 2026, 12, 7, 2027, 1, 6)]     // New Year's Day sits in the December budget month
    public void MonthContaining_finds_the_right_budget_month(int y, int m, int d, int sy, int sm, int sd, int ey, int em, int ed)
    {
        var range = Calendar.MonthContaining(D(y, m, d));
        Assert.Equal(D(sy, sm, sd), range.Start);
        Assert.Equal(D(ey, em, ed), range.End);
    }

    [Fact]
    public void Consecutive_months_tile_with_no_gaps_or_overlaps_for_ten_years()
    {
        var label = D(2026, 1, 1);
        for (var i = 0; i < 120; i++, label = label.AddMonths(1))
        {
            var current = Calendar.MonthOf(label.Year, label.Month);
            var next = Calendar.MonthOf(label.AddMonths(1).Year, label.AddMonths(1).Month);
            Assert.Equal(current.End.AddDays(1), next.Start);
            Assert.True(current.Start.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday);
            Assert.Equal(current, Calendar.MonthContaining(current.Start));
            Assert.Equal(current, Calendar.MonthContaining(current.End));
        }
    }

    [Fact]
    public void Start_day_is_configurable()
    {
        var firstOfMonth = new BudgetMonthCalendar(startDay: 1);
        // Nov 1 2026 is a Sunday -> Monday Nov 2. Dec 1 2026 is a Tuesday.
        var nov = firstOfMonth.MonthOf(2026, 11);
        Assert.Equal(D(2026, 11, 2), nov.Start);
        Assert.Equal(D(2026, 11, 30), nov.End);
    }

    [Fact]
    public void A_Saturday_shift_that_crosses_into_the_previous_calendar_month_is_found()
    {
        // Start day 1: May 1 2027 is a Saturday, so the May budget month starts Friday April 30.
        var firstOfMonth = new BudgetMonthCalendar(startDay: 1);
        Assert.Equal(DayOfWeek.Saturday, D(2027, 5, 1).DayOfWeek);
        var range = firstOfMonth.MonthContaining(D(2027, 4, 30));
        Assert.Equal(D(2027, 4, 30), range.Start);
        Assert.Equal(D(2027, 5, 1), range.Label);
        Assert.Equal(D(2027, 4, 29), firstOfMonth.MonthContaining(D(2027, 4, 29)).End);
    }

    [Fact]
    public void Month_is_labelled_by_its_nominal_calendar_month()
    {
        Assert.Equal(D(2026, 11, 1), Calendar.MonthContaining(D(2026, 12, 6)).Label);
        Assert.Equal(D(2027, 2, 1), Calendar.MonthContaining(D(2027, 2, 8)).Label);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    public void Start_day_outside_1_to_28_is_rejected(int startDay)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BudgetMonthCalendar(startDay));
    }

    [Fact]
    public void DaysRemaining_counts_today_and_the_last_day()
    {
        var nov = Calendar.MonthOf(2026, 11);
        Assert.Equal(31, nov.DaysRemaining(D(2026, 11, 6)));
        Assert.Equal(17, nov.DaysRemaining(D(2026, 11, 20)));
        Assert.Equal(1, nov.DaysRemaining(D(2026, 12, 6)));
        Assert.Throws<ArgumentOutOfRangeException>(() => nov.DaysRemaining(D(2026, 12, 7)));
    }
}
