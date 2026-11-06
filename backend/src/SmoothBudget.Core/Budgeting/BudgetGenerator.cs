using System.Globalization;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Budgeting;

public sealed record GeneratorCategory(Guid Id, string Name, CategoryType Type, bool IsDiscretionary, long CurrentLimitCents);

/// <param name="MonthlyCents">What was spent (or paid) in each history month, oldest first.</param>
/// <param name="Explanation">Why this number, in plain words (SPEC 6.1 step 7: every suggestion is explainable).</param>
public sealed record ProposedLimit(
    Guid CategoryId, string Name, CategoryType Type, long CurrentLimitCents, long MedianCents, long ProposedCents,
    string Explanation, IReadOnlyList<long> MonthlyCents);

public sealed record BudgetProposal(
    int MonthsUsed,
    long IncomeCents,
    string IncomeExplanation,
    IReadOnlyList<long> MonthlyIncomeCents,
    IReadOnlyList<ProposedLimit> Categories,
    long AllocatedCents,
    long UnallocatedCents,
    bool SpendsMoreThanIncome,
    int UncategorizedCount);

/// <summary>
/// Proposes a budget from the last few complete budget months (SPEC section 6.1):
/// <list type="number">
/// <item>Spending per category per month uses the same rules as the Budget screen (transfers excluded,
/// refunds netted, owner's share of splits).</item>
/// <item>The median month is used, so a one-off purchase doesn't skew it. Months with no spending count as $0.</item>
/// <item>Fixed bills: the median, to the cent. Spending categories: the median rounded up to the whole dollar,
/// trimmed by <see cref="DiscretionaryTrimPercent"/>% for discretionary ones (eating out, shopping,
/// entertainment) while there's card debt. Debt payments and savings: the median paid.</item>
/// <item>Income is the median monthly income (paychecks and partner money). What isn't allocated is shown
/// as an explicit remainder, never silently spread around.</item>
/// </list>
/// <para>
/// PLANNED (DECISIONS.md, "Planned: budget generator hookups by milestone"): later milestones replace parts
/// of this with better inputs: card minimums plus extra to the highest-APR card (5), detected recurring
/// bills (6), partner offsets and split shares with partner money removed from income (7), savings goals
/// (8), and the Payday forecast as income plus the shortfall adjustment (9). Each comes with its own test.
/// </para>
/// </summary>
public static class BudgetGenerator
{
    public const int MaxMonths = 6;
    public const int DiscretionaryTrimPercent = 10;
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static BudgetProposal Propose(
        IReadOnlyList<BudgetMonthRange> history,
        IReadOnlyList<GeneratorCategory> categories,
        IReadOnlyList<BudgetTransaction> transactions,
        bool hasCardDebt)
    {
        var months = history.OrderBy(m => m.Start).TakeLast(MaxMonths).ToList();
        var n = months.Count;
        var calcCategories = categories.Select(c => new BudgetCategory(c.Id, c.Type, 0)).ToList();

        var spentByMonth = months.Select(m => CategoryBreakdownCalculator.Calculate(m, calcCategories, transactions)
            .ToDictionary(l => l.CategoryId, l => l.SpentCents)).ToList();

        var incomeByMonth = months.Select(m => transactions
            .Where(t => m.Contains(t.Date) && !t.IsTransfer && t.Kind == TransactionKind.Income && t.AmountCents < 0)
            .Sum(t => -t.AmountCents)).ToList();
        var income = Median(incomeByMonth);

        var proposals = categories.Select(c =>
        {
            var monthly = spentByMonth.Select(s => s[c.Id]).ToList();
            var median = Median(monthly);
            long proposed;
            string why;
            if (n == 0)
            {
                proposed = c.CurrentLimitCents;
                why = "No complete months of history yet; keeping the current limit.";
            }
            else if (c.Type == CategoryType.Variable)
            {
                var trim = c.IsDiscretionary && hasCardDebt;
                var basis = trim ? median * (100 - DiscretionaryTrimPercent) / 100 : median;
                proposed = RoundUpToDollar(basis);
                why = median == 0
                    ? $"No spending here in the last {Months(n)}."
                    : $"Your {n}-month median is {Money(median)}" + (trim ? $", trimmed {DiscretionaryTrimPercent}% while you have card debt." : ".");
            }
            else
            {
                proposed = median;
                why = c.Type switch
                {
                    CategoryType.FixedBill => median == 0 ? $"Not paid in the last {Months(n)}." : $"Usually {Money(median)} a month (median of {Months(n)}).",
                    CategoryType.DebtPayment => median == 0 ? $"No card payments in the last {Months(n)}." : $"You paid a median of {Money(median)} a month toward cards.",
                    _ => median == 0 ? $"No transfers to savings in the last {Months(n)}." : $"You moved a median of {Money(median)} a month to savings.",
                };
            }
            return new ProposedLimit(c.Id, c.Name, c.Type, c.CurrentLimitCents, median, proposed, why, monthly);
        }).ToList();

        var allocated = proposals.Sum(p => p.ProposedCents);
        var uncategorized = transactions.Count(t => months.Any(m => m.Contains(t.Date))
            && TransactionRules.NeedsCategory(t.CategoryId is { } id && categories.Any(c => c.Id == id), t.IsTransfer, t.Kind, t.AmountCents));

        return new BudgetProposal(
            n, income,
            n == 0 ? "No complete months of history yet." : $"Median monthly income over {Months(n)}: paychecks and partner money.",
            incomeByMonth, proposals, allocated, income - allocated, allocated > income, uncategorized);
    }

    /// <summary>The middle value; for an even count, the average of the two middle values rounded half up.</summary>
    public static long Median(IReadOnlyList<long> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        if (sorted.Count % 2 == 1) return sorted[mid];
        var sum = sorted[mid - 1] + sorted[mid];
        return sum >= 0 ? (sum + 1) / 2 : -((-sum + 1) / 2);
    }

    public static long RoundUpToDollar(long cents) => cents <= 0 ? 0 : (cents + 99) / 100 * 100;

    private static string Months(int n) => n == 1 ? "1 month" : $"{n} months";
    private static string Money(long cents) => (cents / 100m).ToString("C", Us);
}
