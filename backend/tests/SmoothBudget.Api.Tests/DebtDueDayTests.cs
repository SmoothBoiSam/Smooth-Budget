using SmoothBudget.Api.Debt;

namespace SmoothBudget.Api.Tests;

/// <summary>A card due on the last day of the month (stored as day 31) follows the month's length.</summary>
public class DebtDueDayTests
{
    [Theory]
    [InlineData("2027-02-10", 31, "2027-02-28")]
    [InlineData("2028-02-10", 31, "2028-02-29")]
    [InlineData("2026-11-05", 31, "2026-11-30")]
    [InlineData("2026-12-31", 31, "2026-12-31")]
    [InlineData("2027-01-01", 31, "2027-01-31")]
    [InlineData("2026-11-26", 25, "2026-12-25")]
    public void Next_due_date(string today, int dueDay, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), DebtService.NextDue(DateOnly.Parse(today), dueDay));
}
