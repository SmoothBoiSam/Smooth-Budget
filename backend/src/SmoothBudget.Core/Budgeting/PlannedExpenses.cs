namespace SmoothBudget.Core.Budgeting;

/// <summary>A big one-time expense you're setting money aside for (DECISIONS.md 327).</summary>
/// <param name="FirstPay">The payday of the check you were on when you added it: the first check that sets money aside.</param>
public sealed record PlannedExpenseInput(Guid Id, string Name, long AmountCents, DateOnly DueOn, DateOnly FirstPay);

/// <summary>One paycheck's share of a planned expense.</summary>
public sealed record SetAsidePart(DateOnly PayDate, long Cents);

/// <summary>What a budget month plans for a planned expense.</summary>
/// <param name="LimitCents">Its line this month: the part set aside this month, or in the month it's paid, what it costs.</param>
/// <param name="FromEarlierCents">Money set aside in earlier months that this month spends: it adds to the month's money.</param>
/// <param name="Explanation">Why, in plain words, for the Budget tab.</param>
public sealed record PlannedMonth(long LimitCents, long FromEarlierCents, string Explanation);

/// <summary>
/// Setting money aside for a big expense (DECISIONS.md 327). Each check from the one you're on when you add it up to its due
/// date keeps an even part in checking; the last of them takes any odd cents. Before the month it's due, each budget month
/// plans its checks' parts on the expense's own bill line, so less is left for extra card payments and savings. In the
/// month it's paid (the month it's due, or an earlier one where you pay it early), the line is the whole cost, and what the
/// earlier months set aside counts as money in, so that month's split isn't squeezed for it.
/// </summary>
public static class PlannedExpenses
{
    /// <summary>Each check's part: the checks paid from <paramref name="payDates"/> on or before the due date.</summary>
    public static IReadOnlyList<SetAsidePart> Parts(long amountCents, DateOnly dueOn, IEnumerable<DateOnly> payDates)
    {
        var checks = payDates.Where(p => p <= dueOn).Distinct().Order().ToList();
        if (checks.Count == 0 || amountCents <= 0) return [];
        var each = amountCents / checks.Count;
        return checks.Select((p, i) => new SetAsidePart(p, each + (i == checks.Count - 1 ? amountCents - each * checks.Count : 0))).ToList();
    }

    /// <summary>
    /// What a check keeps in checking for the expense: everything set aside by this check, less what's been spent on it.
    /// <c>Own</c> is this check's part of that; the rest was set aside by earlier checks and is already in the account.
    /// </summary>
    public static (long Need, long Own) AtCheck(IReadOnlyList<SetAsidePart> parts, DateOnly payDate, long spentCents)
    {
        var need = Math.Max(0, parts.Where(p => p.PayDate <= payDate).Sum(p => p.Cents) - Math.Max(0, spentCents));
        var own = Math.Min(need, parts.Where(p => p.PayDate == payDate).Sum(p => p.Cents));
        return (need, own);
    }

    /// <summary>The month's plan for the expense.</summary>
    /// <param name="month">The budget month.</param>
    /// <param name="dueMonthStart">The start of the budget month the due date falls in.</param>
    /// <param name="spentBeforeCents">What was spent on it before this month.</param>
    /// <param name="spentThisMonthCents">What's been spent on it this month.</param>
    public static PlannedMonth Month(PlannedExpenseInput e, IReadOnlyList<SetAsidePart> parts, BudgetMonthRange month, DateOnly dueMonthStart,
        long spentBeforeCents, long spentThisMonthCents)
    {
        var earlier = parts.Where(p => p.PayDate < month.Start).Sum(p => p.Cents);
        var thisMonth = parts.Where(p => month.Contains(p.PayDate)).Sum(p => p.Cents);
        var due = $"{e.DueOn:MMM d}";
        // Paid off, or its month has gone by: nothing more is planned for it.
        if (spentBeforeCents >= e.AmountCents || month.Start > dueMonthStart)
            return new PlannedMonth(Math.Max(0, spentThisMonthCents), 0, spentBeforeCents >= e.AmountCents ? $"{e.Name} is paid." : $"{e.Name} was due {due}.");
        // The month it's paid: its due month, or an earlier one where some of it has been spent.
        if (month.Start == dueMonthStart || spentThisMonthCents > 0)
        {
            var fromEarlier = Math.Max(0, earlier - spentBeforeCents);
            var cost = Math.Max(e.AmountCents - spentBeforeCents, spentThisMonthCents);
            return new PlannedMonth(cost, fromEarlier,
                fromEarlier > 0
                    ? $"{e.Name}, due {due}: {Money(cost)}. {Money(fromEarlier)} of it was set aside before this month, so it counts as money in."
                    : $"{e.Name}, due {due}: {Money(cost)}, set aside from this month's checks.");
        }
        return new PlannedMonth(thisMonth, 0,
            thisMonth > 0
                ? $"Set aside for {e.Name}, due {due}: {Money(thisMonth)} from this month's checks, kept in checking. It leaves less for extra card payments and savings."
                : $"Nothing set aside for {e.Name} this month.");
    }

    private static string Money(long cents) => (cents / 100m).ToString("C", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
