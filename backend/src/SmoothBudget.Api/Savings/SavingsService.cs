using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Home;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Api.Savings;

public record SavingsAccountDto(Guid Id, string Name, string? Mask, long BalanceCents, bool BalanceFromBank, long PaceCents);

public record SavingsGoalDto(
    Guid Id, string Name, long TargetCents, Guid? AccountId, string? AccountName, long? ManualSavedCents, long? MonthlyCentsSet, bool IsStarter,
    long SavedCents, long RemainingCents, int Percent, bool Reached, long MonthlyCents, string MonthlySource, int? MonthsToGo, DateOnly? ProjectedDate);

/// <param name="Transfers">Money moved into savings, newest first (DECISIONS.md 142): tracked from your accounts, nothing to enter.</param>
/// <param name="ThisMonthCents">Moved into savings this budget month.</param>
/// <param name="PlannedCents">What this month's budget plans for savings.</param>
public record SavingsSummaryDto(
    long TotalSavedCents, IReadOnlyList<SavingsAccountDto> Accounts, IReadOnlyList<SavingsGoalDto> Goals,
    IReadOnlyList<string> Priority, IReadOnlyList<BalancePoint> History,
    IReadOnlyList<SavingsTransferDto>? Transfers = null, long ThisMonthCents = 0, long PlannedCents = 0);

public record SavingsTransferDto(DateOnly Date, long Cents, string Account, string Description);

public record GoalRequest(string Name, long TargetCents, Guid? AccountId, long? ManualSavedCents, long? MonthlyCents);
public record PriorityRequest(List<PriorityStep> Order);
public record BalanceRequest(long BalanceCents);

/// <summary>
/// The Savings tab (SPEC 4.5): savings accounts, goals with progress and a projected date from account balances, the
/// priority order for money left over, and a 12-month balance chart. The math is in <see cref="SavingsGoals"/>.
/// </summary>
public class SavingsService(AppDbContext db, BudgetMonthService months, TimeProvider clock)
{
    public const int PaceMonths = 3;
    public const long StarterTargetCents = 1_000_00;

