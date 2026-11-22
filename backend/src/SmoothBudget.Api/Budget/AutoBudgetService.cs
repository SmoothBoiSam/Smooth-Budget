using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Api.Budget;

/// <summary>
/// Knows when a household's budget is out of date: any change you make through the app, and every bank sync, marks it;
/// a new day does too. One instance per app (the app runs as a single container).
/// </summary>
public class BudgetRefreshTracker
{
    private readonly ConcurrentDictionary<Guid, long> _changes = new();
    private readonly ConcurrentDictionary<Guid, (long Changes, DateOnly Day)> _refreshed = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public void MarkChanged(Guid householdId) => _changes.AddOrUpdate(householdId, 1, (_, n) => n + 1);
    public long Changes(Guid householdId) => _changes.GetValueOrDefault(householdId);
    public bool NeedsRefresh(Guid householdId, DateOnly today) =>
        !_refreshed.TryGetValue(householdId, out var r) || r.Day != today || r.Changes != Changes(householdId);
    public void Refreshed(Guid householdId, long changes, DateOnly today) => _refreshed[householdId] = (changes, today);
    public SemaphoreSlim Lock(Guid householdId) => _locks.GetOrAdd(householdId, _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// The automatic budget (DECISIONS.md 135-137). Whenever something it depends on changes, this month's plan is rebuilt
/// from your history, bills, debt plan, savings priorities and Payday forecast, fitted to the money coming in
/// (<see cref="BudgetFitter"/>), and applied to every limit you haven't set yourself.
/// </summary>
public class AutoBudgetService(AppDbContext db, BudgetMonthService months, BudgetGeneratorService generator, Payday.PaydayService payday,
    BudgetRefreshTracker tracker, Bills.RecurringService recurring, Debt.CardInterestService interest, Transactions.CategorizationService categorization,
    Home.CurrentMonthLoader loader, TimeProvider clock)
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Refreshes the budget if anything changed since the last refresh (or it's a new day).</summary>
    public async Task EnsureFreshAsync(Guid householdId, CancellationToken ct)
    {
        var (_, today, _) = await months.ContextAsync(householdId, ct);
        if (!tracker.NeedsRefresh(householdId, today)) return;
        var gate = tracker.Lock(householdId);
        await gate.WaitAsync(ct);
        try
        {
            if (!tracker.NeedsRefresh(householdId, today)) return;
            var changes = tracker.Changes(householdId);
            await RefreshAsync(householdId, ct);
            tracker.Refreshed(householdId, changes, today);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The fitted plan for this month, with what it would change. Nothing is saved.</summary>
    public async Task<(FittedBudget Fitted, GeneratorDto Dto, IReadOnlySet<Guid> Locked)> PlanAsync(Guid householdId, CancellationToken ct, long holdBackCents = 0)
    {
        var current = await months.EnsureCurrentAsync(householdId, ct);
        var categories = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == householdId && !c.IsHidden).ToListAsync(ct);
        var monthOnly = await db.BudgetMonthCategories.AsNoTracking()
            .Where(x => x.BudgetMonthId == current.Id && x.LimitOverrideCents != null).ToDictionaryAsync(x => x.CategoryId, x => x.LimitOverrideCents!.Value, ct);

        // Yours: limits you set from now on, and this month's "this month only" changes.
        var locked = categories.Where(c => c.LimitSetByUser).ToDictionary(c => c.Id, c => c.MonthlyLimitCents);
        foreach (var (id, cents) in monthOnly) locked[id] = cents;

        var income = current.IncomeSetByUser ? null : await payday.IncomeForMonthAsync(householdId, current.StartDate, current.EndDate, ct);
        var overrides = new BudgetOverrides(
            current.IncomeSetByUser ? current.IncomeExpectedCents : income?.Cents,
            current.IncomeSetByUser ? "The income you set for this month." : income?.Why,
            locked);
        var (proposal, dto, savingsFromGoals) = await generator.BuildAsync(householdId, overrides, runCategorization: false, ct);

        // What's already spent this month: no limit goes below it.
        var range = new BudgetMonthRange(current.StartDate, current.EndDate, new DateOnly(current.StartDate.Year, current.StartDate.Month, 1));
        var txns = await BudgetMonthService.TransactionsAsync(db, householdId, current.StartDate, current.EndDate, ct);
        var spent = CategoryBreakdownCalculator.Calculate(range, categories.Select(c => new BudgetCategory(c.Id, c.Type, 0)).ToList(), txns)
            .ToDictionary(l => l.CategoryId, l => l.SpentCents);

        var fitted = BudgetFitter.Fit(proposal, spent, locked.Keys.ToHashSet(), savingsFromGoals, holdBackCents);
        var p = fitted.Proposal;
        return (fitted, dto with
        {
            IncomeCents = p.IncomeCents, IncomeExplanation = p.IncomeExplanation, Categories = p.Categories,
            AllocatedCents = p.AllocatedCents, UnallocatedCents = p.UnallocatedCents, SpendsMoreThanIncome = p.SpendsMoreThanIncome,
        }, locked.Keys.ToHashSet());
    }

    /// <summary>
    /// A limit you set that's overspent becomes a "this month only" limit of what's been spent (DECISIONS.md 271), so the
    /// Budget tab shows it as this month's override and next month starts from yours again. The automatic budget raises
    /// its own limits itself (268); with it off, every limit is yours. Same lines as 268: only with income on record, and
    /// not the card lines.
    /// </summary>
    private async Task OverrideOverspentAsync(Guid householdId, bool auto, CancellationToken ct)
    {
        var current = await months.EnsureCurrentAsync(householdId, ct);
        var range = new BudgetMonthRange(current.StartDate, current.EndDate, new DateOnly(current.StartDate.Year, current.StartDate.Month, 1));
        if (MonthData.MoneyIn(current, await months.ContributionsAsync(householdId, range, ct)) <= 0) return;
        var categories = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == householdId && !c.IsHidden).ToListAsync(ct);
        var rows = await db.BudgetMonthCategories.Where(x => x.BudgetMonthId == current.Id).ToDictionaryAsync(x => x.CategoryId, ct);
        long LimitOf(Category c) => rows.TryGetValue(c.Id, out var r) && r.LimitOverrideCents is { } o ? o : c.MonthlyLimitCents;
        var forCalculator = categories.Select(c => new BudgetCategory(c.Id, c.Type, LimitOf(c), Core.Transactions.CardInterest.IsInterestCategory(c.Name, c.Type))).ToList();
        var pool = CategoryBreakdownCalculator.CardPool(forCalculator);
        var txns = await BudgetMonthService.TransactionsAsync(db, householdId, current.StartDate, current.EndDate, ct);
        var spent = CategoryBreakdownCalculator.Calculate(range, forCalculator, txns).ToDictionary(l => l.CategoryId, l => l.SpentCents);
        var changed = false;
        foreach (var c in categories)
        {
            var yours = !auto || c.LimitSetByUser || (rows.TryGetValue(c.Id, out var had) && had.LimitOverrideCents is not null);
            if (!yours || pool.Contains(c.Id) || spent[c.Id] <= LimitOf(c)) continue;
            if (!rows.TryGetValue(c.Id, out var row))
                db.BudgetMonthCategories.Add(rows[c.Id] = row = new BudgetMonthCategory { Id = Guid.NewGuid(), BudgetMonthId = current.Id, CategoryId = c.Id });
            row.LimitOverrideCents = spent[c.Id];
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    public async Task RefreshAsync(Guid householdId, CancellationToken ct)
    {
        await interest.EnsureAsync(householdId, ct); // card interest is a bill line, not spending (DECISIONS.md 159)
        await recurring.KeepBillsTogetherAsync(householdId, ct); // a bill's charges follow the one you filed (DECISIONS.md 158)
        if (await categorization.ReleaseHiddenAsync(householdId, ct) > 0) // spending left in a hidden category (DECISIONS.md 177)
            await categorization.RunAsync(householdId, ct);
        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        await OverrideOverspentAsync(householdId, household.AutoBudget, ct);
        if (!household.AutoBudget) return;
        if (!await db.Categories.AnyAsync(c => c.HouseholdId == householdId && !c.IsHidden, ct)) return; // set up categories first

        var current = await months.EnsureCurrentAsync(householdId, ct);
        var categories = await db.Categories.Where(c => c.HouseholdId == householdId && !c.IsHidden).ToDictionaryAsync(c => c.Id, ct);
        var before = categories.ToDictionary(x => x.Key, x => x.Value.MonthlyLimitCents);
        var incomeFrom = current.IncomeExpectedCents;

        // Fit the month's money, then check it against the bank (DECISIONS.md 233): if the accounts can't cover the plan
        // (last month's card charges still to pay, say), fit again to what they can: the plan less the shortfall, so the
        // money held back is everything not planned plus the shortfall. A few rounds at most: each squeeze lowers what's
        // planned about as much as it lowers what the accounts need.
        FittedBudget fitted = null!;
        BudgetProposal p = null!;
        IReadOnlySet<Guid> locked = new HashSet<Guid>();
        long holdBack = 0;
        for (var round = 0; round < 3; round++)
        {
            (fitted, _, locked) = await PlanAsync(householdId, ct, holdBack);
            p = fitted.Proposal;
            foreach (var line in p.Categories.Where(l => !locked.Contains(l.CategoryId)))
                if (categories.TryGetValue(line.CategoryId, out var c)) c.MonthlyLimitCents = line.ProposedCents;
            if (!current.IncomeSetByUser)
            {
                current.IncomeExpectedCents = p.IncomeCents;
                current.PartnerExpectedCents = p.PartnerCents;
            }
            await db.SaveChangesAsync(ct);
            var cashShort = (await loader.LoadAsync(householdId, ct)).LeftToSpend().CashShortCents;
            if (cashShort <= 0 || fitted.ShortCents > 0) break; // fits the bank, or nothing left to squeeze
            holdBack = Math.Max(0, p.IncomeCents + p.PartnerCents - p.AllocatedCents) + cashShort;
        }
        current.AutoHeldBackCents = holdBack;

        var changes = categories.Values.Where(c => before.TryGetValue(c.Id, out var from) && from != c.MonthlyLimitCents)
            .Select(c => (c.Name, From: before[c.Id], To: c.MonthlyLimitCents)).ToList();
        current.AutoShortCents = fitted.ShortCents;

        // Only real changes get a note; a dollar or less isn't worth one.
        var worth = changes.Where(x => Math.Abs(x.To - x.From) >= 1_00).OrderByDescending(x => Math.Abs(x.To - x.From)).ToList();
        var incomeMoved = !current.IncomeSetByUser && Math.Abs(p.IncomeCents - incomeFrom) >= 1_00;
        if (worth.Count > 0 || incomeMoved)
        {
            var parts = new List<string>();
            if (incomeMoved) parts.Add($"Expected income {Money(incomeFrom)} → {Money(p.IncomeCents)}");
            parts.AddRange(worth.Take(5).Select(x => $"{x.Name} {(x.To > x.From ? "+" : "−")}{Money(Math.Abs(x.To - x.From))}"));
            if (worth.Count > 5) parts.Add($"{worth.Count - 5} more");
            current.AutoNote = string.Join(" · ", parts) + (fitted.SqueezedCents > 0
                ? $". {Money(fitted.SqueezedCents)} was squeezed out of spending to fit this month's money{(holdBack > 0 ? " and what your accounts can cover" : "")}."
                : ".");
            current.AutoNoteUtc = clock.GetUtcNow().UtcDateTime;
        }
        await db.SaveChangesAsync(ct);
    }

    private static string Money(long cents) => (cents / 100m).ToString("C", Us);
}

public static class AutoBudgetEndpoints
{
    /// <summary>Anything you change through the app (not reads, not sign-in) marks the budget for a refresh.</summary>
    public static void UseBudgetChangeTracking(this WebApplication app) => app.Use(async (ctx, next) =>
    {
        await next();
        if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method) || ctx.Response.StatusCode >= 400) return;
        if (!ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/api/auth")) return;
        if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.FindFirst(HouseholdClaimsFactory.HouseholdClaim) is not null)
            ctx.RequestServices.GetRequiredService<BudgetRefreshTracker>().MarkChanged(ctx.User.HouseholdId());
    });
}
