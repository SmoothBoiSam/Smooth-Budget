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
    BudgetRefreshTracker tracker, Bills.RecurringService recurring, Debt.CardInterestService interest, TimeProvider clock)
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
    public async Task<(FittedBudget Fitted, GeneratorDto Dto, IReadOnlySet<Guid> Locked)> PlanAsync(Guid householdId, CancellationToken ct)
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
        var txns = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Date >= current.StartDate && t.Date <= current.EndDate && t.Account.IncludedInBudget)
            .Select(t => new BudgetTransaction(t.CategoryId, t.Date, t.AmountCents, t.IsTransfer, t.Pending, t.SplitFactor, t.Kind, t.Account.Type == AccountType.CreditCard))
            .ToListAsync(ct);
        var spent = CategoryBreakdownCalculator.Calculate(range, categories.Select(c => new BudgetCategory(c.Id, c.Type, 0)).ToList(), txns)
            .ToDictionary(l => l.CategoryId, l => l.SpentCents);

        var fitted = BudgetFitter.Fit(proposal, spent, locked.Keys.ToHashSet(), savingsFromGoals);
        var p = fitted.Proposal;
        return (fitted, dto with
        {
            IncomeCents = p.IncomeCents, IncomeExplanation = p.IncomeExplanation, Categories = p.Categories,
            AllocatedCents = p.AllocatedCents, UnallocatedCents = p.UnallocatedCents, SpendsMoreThanIncome = p.SpendsMoreThanIncome,
        }, locked.Keys.ToHashSet());
    }

    public async Task RefreshAsync(Guid householdId, CancellationToken ct)
    {
        await interest.EnsureAsync(householdId, ct); // card interest is a bill line, not spending (DECISIONS.md 159)
        await recurring.KeepBillsTogetherAsync(householdId, ct); // a bill's charges follow the one you filed (DECISIONS.md 158)
        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        if (!household.AutoBudget) return;
        if (!await db.Categories.AnyAsync(c => c.HouseholdId == householdId && !c.IsHidden, ct)) return; // set up categories first

        var (fitted, _, locked) = await PlanAsync(householdId, ct);
        var p = fitted.Proposal;
        var current = await months.EnsureCurrentAsync(householdId, ct);
        var categories = await db.Categories.Where(c => c.HouseholdId == householdId && !c.IsHidden).ToDictionaryAsync(c => c.Id, ct);

        var changes = new List<(string Name, long From, long To)>();
        foreach (var line in p.Categories.Where(l => !locked.Contains(l.CategoryId)))
        {
            if (!categories.TryGetValue(line.CategoryId, out var c) || c.MonthlyLimitCents == line.ProposedCents) continue;
            changes.Add((c.Name, c.MonthlyLimitCents, line.ProposedCents));
            c.MonthlyLimitCents = line.ProposedCents;
        }
        var incomeFrom = current.IncomeExpectedCents;
        if (!current.IncomeSetByUser)
        {
            current.IncomeExpectedCents = p.IncomeCents;
            current.PartnerExpectedCents = p.PartnerCents;
        }
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
            current.AutoNote = string.Join(" · ", parts) + (fitted.SqueezedCents > 0 ? $". {Money(fitted.SqueezedCents)} was squeezed out of spending to fit this month's money." : ".");
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
