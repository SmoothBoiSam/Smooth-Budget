using System.Globalization;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Budgeting;

public sealed record GeneratorCategory(Guid Id, string Name, CategoryType Type, bool IsDiscretionary, long CurrentLimitCents);

/// <param name="MonthlyCents">What was spent (or paid) in each history month, oldest first.</param>
/// <param name="Explanation">Why this number, in plain words (SPEC 6.1 step 7: every suggestion is explainable).</param>
public sealed record ProposedLimit(
    Guid CategoryId, string Name, CategoryType Type, long CurrentLimitCents, long MedianCents, long ProposedCents,
    string Explanation, IReadOnlyList<long> MonthlyCents, bool IsDiscretionary = false);

/// <summary>A detected bill or subscription filed in a category, at its current price per month (milestone 6).</summary>
public sealed record CurrentBill(Guid CategoryId, string Name, long MonthlyCents);

/// <summary>
/// What the automatic budget feeds the generator (DECISIONS.md 135): this month's income from the Payday forecast instead of
/// a median, and limits you set yourself, which it never changes.
/// </summary>
/// <param name="DebtExtraCents">The extra each month you set on the Debt tab: the card payment is the minimums plus this (as far as the month's leftover goes), instead of all the leftover.</param>
public sealed record BudgetOverrides(long? IncomeCents = null, string? IncomeExplanation = null, IReadOnlyDictionary<Guid, long>? LockedLimits = null, long? DebtExtraCents = null);

public sealed record BudgetProposal(
    int MonthsUsed,
    long IncomeCents,
    string IncomeExplanation,
    IReadOnlyList<long> MonthlyIncomeCents,
    IReadOnlyList<ProposedLimit> Categories,
    long AllocatedCents,
    // Income plus partner contributions, minus everything allocated.
    long UnallocatedCents,
    bool SpendsMoreThanIncome,
    int UncategorizedCount,
    // The partner's contributions, kept apart from the owner's income (median month).
    long PartnerCents = 0,
    IReadOnlyList<long>? MonthlyPartnerCents = null);

