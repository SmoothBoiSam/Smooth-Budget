namespace SmoothBudget.Core.Budgeting;

/// <param name="ClosingNetCents">The month's final Left to Spend: unspent minus overspent across spending categories, minus any deficit carried in,
/// a partner shortfall, spending not filed and a plan beyond the month's money (the last three are kept for showing why).</param>
/// <param name="SurplusCents">What's left over, to sweep toward debt or savings. Zero when there's a deficit.</param>
/// <param name="DeficitCents">What carries into next month as "Carried over". Zero when there's a surplus.</param>
public sealed record MonthCloseResult(
    BudgetMonthRange Month,
    long TotalUnspentCents,
    long TotalOverspentCents,
    long CarriedInDeficitCents,
    long ClosingNetCents,
    long SurplusCents,
    long DeficitCents,
    IReadOnlyList<CategoryStatus> Categories,
    long PartnerShortfallCents = 0,
    long UnfiledSpendingCents = 0,
    long OverplannedCents = 0);

/// <summary>
/// Closes a budget month (SPEC section 3, "Rollover and carry-forward"): nets the variance across all
/// spending categories (total unspent minus total overspent). A deficit carried in from the month before
/// is part of that net, so it's paid back before anything counts as surplus (DECISIONS.md 65). The result:
/// <list type="bullet">
/// <item>a surplus is never spendable next month; it becomes a sweep toward debt or savings;</item>
/// <item>a deficit carries into next month and is subtracted from its Left to Spend.</item>
/// </list>
/// Each category keeps its own over or under for the record; only the month-level net carries.
/// </summary>
public static class MonthCloseCalculator
{
    public static MonthCloseResult Close(
        BudgetMonthRange month,
        IEnumerable<BudgetCategory> categories,
        IEnumerable<BudgetTransaction> transactions,
        long carriedInDeficitCents,
        long partnerShortfallCents = 0,
        long moneyInCents = 0)
    {
        if (carriedInDeficitCents < 0)
            throw new ArgumentOutOfRangeException(nameof(carriedInDeficitCents), "A carried-in deficit is a positive amount of cents.");
        ArgumentOutOfRangeException.ThrowIfNegative(partnerShortfallCents);

        var cats = categories.ToList();
        var txns = transactions.ToList();
        var lines = CategoryBreakdownCalculator.Calculate(month, cats, txns);
        var spending = lines.Where(l => l.Type == CategoryType.Variable).ToList();

        var unspent = spending.Where(l => l.RemainingCents > 0).Sum(l => l.RemainingCents);
        var overspent = spending.Where(l => l.RemainingCents < 0).Sum(l => -l.RemainingCents);
        // A partner contribution that never came (or came short) is missing money, like a deficit (SPEC section 3).
        // So is spending still not filed when the month closes, and a plan that went beyond the month's money
        // (DECISIONS.md 177-178): the sweep must never send money that isn't there.
        var unfiled = LeftToSpendCalculator.UnfiledSpending(month, cats, txns);
        var overplanned = LeftToSpendCalculator.Overplanned(month, cats, txns, moneyInCents);
        var net = unspent - overspent - carriedInDeficitCents - partnerShortfallCents - unfiled - overplanned;

        return new MonthCloseResult(month, unspent, overspent, carriedInDeficitCents, net,
            SurplusCents: Math.Max(0, net),
            DeficitCents: Math.Max(0, -net),
            lines, partnerShortfallCents, unfiled, overplanned);
    }
}


public enum SweepTarget { Debt = 0, Savings = 1, Split = 2 }

/// <summary>Where leftover money goes (SPEC section 3): debt (the default), savings, or a percentage split.</summary>
public sealed record SweepPreference(SweepTarget Target, int SavingsPercent = 0)
{
    public static readonly SweepPreference Default = new(SweepTarget.Debt);

    /// <summary>The share that goes to savings, 0 to 100: the one split for this month's plan and a closed month's leftover (DECISIONS.md 321).</summary>
    public int SavingsShare => Target switch { SweepTarget.Savings => 100, SweepTarget.Split => Math.Clamp(SavingsPercent, 0, 100), _ => 0 };
}

public enum SweepKind { Debt = 0, Savings = 1 }

/// <param name="Destination">What to tell the user: "Send $X to {Destination}".</param>
public sealed record SweepItem(SweepKind Kind, long AmountCents, string Destination);

public static class SweepPlanner
{
    /// <summary>
    /// Turns a surplus into "send $X to …" items. The app can't move money, so these become checklist
    /// items. A split sends <see cref="SweepPreference.SavingsPercent"/> to savings (rounded down to the
    /// cent) and the rest to debt. With no card to pay down, everything goes to savings, and vice versa.
    /// </summary>
    /// <param name="debtDestination">The card to pay down, or null when there's none.</param>
    /// <param name="savingsDestination">The savings account or goal, or null when there's none.</param>
    public static IReadOnlyList<SweepItem> Plan(long surplusCents, SweepPreference preference, string? debtDestination, string? savingsDestination)
    {
        if (surplusCents <= 0)
            return [];
        if (preference.SavingsPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(preference), "Savings percent must be 0 to 100.");

        var target = preference.Target;
        if (debtDestination is null && savingsDestination is null)
            return [new SweepItem(target == SweepTarget.Savings ? SweepKind.Savings : SweepKind.Debt, surplusCents, "debt or savings")];
        if (debtDestination is null) target = SweepTarget.Savings;
        if (savingsDestination is null) target = SweepTarget.Debt;

        switch (target)
        {
            case SweepTarget.Savings:
                return [new SweepItem(SweepKind.Savings, surplusCents, savingsDestination!)];
            case SweepTarget.Split:
                var toSavings = surplusCents * preference.SavingsPercent / 100; // integer division rounds down
                var toDebt = surplusCents - toSavings;
                var items = new List<SweepItem>();
                if (toDebt > 0) items.Add(new SweepItem(SweepKind.Debt, toDebt, debtDestination!));
                if (toSavings > 0) items.Add(new SweepItem(SweepKind.Savings, toSavings, savingsDestination!));
                return items;
            default:
                return [new SweepItem(SweepKind.Debt, surplusCents, debtDestination!)];
        }
    }
}
