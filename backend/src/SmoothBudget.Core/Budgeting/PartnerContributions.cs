namespace SmoothBudget.Core.Budgeting;

/// <summary>
/// A fixed contribution from the partner toward one category (SPEC section 3: "$800 a month toward rent"). It arrives
/// between <see cref="WindowFromDay"/> and <see cref="WindowToDay"/> of the budget month (Appendix D: the 18th to 24th).
/// </summary>
public sealed record ContributionArrangement(Guid Id, Guid CategoryId, long ExpectedCents, int WindowFromDay = 18, int WindowToDay = 24);

/// <summary>Money in from the partner. <see cref="LabeledContribution"/>: you said it's her contribution; <see cref="LabeledOther"/>: you said it isn't.</summary>
public sealed record PartnerDeposit(Guid Id, DateOnly Date, long AmountCents, bool LabeledContribution = false, bool LabeledOther = false);

public enum ContributionState
{
    /// <summary>Not here yet, and the window hasn't passed: it still counts (the owner's decision).</summary>
    Expected,
    /// <summary>Arrived at the expected amount.</summary>
    Received,
    /// <summary>Arrived, but not the expected amount: flagged (Appendix D).</summary>
    DifferentAmount,
    /// <summary>The window passed with nothing from her: flagged, and Left to Spend is recomputed without it.</summary>
    Late,
}

/// <param name="ShortfallCents">What Left to Spend loses: the missing amount once the window has passed (0 before then).</param>
public sealed record ContributionStatus(
    Guid ArrangementId, Guid CategoryId, ContributionState State, long ExpectedCents, long ReceivedCents, Guid? MatchedDepositId,
    DateOnly WindowStart, DateOnly WindowEnd, long ShortfallCents);

/// <summary>
/// Matches the partner's fixed contributions for one budget month (SPEC section 3 and Appendix D):
/// <list type="bullet">
/// <item>A deposit you labeled as her contribution wins, anywhere in the budget month; otherwise the
/// <b>largest</b> deposit from her inside the window (not one you labeled as something else) is the contribution.</item>
/// <item>Until the window ends, an unmatched contribution is <b>expected</b> and still counts toward Left to Spend.</item>
/// <item>After it ends with nothing matched, it's <b>late</b>: Left to Spend is recomputed without it (the whole amount).</item>
/// <item>A matched amount that isn't the expected one is flagged; if it's less, the difference is missing from
/// Left to Spend once the window has passed (an extra amount never raises limits, SPEC section 3).</item>
/// </list>
/// With several arrangements, each takes the largest deposit not already matched, in arrangement order.
/// </summary>
public static class PartnerContributions
{
    public static IReadOnlyList<ContributionStatus> Match(
        IReadOnlyList<ContributionArrangement> arrangements, BudgetMonthRange month, IReadOnlyList<PartnerDeposit> deposits, DateOnly today)
    {
        var used = new HashSet<Guid>();
        var result = new List<ContributionStatus>();
        foreach (var a in arrangements)
        {
            var (start, end) = Window(month, a.WindowFromDay, a.WindowToDay);
            var inMonth = deposits.Where(d => month.Contains(d.Date) && !used.Contains(d.Id) && d.AmountCents > 0).ToList();
            var match = inMonth.Where(d => d.LabeledContribution).OrderByDescending(d => d.AmountCents).ThenBy(d => d.Date).FirstOrDefault()
                        ?? inMonth.Where(d => !d.LabeledOther && d.Date >= start && d.Date <= end)
                            .OrderByDescending(d => d.AmountCents).ThenBy(d => d.Date).FirstOrDefault();
            var passed = today > end;
            if (match is null)
            {
                result.Add(new ContributionStatus(a.Id, a.CategoryId, passed ? ContributionState.Late : ContributionState.Expected,
                    a.ExpectedCents, 0, null, start, end, passed ? a.ExpectedCents : 0));
                continue;
            }
            used.Add(match.Id);
            var state = match.AmountCents == a.ExpectedCents ? ContributionState.Received : ContributionState.DifferentAmount;
            var shortfall = passed ? Math.Max(0, a.ExpectedCents - match.AmountCents) : 0; // the rest may still come until the window ends
            result.Add(new ContributionStatus(a.Id, a.CategoryId, state, a.ExpectedCents, match.AmountCents, match.Id, start, end, shortfall));
        }
        return result;
    }

    /// <summary>
    /// The window inside a budget month: the first date in the month whose day is <paramref name="fromDay"/>, through
    /// <paramref name="toDay"/> (a budget month starting the 7th has its 18th-24th in the first calendar month).
    /// </summary>
    public static (DateOnly Start, DateOnly End) Window(BudgetMonthRange month, int fromDay, int toDay)
    {
        var start = month.Start;
        while (start <= month.End && start.Day != Math.Min(fromDay, DateTime.DaysInMonth(start.Year, start.Month)))
            start = start.AddDays(1);
        if (start > month.End) start = month.Start; // a window day the month doesn't contain: the whole month
        var end = start.AddDays(Math.Max(0, toDay - fromDay));
        return (start, end > month.End ? month.End : end);
    }
}