/// <summary>
/// Proposes a budget from the last few complete budget months (SPEC section 6.1):
/// <list type="number">
/// <item>Spending per category per month uses the same rules as the Budget screen (transfers excluded,
/// refunds netted, owner's share of splits).</item>
/// <item>The median month is used, so a one-off purchase doesn't skew it. Months with no spending count as $0.</item>
/// <item>Fixed bills: the median, to the cent. Spending categories: the median rounded up to the whole dollar,
/// trimmed by <see cref="DiscretionaryTrimPercent"/>% for discretionary ones (eating out, shopping,
/// entertainment) while there's card debt. Debt payments and savings: the median paid.</item>
/// <item>Income is the median monthly income from paychecks and other deposits; partner contributions are a
/// separate median. What isn't allocated (income plus partner money, minus the plan) is shown
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
        bool hasCardDebt,
        IReadOnlyList<Debt.DebtInput>? cardDebts = null,
        IReadOnlyList<CurrentBill>? currentBills = null,
        long? fixedPartnerCents = null,
        SavingsPlan? savings = null,
        BudgetOverrides? overrides = null)
    {
        var locked = overrides?.LockedLimits ?? new Dictionary<Guid, long>();
        var months = history.OrderBy(m => m.Start).TakeLast(MaxMonths).ToList();
        var n = months.Count;
        var calcCategories = categories.Select(c => new BudgetCategory(c.Id, c.Type, 0)).ToList();

        var spentByMonth = months.Select(m => CategoryBreakdownCalculator.Calculate(m, calcCategories, transactions)
            .ToDictionary(l => l.CategoryId, l => l.SpentCents)).ToList();

        var incomeByMonth = months.Select(m => transactions
            .Where(t => m.Contains(t.Date) && !t.IsTransfer && t.Kind == TransactionKind.Income && t.AmountCents < 0)
            .Sum(t => -t.AmountCents)).ToList();
        var income = overrides?.IncomeCents ?? Median(incomeByMonth);

        // Partner money is tracked on its own, not as the owner's income.
        var partnerByMonth = months.Select(m => transactions
            .Where(t => m.Contains(t.Date) && !t.IsTransfer && t.Kind == TransactionKind.PartnerContribution && t.AmountCents < 0)
            .Sum(t => -t.AmountCents)).ToList();
        // Milestone 7: with fixed contributions set up, partner money is exactly those (DECISIONS.md 97).
        var partner = fixedPartnerCents ?? Median(partnerByMonth);

        var proposals = categories.Select(c =>
        {
            var monthly = spentByMonth.Select(s => s[c.Id]).ToList();
            var median = Median(monthly);
            long proposed;
            string why;
            if (locked.TryGetValue(c.Id, out var mine))
            {
                proposed = mine;
                why = "You set this limit, so it stays as you set it.";
            }
            else if (n == 0)
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
            else if (c.Type == CategoryType.FixedBill && (currentBills ?? []).Where(b => b.CategoryId == c.Id).ToList() is { Count: > 0 } bills)
            {
                // Milestone 6 (DECISIONS.md 95): the bill's current price, so a price increase is picked up right away.
                proposed = bills.Sum(b => b.MonthlyCents);
                why = "Current price: " + string.Join(" + ", bills.Select(b => $"{b.Name} {Money(b.MonthlyCents)}")) + " a month"
                      + (median != proposed ? $" (your {n}-month median was {Money(median)})." : ".");
            }
            else
            {
                proposed = median;
                why = c.Type switch
                {
                    CategoryType.FixedBill => median == 0 ? $"Not paid in the last {Months(n)}." : $"Usually {Money(median)} a month (median of {Months(n)}).",
                    CategoryType.DebtPayment => median == 0 ? $"Nothing paid beyond your card purchases in the last {Months(n)}." : $"You paid a median of {Money(median)} a month beyond what you bought on the cards (the purchases already count in their categories).",
                    _ => median == 0 ? $"No transfers to savings in the last {Months(n)}." : $"You moved a median of {Money(median)} a month to savings.",
                };
            }
            return new ProposedLimit(c.Id, c.Name, c.Type, c.CurrentLimitCents, median, proposed, why, monthly, c.IsDiscretionary);
        }).ToList();

        // Milestone 5 (DECISIONS.md 89): with your cards' minimums known, the card-payment category is the sum of the
        // minimums plus whatever is left after everything else, sent to the highest-APR card.
        // Milestone 8 (DECISIONS.md 103): with savings goals, what's left goes in your priority order (default: starter fund,
        // then high-APR debt, then larger savings), and the savings line comes from the goals instead of a median.
        var owed = (cardDebts ?? []).Where(d => d.BalanceCents > 0).ToList();
        var cardCategory = proposals.FirstOrDefault(p => p.Type == CategoryType.DebtPayment && !locked.ContainsKey(p.CategoryId));
        var savingsCategory = savings is not null ? proposals.FirstOrDefault(p => p.Type == CategoryType.SavingsTransfer && !locked.ContainsKey(p.CategoryId)) : null;
        var debtStep = owed.Count > 0 && cardCategory is not null;
        if ((n > 0 || overrides?.IncomeCents is not null) && (debtStep || savingsCategory is not null))
        {
            var minimums = debtStep ? owed.Sum(d => Math.Min(d.MinimumCents, d.BalanceCents)) : 0;
            var others = proposals.Where(p => p != savingsCategory && (!debtStep || p != cardCategory)).Sum(p => p.ProposedCents);
            var left = Math.Max(0, income + partner - others - minimums);
            long toStarter = 0, toDebt = 0, toLarger = 0;
            foreach (var step in savings?.Order ?? SavingsPlan.DefaultOrder)
            {
                switch (step)
                {
                    case PriorityStep.StarterFund when savings is not null && savingsCategory is not null:
                        toStarter = Math.Min(left, savings.StarterRemainingCents);
                        left -= toStarter;
                        break;
                    case PriorityStep.Debt when debtStep:
                        // Your extra from the Debt tab (as far as the leftover goes), or else all of it, to the highest APR.
                        toDebt = overrides?.DebtExtraCents is { } extra ? Math.Min(left, Math.Max(0, extra)) : left;
                        left -= toDebt;
                        break;
                    case PriorityStep.Savings when savings is not null && savingsCategory is not null:
                        toLarger = savings.LargerMonthlyCents is { } cap ? Math.Min(left, cap) : left;
                        left -= toLarger;
                        break;
                }
            }

            if (debtStep)
            {
                var target = Debt.DebtPayoff.Order(owed, owed.ToDictionary(d => d.Id, d => d.BalanceCents), Debt.DebtStrategy.Avalanche).First();
                var why = $"Your card minimums add up to {Money(minimums)}"
                          + (toDebt > 0 && overrides?.DebtExtraCents is { } setExtra ? $", plus {Money(toDebt)} extra to {target.Name}" + (toDebt < setExtra ? $" (you set {Money(setExtra)} on the Debt tab; that's what's left this month)." : " (the extra you set on the Debt tab).")
                             : toDebt > 0 ? $", plus {Money(toDebt)} left over {(toStarter > 0 ? $"after your {savings!.StarterName.ToLowerInvariant()}" : "after everything else")}, as extra to {target.Name} ({target.AprBps / 100m:0.##}% APR, the highest)."
                             : toStarter > 0 ? $". What's left goes to your {savings!.StarterName.ToLowerInvariant()} first." : ". Nothing is left over for extra this month.");
                proposals[proposals.IndexOf(cardCategory!)] = cardCategory! with { ProposedCents = minimums + toDebt, Explanation = why };
            }
            if (savingsCategory is not null)
            {
                var parts = new List<string>();
                if (toStarter > 0)
                    parts.Add($"{Money(toStarter)} toward your {savings!.StarterName.ToLowerInvariant()} ({Money(savings.StarterSavedCents)} of {Money(savings.StarterTargetCents)} saved), first in your priorities");
                else if (savings!.StarterRemainingCents == 0 && savings.StarterTargetCents > 0)
                    parts.Add($"Your {savings.StarterName.ToLowerInvariant()} is full");
                if (toLarger > 0)
                    parts.Add($"{Money(toLarger)} toward your other goals");
                var why = parts.Count == 0
                    ? "Nothing is left for savings this month after your priorities come first."
                    : string.Join("; ", parts) + ".";
                proposals[proposals.IndexOf(savingsCategory)] = savingsCategory with { ProposedCents = toStarter + toLarger, Explanation = why };
            }
        }

        var allocated = proposals.Sum(p => p.ProposedCents);
        var uncategorized = transactions.Count(t => months.Any(m => m.Contains(t.Date))
            && TransactionRules.NeedsCategory(t.CategoryId is { } id && categories.Any(c => c.Id == id), t.IsTransfer, t.Kind, t.AmountCents));

        return new BudgetProposal(
            n, income,
            overrides?.IncomeExplanation ?? (n == 0 ? "No complete months of history yet." : $"Your median monthly income over {Months(n)}: paychecks and other deposits."),
            incomeByMonth, proposals, allocated, income + partner - allocated, allocated > income + partner, uncategorized,
            partner, partnerByMonth);
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
