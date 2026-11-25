using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Pay;

namespace SmoothBudget.Api.Budget;

/// <summary>One check's part of a planned expense.</summary>
public record SetAsidePartDto(DateOnly PayDate, long Cents);

/// <param name="SetAsideCents">What the checks paid so far were to set aside, less what's been spent on it.</param>
/// <param name="SpentCents">What's been filed under it so far.</param>
public record PlannedExpenseDto(Guid Id, string Name, long AmountCents, DateOnly DueOn, Guid CategoryId, long SetAsideCents, long SpentCents,
    IReadOnlyList<SetAsidePartDto> Checks, bool Paid);

public record PlannedExpenseRequest(string Name, long AmountCents, DateOnly DueOn);

/// <summary>A planned expense with its checks' parts and what's been spent on it.</summary>
public sealed record LoadedPlannedExpense(PlannedExpense Row, PlannedExpenseInput Input, IReadOnlyList<SetAsidePart> Parts, IReadOnlyList<(DateOnly Date, long Cents)> Spending)
{
    public long SpentCents => Spending.Sum(s => s.Cents);
    public long SpentBefore(DateOnly day) => Spending.Where(s => s.Date < day).Sum(s => s.Cents);
    public bool Paid => SpentCents >= Row.AmountCents;
}

/// <summary>
/// Planned expenses (DECISIONS.md 327): a big one-time cost you set money aside for over the checks before it's due. Each
/// has its own bill line; the budget plans what each month sets aside on it, the pay period tasks keep that in checking,
/// and in the month it's paid, what earlier months set aside counts as money in.
/// </summary>
public class PlannedExpenseService(AppDbContext db, BudgetMonthService months, Payday.PaydayService payday, TimeProvider clock)
{
    public const int MaxName = 60;

    /// <summary>The payday of the check you're on: the last one on or before today.</summary>
    public static DateOnly CurrentPay(DateOnly today, PayRules rules) =>
        PayCalendar.From(today.AddDays(-45), rules).TakeWhile(p => p.PayDate <= today).Select(p => p.PayDate).DefaultIfEmpty(today).Last();

