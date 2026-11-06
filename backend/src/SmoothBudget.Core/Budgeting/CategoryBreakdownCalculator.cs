namespace SmoothBudget.Core.Budgeting;

public enum CategoryHealth
{
    /// <summary>Within the limit with room to spare.</summary>
    OnTrack,
    /// <summary>Spending category with under 15% of its limit left.</summary>
    Low,
    /// <summary>Spent (or paid) more than the limit.</summary>
    Over,
}

/// <param name="SpentCents">For spending categories, what was spent. For bills, debt and savings, what was paid in.</param>
public sealed record CategoryStatus(Guid CategoryId, CategoryType Type, long LimitCents, long SpentCents, int TransactionCount)
{
    /// <summary>Limit minus spent. Negative means over.</summary>
    public long RemainingCents => LimitCents - SpentCents;

    public CategoryHealth Health =>
        SpentCents > LimitCents ? CategoryHealth.Over
        : Type == CategoryType.Variable && RemainingCents * 100 < LeftToSpendCalculator.LowThresholdPercent * LimitCents ? CategoryHealth.Low
        : CategoryHealth.OnTrack;
}

/// <summary>
/// Spent (or paid) against every category for one budget month, for the Budget screen (SPEC section 4.3).
/// Spending categories use exactly the rules of <see cref="LeftToSpendCalculator"/>, so their remaining
/// amounts add up to Left to Spend before any carried-in deficit. Debt-payment and savings categories
/// also count transfers, because a card payment or a move to savings is how those get paid.
/// </summary>
public static class CategoryBreakdownCalculator
{
    public static IReadOnlyList<CategoryStatus> Calculate(
        BudgetMonthRange month,
        IEnumerable<BudgetCategory> categories,
        IEnumerable<BudgetTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(month);
        var cats = categories.ToList();
        var byId = cats.ToDictionary(c => c.Id);
        var spent = cats.ToDictionary(c => c.Id, _ => 0L);
        var count = cats.ToDictionary(c => c.Id, _ => 0);

        foreach (var t in transactions)
        {
            if (t.CategoryId is not { } id || !byId.TryGetValue(id, out var c) || !CountsToward(month, c.Type, t))
                continue;
            spent[id] += LeftToSpendCalculator.OwnerShare(t);
            count[id]++;
        }

        return cats.Select(c => new CategoryStatus(c.Id, c.Type, c.MonthlyLimitCents, LeftToSpendCalculator.FloorAtZero(spent[c.Id]), count[c.Id])).ToList();
    }

    /// <summary>Whether a transaction already filed under a category of this type counts toward it this month.</summary>
    public static bool CountsToward(BudgetMonthRange month, CategoryType type, BudgetTransaction t) =>
        month.Contains(t.Date)
        && (type is CategoryType.DebtPayment or CategoryType.SavingsTransfer || !t.IsTransfer);
}
