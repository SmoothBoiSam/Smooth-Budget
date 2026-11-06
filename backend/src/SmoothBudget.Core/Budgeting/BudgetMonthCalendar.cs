namespace SmoothBudget.Core.Budgeting;

/// <summary>An inclusive date range for one budget month.</summary>
/// <param name="Start">First day of the budget month.</param>
/// <param name="End">Last day of the budget month (the day before the next month's start).</param>
/// <param name="Label">First day of the calendar month this budget month is named after (2026-11-01 for "November 2026").</param>
public sealed record BudgetMonthRange(DateOnly Start, DateOnly End, DateOnly Label)
{
    public bool Contains(DateOnly date) => date >= Start && date <= End;

    /// <summary>Days from <paramref name="today"/> through <see cref="End"/>, counting today.</summary>
    public int DaysRemaining(DateOnly today)
    {
        if (!Contains(today))
            throw new ArgumentOutOfRangeException(nameof(today), today, $"Date is outside the budget month {Start}..{End}.");
        return End.DayNumber - today.DayNumber + 1;
    }
}

/// <summary>
/// Budget-month boundaries (SPEC section 3, "Budget month").
/// A budget month starts on a configurable day of the calendar month (default the 7th).
/// A Saturday start moves to the Friday before; a Sunday start moves to the Monday after.
/// It ends the day before the next month's start. Each budget month is labelled by the
/// calendar month its nominal start day falls in.
/// </summary>
public sealed class BudgetMonthCalendar
{
    public const int DefaultStartDay = 7;

    public BudgetMonthCalendar(int startDay = DefaultStartDay)
    {
        // Capped at 28 so every calendar month has the nominal start day.
        if (startDay is < 1 or > 28)
            throw new ArgumentOutOfRangeException(nameof(startDay), startDay, "Start day must be between 1 and 28.");
        StartDay = startDay;
    }

    public int StartDay { get; }

    /// <summary>The actual start date of the budget month labelled <paramref name="year"/>-<paramref name="month"/>.</summary>
    public DateOnly StartOf(int year, int month)
    {
        var nominal = new DateOnly(year, month, StartDay);
        return nominal.DayOfWeek switch
        {
            DayOfWeek.Saturday => nominal.AddDays(-1),
            DayOfWeek.Sunday => nominal.AddDays(1),
            _ => nominal,
        };
    }

    /// <summary>The full range of the budget month labelled <paramref name="year"/>-<paramref name="month"/>.</summary>
    public BudgetMonthRange MonthOf(int year, int month)
    {
        var start = StartOf(year, month);
        var next = new DateOnly(year, month, 1).AddMonths(1);
        var end = StartOf(next.Year, next.Month).AddDays(-1);
        return new BudgetMonthRange(start, end, new DateOnly(year, month, 1));
    }

    /// <summary>The budget month that contains <paramref name="date"/>.</summary>
    public BudgetMonthRange MonthContaining(DateOnly date)
    {
        // A weekend shift can move a start across a calendar-month boundary (start day 1 on a
        // Saturday starts on the last day of the previous month), so check both neighbours.
        var label = new DateOnly(date.Year, date.Month, 1);
        var next = label.AddMonths(1);
        if (date < StartOf(label.Year, label.Month))
            label = label.AddMonths(-1);
        else if (date >= StartOf(next.Year, next.Month))
            label = next;
        return MonthOf(label.Year, label.Month);
    }
}