    /// <summary>Every planned expense, with each check's part and what's been filed under it.</summary>
    public static async Task<List<LoadedPlannedExpense>> LoadAsync(AppDbContext db, Guid householdId, PayRules rules, CancellationToken ct)
    {
        var rows = await db.PlannedExpenses.AsNoTracking().Where(x => x.HouseholdId == householdId).OrderBy(x => x.DueOn).ToListAsync(ct);
        if (rows.Count == 0) return [];
        var categoryIds = rows.Select(r => r.CategoryId).ToList();
        // What's been paid on it: purchases less refunds filed under its line, not transfers.
        var spending = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.CategoryId != null && categoryIds.Contains(t.CategoryId.Value) && !t.IsTransfer)
            .Select(t => new { CategoryId = t.CategoryId!.Value, t.Date, t.AmountCents }).ToListAsync(ct);
        return rows.Select(r =>
        {
            var input = new PlannedExpenseInput(r.Id, r.Name, r.AmountCents, r.DueOn, r.FirstPay);
            var parts = PlannedExpenses.Parts(r.AmountCents, r.DueOn, PayCalendar.From(r.FirstPay, rules).TakeWhile(p => p.PayDate <= r.DueOn).Select(p => p.PayDate));
            return new LoadedPlannedExpense(r, input, parts, spending.Where(s => s.CategoryId == r.CategoryId).Select(s => (s.Date, s.AmountCents)).ToList());
        }).ToList();
    }

    private async Task<PayRules> RulesAsync(Guid householdId, CancellationToken ct) => Payday.PaydayService.RulesOf(await payday.ProfileAsync(householdId, ct));

    /// <summary>What this budget month plans for each planned expense: its line, why, and money set aside earlier that it spends.</summary>
    public async Task<(Dictionary<Guid, long> Limits, Dictionary<Guid, string> Why, long SetAsideIn)> MonthAsync(Guid householdId, BudgetMonth month, CancellationToken ct)
    {
        var (_, _, calendar) = await months.ContextAsync(householdId, ct);
        var loaded = await LoadAsync(db, householdId, await RulesAsync(householdId, ct), ct);
        var range = new BudgetMonthRange(month.StartDate, month.EndDate, new DateOnly(month.StartDate.Year, month.StartDate.Month, 1));
        var (limits, why) = (new Dictionary<Guid, long>(), new Dictionary<Guid, string>());
        long setAsideIn = 0;
        foreach (var e in loaded)
        {
            var thisMonth = e.Spending.Where(s => range.Contains(s.Date)).Sum(s => s.Cents);
            var plan = PlannedExpenses.Month(e.Input, e.Parts, range, calendar.MonthContaining(e.Row.DueOn).Start, e.SpentBefore(range.Start), thisMonth);
            limits[e.Row.CategoryId] = plan.LimitCents;
            why[e.Row.CategoryId] = plan.Explanation;
            setAsideIn += plan.FromEarlierCents;
        }
        return (limits, why, setAsideIn);
    }

    /// <summary>
    /// Puts this month's plan for each planned expense on its line, and money set aside earlier on the month, whether or not
    /// the automatic budget is on: it's your plan, not the app's guess.
    /// </summary>
    public async Task SyncAsync(Guid householdId, CancellationToken ct)
    {
        var current = await months.EnsureCurrentAsync(householdId, ct);
        var (limits, _, setAsideIn) = await MonthAsync(householdId, current, ct);
        if (limits.Count > 0)
        {
            var ids = limits.Keys.ToList();
            foreach (var c in await db.Categories.Where(c => ids.Contains(c.Id)).ToListAsync(ct))
                (c.MonthlyLimitCents, c.LimitSetByUser) = (limits[c.Id], true);
        }
        var row = await db.BudgetMonths.SingleAsync(m => m.Id == current.Id, ct);
        row.SetAsideInCents = setAsideIn;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<PlannedExpenseDto>> ListAsync(Guid householdId, CancellationToken ct)
    {
        var (_, today, _) = await months.ContextAsync(householdId, ct);
        var loaded = await LoadAsync(db, householdId, await RulesAsync(householdId, ct), ct);
        return loaded.Select(e => new PlannedExpenseDto(e.Row.Id, e.Row.Name, e.Row.AmountCents, e.Row.DueOn, e.Row.CategoryId,
            Math.Max(0, e.Parts.Where(p => p.PayDate <= today).Sum(p => p.Cents) - e.SpentCents), e.SpentCents,
            e.Parts.Select(p => new SetAsidePartDto(p.PayDate, p.Cents)).ToList(), e.Paid)).ToList();
    }

    private async Task<IResult?> ValidateAsync(Guid householdId, PlannedExpenseRequest req, Guid? categoryId, DateOnly today, CancellationToken ct)
    {
        var name = req.Name?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (name.Length is 0 or > MaxName) errors["name"] = [$"Give it a name (up to {MaxName} characters)."];
        else if (await db.Categories.AnyAsync(c => c.HouseholdId == householdId && c.Id != categoryId && c.Name.ToLower() == name.ToLower(), ct))
            errors["name"] = ["You already have a category with that name. Pick another."];
        if (req.AmountCents is <= 0 or > Accounts.AccountManagement.MaxCents) errors["amountCents"] = ["Enter what it costs in dollars, like 1200.00."];
        if (req.DueOn < today) errors["dueOn"] = ["Pick a day from today on."];
        else if (req.DueOn > today.AddYears(3)) errors["dueOn"] = ["Pick a day within 3 years."];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    public async Task<IResult> AddAsync(Guid householdId, PlannedExpenseRequest req, CancellationToken ct)
    {
        var (_, today, _) = await months.ContextAsync(householdId, ct);
        if (await ValidateAsync(householdId, req, null, today, ct) is { } problem) return problem;
        var name = req.Name.Trim();
        var order = await db.Categories.Where(c => c.HouseholdId == householdId).MaxAsync(c => (int?)c.SortOrder, ct) ?? -1;
        // Its own bill line: what each month sets aside, and in the month it's paid, what it costs. File the charge there.
        var category = new Category
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, Name = name, Type = CategoryType.FixedBill, LimitSetByUser = true,
            Priority = CategoryPriority.High, SortOrder = order + 1,
        };
        var row = new PlannedExpense
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, Name = name, AmountCents = req.AmountCents, DueOn = req.DueOn,
            FirstPay = CurrentPay(today, await RulesAsync(householdId, ct)), CategoryId = category.Id, CreatedUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.Categories.Add(category);
        db.PlannedExpenses.Add(row);
        await db.SaveChangesAsync(ct);
        await SyncAsync(householdId, ct);
        return Results.Ok(new { row.Id });
    }

    public async Task<IResult> UpdateAsync(Guid householdId, Guid id, PlannedExpenseRequest req, CancellationToken ct)
    {
        var row = await db.PlannedExpenses.Include(x => x.Category).SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
        if (row is null) return Results.NotFound();
        var (_, today, _) = await months.ContextAsync(householdId, ct);
        // A due date already gone by can stay as it is; a new one has to be from today on.
        if (await ValidateAsync(householdId, req with { DueOn = req.DueOn == row.DueOn && row.DueOn < today ? today : req.DueOn }, row.CategoryId, today, ct) is { } problem)
            return problem;
        (row.Name, row.AmountCents, row.DueOn) = (req.Name.Trim(), req.AmountCents, req.DueOn);
        if (row.Category is { } c) c.Name = row.Name;
        await db.SaveChangesAsync(ct);
        await SyncAsync(householdId, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Removing one stops setting money aside for it. Its line stays as an ordinary bill line when something's filed under it;
    /// with nothing filed, it's archived.
    /// </summary>
    public async Task<IResult> RemoveAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        var row = await db.PlannedExpenses.Include(x => x.Category).SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
        if (row is null) return Results.NotFound();
        if (row.Category is { } c)
        {
            c.LimitSetByUser = false;
            c.MonthlyLimitCents = 0;
            if (!await db.Transactions.AnyAsync(t => t.CategoryId == c.Id, ct)) c.IsHidden = true;
        }
        db.PlannedExpenses.Remove(row);
        await db.SaveChangesAsync(ct);
        await SyncAsync(householdId, ct);
        return Results.NoContent();
    }
}

public static class PlannedExpenseEndpoints
{
    public static void MapPlannedExpenseEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/planned").RequireAuthorization();
        g.MapGet("", (HttpContext ctx, PlannedExpenseService s, CancellationToken ct) => s.ListAsync(ctx.User.HouseholdId(), ct));
        g.MapPost("", (PlannedExpenseRequest req, HttpContext ctx, PlannedExpenseService s, CancellationToken ct) => s.AddAsync(ctx.User.HouseholdId(), req, ct));
        g.MapPut("/{id:guid}", (Guid id, PlannedExpenseRequest req, HttpContext ctx, PlannedExpenseService s, CancellationToken ct) => s.UpdateAsync(ctx.User.HouseholdId(), id, req, ct));
        g.MapDelete("/{id:guid}", (Guid id, HttpContext ctx, PlannedExpenseService s, CancellationToken ct) => s.RemoveAsync(ctx.User.HouseholdId(), id, ct));
    }
}