    public async Task<SavingsSummaryDto> SummaryAsync(Guid householdId, CancellationToken ct)
    {
        await EnsureStarterAsync(householdId, ct);
        var (household, today, calendar) = await months.ContextAsync(householdId, ct);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.HouseholdId == householdId && a.Type == AccountType.Savings && a.IncludedInBudget)
            .OrderBy(a => a.Name).ToListAsync(ct);
        var ids = accounts.Select(a => a.Id).ToList();
        var txns = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && ids.Contains(t.AccountId))
            .Select(t => new { t.AccountId, t.Date, t.AmountCents }).ToListAsync(ct);

        // Pace: the last few complete budget months.
        var current = calendar.MonthContaining(today);
        var paceMonths = new List<BudgetMonthRange>();
        var m = current;
        for (var i = 0; i < PaceMonths; i++)
            paceMonths.Add(m = calendar.MonthContaining(m.Start.AddDays(-1)));
        // A bank-synced balance is the bank's. Otherwise: the balance last given (by a CSV or by you), moved by every
        // transaction after that day, or the sum of all of them if none was ever given (DECISIONS.md 189).
        var balances = accounts.ToDictionary(a => a.Id, a => a.PlaidAccountId is not null
            ? a.CurrentBalanceCents
            : a.CurrentBalanceCents - txns.Where(t => t.AccountId == a.Id && (a.BalanceAsOf is null || t.Date > a.BalanceAsOf)).Sum(t => t.AmountCents));

        var pace = accounts.ToDictionary(a => a.Id,
            a => SavingsGoals.Pace(txns.Where(t => t.AccountId == a.Id).Select(t => (t.Date, t.AmountCents)).ToList(), paceMonths));

        var goals = await db.SavingsGoals.AsNoTracking().Include(g => g.Account)
            .Where(g => g.HouseholdId == householdId).OrderBy(g => g.SortOrder).ThenBy(g => g.CreatedUtc).ToListAsync(ct);
        var statuses = SavingsGoals.Status(goals.Select(Input).ToList(), balances, pace, today);

        // The chart: total savings at the end of each of the last 12 budget months, and today.
        var ends = new List<DateOnly>();
        m = current;
        for (var i = 0; i < 12; i++)
            ends.Insert(0, (m = calendar.MonthContaining(m.Start.AddDays(-1))).End);
        ends.Add(today);
        var history = ends.Select(d => new BalancePoint(d, accounts.Sum(a =>
            SavingsGoals.History(balances[a.Id], txns.Where(t => t.AccountId == a.Id).Select(t => (t.Date, t.AmountCents)).ToList(), [d])[0].BalanceCents))).ToList();

        // Transfers into savings, tracked automatically: money into a savings account (or, with none linked, transfers out
        // of checking filed as savings).
        var savingsCats = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == householdId && c.Type == CategoryType.SavingsTransfer).Select(c => c.Id).ToListAsync(ct);
        var moves = ids.Count > 0
            ? await db.Transactions.AsNoTracking().Where(t => t.HouseholdId == householdId && ids.Contains(t.AccountId) && t.AmountCents < 0)
                .OrderByDescending(t => t.Date).Take(60)
                .Select(t => new SavingsTransferDto(t.Date, -t.AmountCents, t.Account.Name, t.IsTransfer ? "Moved to savings" : t.Description)).ToListAsync(ct)
            : await db.Transactions.AsNoTracking().Where(t => t.HouseholdId == householdId && t.AmountCents > 0 && t.CategoryId != null && savingsCats.Contains(t.CategoryId.Value))
                .OrderByDescending(t => t.Date).Take(60)
                .Select(t => new SavingsTransferDto(t.Date, t.AmountCents, t.Account.Name, "Moved to savings")).ToListAsync(ct);
        var thisMonth = moves.Where(m => current.Contains(m.Date)).Sum(m => m.Cents);
        var planned = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == householdId && c.Type == CategoryType.SavingsTransfer && !c.IsHidden).SumAsync(c => c.MonthlyLimitCents, ct);

        return new SavingsSummaryDto(
            accounts.Sum(a => Math.Max(0, balances[a.Id])) + goals.Where(g => g.AccountId is null).Sum(g => g.ManualSavedCents ?? 0),
            accounts.Select(a => new SavingsAccountDto(a.Id, a.Name, a.Mask, balances[a.Id], a.PlaidAccountId is not null, pace[a.Id])).ToList(),
            goals.Select(g =>
            {
                var s = statuses.Single(x => x.Id == g.Id);
                return new SavingsGoalDto(g.Id, g.Name, g.TargetCents, g.AccountId, g.Account?.Name, g.ManualSavedCents, g.MonthlyCents, g.IsStarter,
                    s.SavedCents, s.RemainingCents, s.Percent, s.Reached, s.MonthlyCents, s.MonthlySource, s.MonthsToGo, s.ProjectedDate);
            }).ToList(),
            Order(household.SavingsPriority).Select(x => x.ToString()).ToList(),
            history, moves.Take(12).ToList(), thisMonth, planned);
    }

    /// <summary>The input for the budget generator: the priority order and what the starter fund still needs (milestone 8).</summary>
    public async Task<SavingsPlan?> PlanAsync(Guid householdId, CancellationToken ct)
    {
        var s = await SummaryAsync(householdId, ct);
        if (s.Goals.Count == 0) return null;
        var starter = s.Goals.FirstOrDefault(g => g.IsStarter);
        var larger = s.Goals.Where(g => !g.IsStarter && !g.Reached && g.MonthlyCentsSet is not null).Sum(g => g.MonthlyCentsSet!.Value);
        var anyLargerWithoutAmount = s.Goals.Any(g => !g.IsStarter && !g.Reached && g.MonthlyCentsSet is null);
        return new SavingsPlan(s.Priority.Select(Enum.Parse<PriorityStep>).ToList(), starter?.Name ?? "Rainy-day fund",
            starter?.RemainingCents ?? 0, starter?.SavedCents ?? 0, starter?.TargetCents ?? 0,
            anyLargerWithoutAmount ? null : larger);
    }

    /// <summary>The first goal is the rainy-day fund of about $1,000 (SPEC 4.5), on the first savings account if there is one.</summary>
    public async Task EnsureStarterAsync(Guid householdId, CancellationToken ct)
    {
        if (await db.SavingsGoals.AnyAsync(g => g.HouseholdId == householdId, ct))
            return;
        var account = await db.Accounts.Where(a => a.HouseholdId == householdId && a.Type == AccountType.Savings && a.IncludedInBudget)
            .OrderBy(a => a.CreatedUtc).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
        db.SavingsGoals.Add(new SavingsGoal
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Rainy-day fund", TargetCents = StarterTargetCents, AccountId = account,
            ManualSavedCents = account is null ? 0 : null, IsStarter = true, CreatedUtc = clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
    }

    public static IReadOnlyList<PriorityStep> Order(string stored)
    {
        var parsed = stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<PriorityStep>(s, out var p) ? p : (PriorityStep?)null).OfType<PriorityStep>().Distinct().ToList();
        return parsed.Count == 3 ? parsed : SavingsPlan.DefaultOrder;
    }

    private static GoalInput Input(SavingsGoal g) =>
        new(g.Id, g.Name, g.TargetCents, g.AccountId, g.ManualSavedCents, g.MonthlyCents, g.IsStarter, g.SortOrder);
}

