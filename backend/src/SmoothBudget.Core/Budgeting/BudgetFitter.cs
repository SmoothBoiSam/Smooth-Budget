using System.Globalization;

namespace SmoothBudget.Core.Budgeting;

/// <param name="ShortCents">What the plan still needs beyond the month's money after every squeeze (0 when it fits).</param>
/// <param name="SqueezedCents">How much was taken off limits to make it fit.</param>
public sealed record FittedBudget(BudgetProposal Proposal, long ShortCents, long SqueezedCents);

/// <summary>
/// Fits a month's budget to the money coming in (DECISIONS.md 136). The generator already gives the leftover to your
/// priorities only when there is some, so extra debt payments and savings goals are the first to drop. If the plan
/// still costs more than the month brings in, it squeezes, in this order:
/// <list type="number">
/// <item>nice-to-have spending (DECISIONS.md 182), as far as what's already spent;</item>
/// <item>savings transfers that aren't part of your goals plan (a habit, not a bill);</item>
/// <item>important spending, then must-have spending, each never below <see cref="EssentialFloorPercent"/>% of its
/// usual amount.</item>
/// </list>
/// Within a step, categories give in proportion to the room each has.
/// Bill, card and savings lines are first raised to whatever was already paid on them this month.
/// Nothing goes below what's already been spent this month, limits you set yourself are never touched, and bills,
/// card minimums and debt payments aren't squeezed. Whatever is still missing is reported as a shortfall rather than
/// hidden in impossible limits.
/// </summary>
public static class BudgetFitter
{
    public const int EssentialFloorPercent = 80;
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    /// <param name="spentThisMonth">Spent (or paid) so far this month, by category: a floor for every limit.</param>
    /// <param name="locked">Categories whose limit you set yourself.</param>
    /// <param name="savingsFromGoals">True when the savings line comes from your goals plan (already 0 when there's no leftover).</param>
    public static FittedBudget Fit(BudgetProposal p, IReadOnlyDictionary<Guid, long> spentThisMonth, IReadOnlySet<Guid> locked, bool savingsFromGoals)
    {
        long Spent(Guid id) => Math.Max(0, spentThisMonth.GetValueOrDefault(id));
        // DECISIONS.md 173: money already paid this month toward a bill, a card or savings is gone, so its line is at least
        // that much (a bill that went up, a yearly charge, an extra card payment). Spending lines don't need it: going over
        // one already comes off Left to Spend.
        var lines = p.Categories.Select(l => l.Type != CategoryType.Variable && !locked.Contains(l.CategoryId) && Spent(l.CategoryId) > l.ProposedCents
            ? l with
            {
                ProposedCents = Spent(l.CategoryId),
                Explanation = $"{l.Explanation} Raised to {Money(Spent(l.CategoryId))}, what's already been paid this month.",
            }
            : l).ToList();
        if (!lines.SequenceEqual(p.Categories))
        {
            var raised = lines.Sum(l => l.ProposedCents);
            p = p with { Categories = lines, AllocatedCents = raised, UnallocatedCents = p.IncomeCents + p.PartnerCents - raised, SpendsMoreThanIncome = raised > p.IncomeCents + p.PartnerCents };
        }

        var available = p.IncomeCents + p.PartnerCents;
        var over = p.AllocatedCents - available;
        if (over <= 0) return new FittedBudget(p, 0, 0);
        long Floor80(ProposedLimit l) => Math.Max(Spent(l.CategoryId), RoundUpToDollar(l.MedianCents * EssentialFloorPercent / 100));
        var stages = new (Func<ProposedLimit, bool> Pick, Func<ProposedLimit, long> Floor, string Reason)[]
        {
            (l => l.Type == CategoryType.Variable && l.Priority == CategoryPriority.Low, l => Spent(l.CategoryId), "Nice-to-have spending is squeezed first"),
            (l => l.Type == CategoryType.SavingsTransfer && !savingsFromGoals, l => Spent(l.CategoryId), "Savings transfers are squeezed next"),
            (l => l.Type == CategoryType.Variable && l.Priority == CategoryPriority.Medium, Floor80, $"Important spending is next, to no less than {EssentialFloorPercent}% of usual"),
            (l => l.Type == CategoryType.Variable && l.Priority == CategoryPriority.High, Floor80, $"Must-have spending is squeezed last, to no less than {EssentialFloorPercent}% of usual"),
        };

        long squeezed = 0;
        foreach (var (pick, floorOf, _) in stages)
        {
            if (over <= 0) break;
            var candidates = lines.Where(l => !locked.Contains(l.CategoryId) && pick(l))
                .Select(l => (Line: l, Room: Math.Max(0, l.ProposedCents - Math.Min(l.ProposedCents, floorOf(l)))))
                .Where(x => x.Room > 0).ToList();
            var room = candidates.Sum(x => x.Room);
            if (room == 0) continue;
            var target = Math.Min(over, room);
            long cut = 0;
            foreach (var (line, r) in candidates.OrderByDescending(x => x.Room))
            {
                // Each gives up its share of the cut, in proportion to its room, rounded so limits stay in whole dollars.
                var share = (long)Math.Ceiling(target * (decimal)r / room);
                var floor = line.ProposedCents - r;
                var newLimit = Math.Max(floor, RoundDownToDollar(line.ProposedCents - Math.Min(share, target - cut)));
                if (newLimit < floor) newLimit = floor;
                var taken = line.ProposedCents - newLimit;
                if (taken <= 0) continue;
                cut += taken;
                lines[lines.IndexOf(line)] = line with
                {
                    ProposedCents = newLimit,
                    Explanation = $"{line.Explanation} Squeezed by {Money(taken)} (from {Money(line.ProposedCents)}) to fit this month's money.",
                };
                if (cut >= target) break;
            }
            over -= cut;
            squeezed += cut;
        }

        var allocated = lines.Sum(l => l.ProposedCents);
        var fitted = p with { Categories = lines, AllocatedCents = allocated, UnallocatedCents = available - allocated, SpendsMoreThanIncome = allocated > available };
        return new FittedBudget(fitted, Math.Max(0, allocated - available), squeezed);
    }

    private static long RoundUpToDollar(long cents) => cents <= 0 ? 0 : (cents + 99) / 100 * 100;
    private static long RoundDownToDollar(long cents) => cents <= 0 ? 0 : cents / 100 * 100;
    private static string Money(long cents) => (cents / 100m).ToString("C", Us);
}
