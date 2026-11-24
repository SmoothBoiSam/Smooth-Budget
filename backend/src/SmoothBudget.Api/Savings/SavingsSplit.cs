using SmoothBudget.Api.Data;

namespace SmoothBudget.Api.Savings;

/// <summary>
/// Where each month's savings goes (Savings → Payment settings, DECISIONS.md 314): a percent for each savings account. With
/// none set, it all goes to the first savings account, as before.
/// </summary>
public static class SavingsSplit
{
    /// <summary>The savings accounts that get some of it, oldest first, with their percent (adding up to 100).</summary>
    public static IReadOnlyList<(Account Account, int Percent)> Shares(IEnumerable<Account> accounts)
    {
        var savings = accounts.Where(a => a.Type == AccountType.Savings && a.IncludedInBudget).OrderBy(a => a.CreatedUtc).ThenBy(a => a.Id).ToList();
        var set = savings.Where(a => a.SavingsPercent > 0).ToList();
        if (set.Count == 0) return savings.Take(1).Select(a => (a, 100)).ToList();
        // An account that left (taken out of the budget) leaves the others' percents to add up to 100 between them.
        var percents = Divide(100, set.Select(a => (long)a.SavingsPercent!.Value).ToList());
        return set.Select((a, i) => (a, (int)percents[i])).ToList();
    }

    /// <summary>Cents divided by weights, whole cents only; what doesn't divide evenly goes to the first.</summary>
    public static IReadOnlyList<long> Divide(long cents, IReadOnlyList<long> weights)
    {
        var total = weights.Sum();
        if (total <= 0) return weights.Select(_ => 0L).ToList();
        var parts = weights.Select(w => cents * w / total).ToList();
        if (parts.Count > 0) parts[0] += cents - parts.Sum();
        return parts;
    }
}
