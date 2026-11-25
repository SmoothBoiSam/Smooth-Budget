using System.Globalization;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Budgeting;

/// <param name="ShortCents">What the plan still needs beyond the month's money after every squeeze (0 when it fits).</param>
/// <param name="SqueezedCents">How much was taken off limits to make it fit.</param>
/// <param name="Unsqueezed">The proposal before any squeeze: each limit where it would be with enough money (DECISIONS.md 223).</param>
public sealed record FittedBudget(BudgetProposal Proposal, long ShortCents, long SqueezedCents, BudgetProposal? Unsqueezed = null);

/// <summary>
/// Fits a month's budget to the money coming in (DECISIONS.md 136). The generator already gives the leftover to your
/// priorities only when there is some, so extra debt payments and savings goals are the first to drop. If the plan
/// still costs more than the month brings in, it squeezes, in this order:
/// <list type="number">
/// <item>nice-to-have spending (DECISIONS.md 182), as far as what's already spent;</item>
/// <item>savings transfers that aren't part of your goals plan (a habit, not a bill);</item>
/// <item>card payments beyond the minimums, Card interest first (DECISIONS.md 226): the minimums already include the
/// interest, so in a pinch they're all the cards need;</item>
/// <item>important spending, then must-have spending, each down to <see cref="EssentialFloorPercent"/>% of its usual amount;</item>
/// <item>then, only if the month's money still doesn't cover the plan, important and then must-have spending below that,
/// as far as what's already spent (DECISIONS.md 226), so the limits never add up to more than there is to spend.</item>
/// </list>
/// Within a step, categories give in proportion to the room each has.
/// Every line is first raised to whatever was already spent or paid on it this month (DECISIONS.md 173, 268), so an
/// overspent category takes its room from the others, from extra card payments and from savings.
/// Nothing goes below what's already been spent this month, limits you set yourself are never touched, and bills and
/// card minimums aren't squeezed. Whatever is still missing (bills and minimums alone cost more than the month brings in)
/// is reported as a shortfall.
/// </summary>
public static class BudgetFitter
{
    public const int EssentialFloorPercent = 80;
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    /// <param name="spentThisMonth">Spent (or paid) so far this month, by category: a floor for every limit.</param>
    /// <param name="locked">Categories whose limit you set yourself.</param>
    /// <param name="savingsFromGoals">True when the savings line comes from your goals plan (already 0 when there's no leftover).</param>
    /// <param name="holdBackCents">Money in that the bank accounts can't cover this month (DECISIONS.md 233): the plan fits
    /// the month's money less this.</param>
    public static FittedBudget Fit(BudgetProposal p, IReadOnlyDictionary<Guid, long> spentThisMonth, IReadOnlySet<Guid> locked, bool savingsFromGoals,
        long holdBackCents = 0)
    {
        long Spent(Guid id) => Math.Max(0, spentThisMonth.GetValueOrDefault(id));
        // DECISIONS.md 173, 268: money already spent or paid this month is gone, so every line is at least that much: a bill
        // that went up, an extra card payment, and an overspent category too. The plan then fits around it, so the overspend
        // comes out of the other categories, extra card payments and savings. A limit you set yourself is raised here
        // only for the plan; the month's limit is raised where it's loaded, and yours stays as you set it.
        var lines = p.Categories.Select(l => Spent(l.CategoryId) > l.ProposedCents
            ? l with
            {
                ProposedCents = Spent(l.CategoryId),
                Explanation = $"{l.Explanation} Raised to {Money(Spent(l.CategoryId))}, what's already been {(l.Type == CategoryType.Variable ? "spent" : "paid")} this month.",
            }
            : l).ToList();
        if (!lines.SequenceEqual(p.Categories))
        {
            var raised = lines.Sum(l => l.ProposedCents);
            // A copy: the squeeze below edits \`lines\`, and this stays the unsqueezed proposal (DECISIONS.md 223).
            p = p with { Categories = lines.ToList(), AllocatedCents = raised, UnallocatedCents = p.MoneyCents - raised, SpendsMoreThanIncome = raised > p.MoneyCents };
        }

        var available = p.MoneyCents - Math.Max(0, holdBackCents);
        var over = p.AllocatedCents - available;
        if (over <= 0) return new FittedBudget(p, 0, 0, p);
        long Floor80(ProposedLimit l) => Math.Max(Spent(l.CategoryId), RoundUpToDollar(l.MedianCents * EssentialFloorPercent / 100));
        long SpentOnly(ProposedLimit l) => Spent(l.CategoryId);
        static bool Spending(ProposedLimit l, CategoryPriority p) => l.Type == CategoryType.Variable && l.Priority == p;
        static bool Card(ProposedLimit l) => l.Type == CategoryType.DebtPayment || CardInterest.IsInterestCategory(l.Name, l.Type);

        long squeezed = 0;
        Squeeze(l => Spending(l, CategoryPriority.Low), SpentOnly);
        Squeeze(l => l.Type == CategoryType.SavingsTransfer && !savingsFromGoals, SpentOnly);
        CardsToMinimums();
        Squeeze(l => Spending(l, CategoryPriority.Medium), Floor80);
        Squeeze(l => Spending(l, CategoryPriority.High), Floor80);
        Squeeze(l => Spending(l, CategoryPriority.Medium), SpentOnly);
        Squeeze(l => Spending(l, CategoryPriority.High), SpentOnly);

        var allocated = lines.Sum(l => l.ProposedCents);
        var fitted = p with { Categories = lines, AllocatedCents = allocated, UnallocatedCents = p.MoneyCents - allocated, SpendsMoreThanIncome = allocated > available };
        return new FittedBudget(fitted, Math.Max(0, allocated - available), squeezed, p);

        // The card lines (payments and Card interest) together come down to the minimums, never below them or what's
        // already been paid, Card interest first: the minimums include the interest.
        void CardsToMinimums()
        {
            if (over <= 0 || p.CardMinimumsCents is not { } minimums) return;
            var all = lines.Where(Card).ToList();
            var room = all.Sum(l => l.ProposedCents) - Math.Max(minimums, all.Sum(l => Spent(l.CategoryId)));
            var take = Math.Min(over, room);
            foreach (var line in all.Where(l => !locked.Contains(l.CategoryId)).OrderBy(l => l.Type == CategoryType.DebtPayment ? 1 : 0))
            {
                if (take <= 0) break;
                var cut = Math.Min(take, line.ProposedCents - Math.Min(line.ProposedCents, Spent(line.CategoryId)));
                if (cut <= 0) continue;
                lines[lines.IndexOf(line)] = line with
                {
                    ProposedCents = line.ProposedCents - cut,
                    Explanation = $"{line.Explanation} Squeezed by {Money(cut)} (from {Money(line.ProposedCents)}): this month the cards get their minimums, {Money(minimums)}, which include the interest.",
                };
                take -= cut;
                over -= cut;
                squeezed += cut;
            }
        }

        void Squeeze(Func<ProposedLimit, bool> pick, Func<ProposedLimit, long> floorOf)
        {
            if (over <= 0) return;
            var candidates = lines.Where(l => !locked.Contains(l.CategoryId) && pick(l))
                .Select(l => (Line: l, Room: Math.Max(0, l.ProposedCents - Math.Min(l.ProposedCents, floorOf(l)))))
                .Where(x => x.Room > 0).ToList();
            var room = candidates.Sum(x => x.Room);
            if (room == 0) return;
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
                // Squeezed twice (to 80%, then below): one note, from where it started.
                var start = p.Categories.First(l => l.CategoryId == line.CategoryId);
                lines[lines.IndexOf(line)] = line with
                {
                    ProposedCents = newLimit,
                    Explanation = $"{start.Explanation} Squeezed by {Money(start.ProposedCents - newLimit)} (from {Money(start.ProposedCents)}) to fit this month's money.",
                };
                if (cut >= target) break;
            }
            over -= cut;
            squeezed += cut;
        }
    }

    private static long RoundUpToDollar(long cents) => cents <= 0 ? 0 : (cents + 99) / 100 * 100;
    private static long RoundDownToDollar(long cents) => cents <= 0 ? 0 : cents / 100 * 100;
    private static string Money(long cents) => (cents / 100m).ToString("C", Us);
}
