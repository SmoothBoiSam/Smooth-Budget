namespace SmoothBudget.Core.Budgeting;

/// <param name="AccountId">The savings account whose balance is this goal's progress (null: tracked by hand).</param>
/// <param name="ManualSavedCents">Saved so far, for a goal tracked by hand.</param>
/// <param name="IsStarter">The starter emergency fund (the rainy-day fund), the goal you can't remove.</param>
public sealed record GoalInput(
    Guid Id, string Name, long TargetCents, Guid? AccountId, long? ManualSavedCents, bool IsStarter, int Order);

/// <param name="MonthlySource">"plan" (its account's share of this month's savings), "pace" (your recent deposits), or "none".</param>
/// <param name="MonthsToGo">Months until reached at that rate; null when there's no monthly amount (or it's reached).</param>
public sealed record GoalStatus(
    Guid Id, string Name, long TargetCents, long SavedCents, long RemainingCents, int Percent, bool Reached,
    long MonthlyCents, string MonthlySource, int? MonthsToGo, DateOnly? ProjectedDate);

/// <summary>A point on the savings chart: the balance at the end of a month.</summary>
public sealed record BalancePoint(DateOnly Date, long BalanceCents);

/// <summary>
/// Savings goals (SPEC 4.5). Progress comes from account balances: goals linked to the same account are filled in
/// order (the first takes up to its target, the next the rest, and so on); a goal tracked by hand uses what you entered.
/// The projected date is the remaining amount at the monthly rate, which goes to the first unfinished goal on each
/// account (DECISIONS.md 321): the account's share of this month's planned savings, or, with none planned, your actual
/// pace (the median of the last complete months' net deposits into the account). Goals tracked by hand share
/// <c>planByHandCents</c> the same way (the month's savings when you have no savings account). Months are rounded up.
/// </summary>
public static class SavingsGoals
{
    public static IReadOnlyList<GoalStatus> Status(
        IReadOnlyList<GoalInput> goals, IReadOnlyDictionary<Guid, long> balances, IReadOnlyDictionary<Guid, long> paceByAccount, DateOnly today,
        IReadOnlyDictionary<Guid, long>? planByAccount = null, long planByHandCents = 0)
    {
        var left = balances.ToDictionary(kv => kv.Key, kv => Math.Max(0, kv.Value));
        var paceUsed = new HashSet<Guid>();
        var handUsed = false;
        var result = new List<GoalStatus>();
        foreach (var g in goals.OrderBy(g => g.Order).ThenBy(g => g.Name, StringComparer.Ordinal))
        {
            long saved;
            if (g.AccountId is { } acct)
            {
                var available = left.GetValueOrDefault(acct);
                saved = Math.Min(available, g.TargetCents);
                left[acct] = available - saved;
            }
            else
                saved = Math.Max(0, g.ManualSavedCents ?? 0);

            var remaining = Math.Max(0, g.TargetCents - saved);
            var reached = remaining == 0;
            long monthly = 0;
            var source = "none";
            if (!reached && g.AccountId is { } a && !paceUsed.Contains(a))
            {
                if (planByAccount?.GetValueOrDefault(a) is > 0 and var plan)
                    (monthly, source) = (plan, "plan");
                else if (paceByAccount.GetValueOrDefault(a) > 0)
                    (monthly, source) = (paceByAccount[a], "pace");
                paceUsed.Add(a);
            }
            else if (!reached && g.AccountId is null && !handUsed)
            {
                if (planByHandCents > 0) (monthly, source) = (planByHandCents, "plan");
                handUsed = true;
            }
            int? months = reached || monthly <= 0 ? null : (int)Math.Ceiling(remaining / (double)monthly);
            var percent = g.TargetCents <= 0 ? 100 : (int)Math.Min(100, saved * 100 / g.TargetCents);
            result.Add(new GoalStatus(g.Id, g.Name, g.TargetCents, saved, remaining, percent, reached, monthly, source,
                months, months is { } m ? today.AddMonths(m) : null));
        }
        return result;
    }

    /// <summary>
    /// The pace into an account: the median of its net deposits (money in minus money out) over the given complete
    /// months. Positive cents are money out, as everywhere in the app.
    /// </summary>
    public static long Pace(IReadOnlyList<(DateOnly Date, long AmountCents)> transactions, IReadOnlyList<BudgetMonthRange> months)
    {
        if (months.Count == 0) return 0;
        var perMonth = months.Select(m => -transactions.Where(t => m.Contains(t.Date)).Sum(t => t.AmountCents)).ToList();
        return Math.Max(0, BudgetGenerator.Median(perMonth));
    }

    /// <summary>
    /// The balance at the end of each given day, worked back from today's balance and the transactions since: each
    /// later deposit is taken off and each later withdrawal put back. Only as good as the account's transaction history.
    /// </summary>
    public static IReadOnlyList<BalancePoint> History(long currentCents, IReadOnlyList<(DateOnly Date, long AmountCents)> transactions, IReadOnlyList<DateOnly> ends) =>
        ends.Select(d => new BalancePoint(d, currentCents + transactions.Where(t => t.Date > d).Sum(t => t.AmountCents))).ToList();
}

/// <summary>
/// How the month's money left after bills, spending and card minimums is shared (DECISIONS.md 321): this percent to the
/// savings line, the rest to the cards as extra. The same split sends a closed month's leftover.
/// </summary>
public sealed record SavingsPlan(int SavingsPercent);