/// <summary>SPEC section 9: <c>GET/PUT /savings/goals</c>, plus the priority order and a CSV-only account's balance.</summary>
public static class SavingsEndpoints
{
    public const long MaxCents = 100_000_000_00;

    public static void MapSavingsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/savings").RequireAuthorization();

        g.MapGet("", async (HttpContext ctx, SavingsService svc, CancellationToken ct) => await svc.SummaryAsync(ctx.User.HouseholdId(), ct));

        g.MapPost("/goals", async (GoalRequest req, HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            if (await ValidateAsync(req, householdId, db, ct) is { } problem) return problem;
            var order = await db.SavingsGoals.Where(x => x.HouseholdId == householdId).MaxAsync(x => (int?)x.SortOrder, ct) ?? -1;
            var goal = new SavingsGoal
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, Name = req.Name.Trim(), TargetCents = req.TargetCents, AccountId = req.AccountId,
                ManualSavedCents = req.AccountId is null ? req.ManualSavedCents ?? 0 : null, MonthlyCents = req.MonthlyCents,
                SortOrder = order + 1, CreatedUtc = clock.GetUtcNow().UtcDateTime,
            };
            db.SavingsGoals.Add(goal);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { goal.Id });
        });

        g.MapPut("/goals/{id:guid}", async (Guid id, GoalRequest req, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var goal = await db.SavingsGoals.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (goal is null) return Results.NotFound();
            if (await ValidateAsync(req, householdId, db, ct) is { } problem) return problem;
            (goal.Name, goal.TargetCents, goal.AccountId, goal.MonthlyCents) = (req.Name.Trim(), req.TargetCents, req.AccountId, req.MonthlyCents);
            goal.ManualSavedCents = req.AccountId is null ? req.ManualSavedCents ?? 0 : null;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // The starter fund can't be removed (it's the first goal, SPEC 4.5); others can.
        g.MapDelete("/goals/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var goal = await db.SavingsGoals.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == ctx.User.HouseholdId(), ct);
            if (goal is null) return Results.NotFound();
            if (goal.IsStarter)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["id"] = ["The rainy-day fund is your starter goal; change its target instead."] });
            db.SavingsGoals.Remove(goal);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPut("/priority", async (PriorityRequest req, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            if (req.Order.Count != 3 || req.Order.Distinct().Count() != 3 || req.Order.Any(p => !Enum.IsDefined(p)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["order"] = ["List the starter fund, debt and savings once each."] });
            var h = await db.Households.SingleAsync(x => x.Id == ctx.User.HouseholdId(), ct);
            h.SavingsPriority = string.Join(",", req.Order);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // A savings account imported from CSV has no balance from a bank: you enter it. A bank-linked one follows the bank.
        g.MapPut("/accounts/{id:guid}/balance", async (Guid id, BalanceRequest req, HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var a = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == ctx.User.HouseholdId() && x.Type == AccountType.Savings, ct);
            if (a is null) return Results.NotFound();
            if (a.PlaidAccountId is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["balanceCents"] = ["This balance comes from your bank."] });
            if (req.BalanceCents is < 0 or > MaxCents)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["balanceCents"] = ["Enter the balance in dollars."] });
            a.CurrentBalanceCents = req.BalanceCents;
            // True as of today: only what comes in or goes out after today moves it (DECISIONS.md 189).
            var zone = await db.Households.Where(h => h.Id == a.HouseholdId).Select(h => h.TimeZoneId).SingleAsync(ct);
            a.BalanceAsOf = HomeSummaryService.TodayIn(zone, clock);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static async Task<IResult?> ValidateAsync(GoalRequest r, Guid householdId, AppDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 120) errors["name"] = ["Give it a name (up to 120 characters)."];
        if (r.TargetCents is <= 0 or > MaxCents) errors["targetCents"] = ["Enter the target in dollars."];
        if (r.ManualSavedCents is < 0 or > MaxCents) errors["manualSavedCents"] = ["Enter what you've saved in dollars."];
        if (r.MonthlyCents is < 0 or > MaxCents) errors["monthlyCents"] = ["Enter the monthly amount in dollars."];
        if (r.AccountId is { } a && !await db.Accounts.AnyAsync(x => x.Id == a && x.HouseholdId == householdId && x.Type == AccountType.Savings, ct))
            errors["accountId"] = ["Pick one of your savings accounts."];
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }
}
