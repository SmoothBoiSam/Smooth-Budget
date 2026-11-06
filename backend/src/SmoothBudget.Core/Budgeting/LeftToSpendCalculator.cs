using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Budgeting;

/// <summary>A category as the calculator sees it. Money is integer cents.</summary>
public sealed record BudgetCategory(Guid Id, CategoryType Type, long MonthlyLimitCents);

/// <summary>
/// A transaction as the calculator sees it.
/// Sign convention: positive <see cref="AmountCents"/> is money out (a purchase),
/// negative is money in (a refund, paycheck, or transfer in).
/// <see cref="SplitFactor"/> is the owner's share (1.0 = all of it, 0.5 = half).
/// </summary>
public sealed record BudgetTransaction(
    Guid? CategoryId,
    DateOnly Date,
    long AmountCents,
    bool IsTransfer = false,
    bool Pending = false,
    decimal SplitFactor = 1m,
    TransactionKind Kind = TransactionKind.Purchase);

public enum LeftToSpendStatus
{
    /// <summary>At least 15% of the variable budget is left.</summary>
    Healthy,
    /// <summary>Under 15% of the variable budget is left, but not overspent.</summary>
    Low,
    /// <summary>Left to Spend is below zero.</summary>
    Overspent,
}

public sealed record CategorySpend(Guid CategoryId, long LimitCents, long SpentCents)
{
    /// <summary>Limit minus spent. Negative means over budget.</summary>
    public long RemainingCents => LimitCents - SpentCents;
    public bool IsOver => SpentCents > LimitCents;
}

public sealed record LeftToSpendResult(
    BudgetMonthRange Month,
    DateOnly Today,
    long LeftToSpendCents,
    long TotalVariableLimitCents,
    long TotalVariableSpentCents,
    long CarriedInDeficitCents,
    int OverBudgetCategoryCount,
    long OverBudgetCents,
    int DaysRemaining,
    long SafeToSpendPerDayCents,
    LeftToSpendStatus Status,
    int UncategorizedSpendingCount,
    IReadOnlyList<CategorySpend> Categories);

/// <summary>
/// Computes the headline number (SPEC section 3, "Left to Spend"):
/// <code>Left to Spend = Σ over variable categories (limit − spent) − carried-in deficit</code>
/// Rules applied:
/// <list type="bullet">
/// <item>Only transactions dated inside the budget month count.</item>
/// <item>Pending transactions count immediately.</item>
/// <item>Transfers (card payments, own-account moves, savings transfers) never count as spending.</item>
/// <item>Refunds (negative amounts) reduce a category's spent amount.</item>
/// <item>Only the owner's share (<see cref="BudgetTransaction.SplitFactor"/>) counts.</item>
/// <item>Overspending in one category reduces the shared total.</item>
/// </list>
/// </summary>
public static class LeftToSpendCalculator
{
    /// <summary>Below this share of the variable budget, the status turns from Healthy to Low.</summary>
    public const int LowThresholdPercent = 15;

    public static LeftToSpendResult Calculate(
        BudgetMonthRange month,
        DateOnly today,
        IEnumerable<BudgetCategory> categories,
        IEnumerable<BudgetTransaction> transactions,
        long carriedInDeficitCents)
    {
        ArgumentNullException.ThrowIfNull(month);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(transactions);
        if (carriedInDeficitCents < 0)
            throw new ArgumentOutOfRangeException(nameof(carriedInDeficitCents), "A carried-in deficit is a positive amount of cents.");

        var daysRemaining = month.DaysRemaining(today); // throws if today is outside the month

        var variable = categories
            .Where(c => c.Type == CategoryType.Variable)
            .ToDictionary(c => c.Id);
        var knownCategoryIds = categories.Select(c => c.Id).ToHashSet();

        var spentByCategory = variable.Keys.ToDictionary(id => id, _ => 0L);
        var uncategorized = 0;

        foreach (var t in transactions)
        {
            if (!month.Contains(t.Date) || t.IsTransfer)
                continue;

            if (t.CategoryId is null || !knownCategoryIds.Contains(t.CategoryId.Value))
            {
                if (TransactionRules.NeedsCategory(hasKnownCategory: false, t.IsTransfer, t.Kind, t.AmountCents))
                    uncategorized++;
                continue;
            }

            if (spentByCategory.TryGetValue(t.CategoryId.Value, out var spent))
                spentByCategory[t.CategoryId.Value] = spent + OwnerShare(t);
        }

        var lines = variable.Values
            .Select(c => new CategorySpend(c.Id, c.MonthlyLimitCents, FloorAtZero(spentByCategory[c.Id])))
            .ToList();

        var totalLimit = lines.Sum(l => l.LimitCents);
        var totalSpent = lines.Sum(l => l.SpentCents);
        var leftToSpend = lines.Sum(l => l.RemainingCents) - carriedInDeficitCents;
        var over = lines.Where(l => l.IsOver).ToList();

        return new LeftToSpendResult(
            Month: month,
            Today: today,
            LeftToSpendCents: leftToSpend,
            TotalVariableLimitCents: totalLimit,
            TotalVariableSpentCents: totalSpent,
            CarriedInDeficitCents: carriedInDeficitCents,
            OverBudgetCategoryCount: over.Count,
            OverBudgetCents: over.Sum(l => -l.RemainingCents),
            DaysRemaining: daysRemaining,
            SafeToSpendPerDayCents: leftToSpend <= 0 ? 0 : leftToSpend / daysRemaining, // integer division floors: never suggest more than is safe
            Status: StatusFor(leftToSpend, totalLimit),
            UncategorizedSpendingCount: uncategorized,
            Categories: lines);
    }

    /// <summary>
    /// Refunds can bring a category's spending down to zero but never below: a large or mislabeled credit
    /// must not create extra money to spend beyond the category's limit (DECISIONS.md 47).
    /// </summary>
    public static long FloorAtZero(long spentCents) => Math.Max(0, spentCents);

    /// <summary>The owner's share of a transaction, rounded half away from zero to the cent.</summary>
    public static long OwnerShare(BudgetTransaction t)
    {
        if (t.SplitFactor is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(t), t.SplitFactor, "Split factor must be between 0 and 1.");
        return t.SplitFactor == 1m
            ? t.AmountCents
            : (long)Math.Round(t.AmountCents * t.SplitFactor, 0, MidpointRounding.AwayFromZero);
    }

    public static LeftToSpendStatus StatusFor(long leftToSpendCents, long totalVariableLimitCents)
    {
        if (leftToSpendCents < 0)
            return LeftToSpendStatus.Overspent;
        // left / total < 15%  <=>  left * 100 < 15 * total   (integer math, no rounding).
        // Exactly zero left is always Low, even when there is no variable budget at all.
        return leftToSpendCents == 0 || leftToSpendCents * 100 < LowThresholdPercent * totalVariableLimitCents
            ? LeftToSpendStatus.Low
            : LeftToSpendStatus.Healthy;
    }
}
