using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Api.Budget;

/// <param name="PartnerSharePercent">Split with your partner by default: her share in percent (0 = not split). Null leaves it as it is.</param>
public record CategoryRequest(string Name, CategoryType Type, long LimitCents, bool IsDiscretionary, int? PartnerSharePercent = null);

/// <param name="ApplyTo">"permanent" changes the usual limit from now on; "month" changes only the current budget month.</param>
public record UpdateCategoryRequest(string Name, CategoryType Type, long LimitCents, bool IsDiscretionary, bool IsHidden, string ApplyTo, int? PartnerSharePercent = null);

public record ReorderRequest(List<Guid> Ids);
public record IncomeRequest(long IncomeCents, long? PartnerCents = null);
public record GeneratorLimit(Guid CategoryId, long LimitCents);
public record ApplyBudgetRequest(long IncomeCents, List<GeneratorLimit> Limits, long PartnerCents = 0);
public record BudgetSettingsDto(SweepTarget SweepTarget, int SweepSavingsPercent);

public record GeneratorDto(
    int MonthsUsed, IReadOnlyList<BudgetMonthDtoLite> Months, long IncomeCents, string IncomeExplanation,
    long PartnerCents, string? PartnerName, AutoCategorizeResult Categorization,
    IReadOnlyList<ProposedLimit> Categories, long AllocatedCents, long UnallocatedCents, bool SpendsMoreThanIncome,
    int UncategorizedCount, bool HasCardDebt,
    // True when there are no categories yet: set them up from your transactions first (/api/budget/categories/discover).
    bool NeedsCategorySetup);

public record BudgetMonthDtoLite(DateOnly Start, DateOnly End);

public static class BudgetEndpoints
{
    public static void MapBudgetEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/budget").RequireAuthorization();

        g.MapGet("/summary", (HttpContext ctx, BudgetSummaryService svc, CancellationToken ct) => svc.GetAsync(ctx.User.HouseholdId(), ct));

        // ---- Categories (SPEC 4.3: add, edit, hide, reorder; a limit change can be this month only or permanent)

        g.MapPost("/categories", async (CategoryRequest req, HttpContext ctx, AppDbContext db, CategorizationService categorization, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            if (Validate(req.Name, req.Type, req.LimitCents) is { } problem) return problem;
            if (req.PartnerSharePercent is < 0 or >= 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["partnerSharePercent"] = ["Use 0 (not split) up to 99."] });
            var name = req.Name.Trim();
            if (await db.Categories.AnyAsync(c => c.HouseholdId == householdId && c.Name == name, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A category with that name already exists."] });
            var order = await db.Categories.Where(c => c.HouseholdId == householdId).MaxAsync(c => (int?)c.SortOrder, ct) ?? -1;
            var c = new Category
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, Name = name, Type = req.Type, MonthlyLimitCents = req.LimitCents,
                IsDiscretionary = req.IsDiscretionary, SortOrder = order + 1, PartnerSharePercent = req.PartnerSharePercent ?? 0,
            };
            db.Categories.Add(c);
            await db.SaveChangesAsync(ct);
            var filed = await categorization.RunAsync(householdId, ct); // file transactions that fit the new category
            return Results.Ok(new { c.Id, Categorization = filed });
        });

        g.MapPut("/categories/{id:guid}", async (Guid id, UpdateCategoryRequest req, HttpContext ctx, AppDbContext db, BudgetMonthService months, CategorizationService categorization, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var c = await db.Categories.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (c is null) return Results.NotFound();
            if (Validate(req.Name, req.Type, req.LimitCents) is { } problem) return problem;
            if (req.PartnerSharePercent is < 0 or >= 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["partnerSharePercent"] = ["Use 0 (not split) up to 99."] });
            var name = req.Name.Trim();
            if (await db.Categories.AnyAsync(x => x.HouseholdId == householdId && x.Name == name && x.Id != id, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A category with that name already exists."] });

            c.Name = name;
            c.Type = req.Type;
            c.IsDiscretionary = req.IsDiscretionary;
            c.IsHidden = req.IsHidden;
            if (req.PartnerSharePercent is { } p)
                c.PartnerSharePercent = p;

            var current = await months.EnsureCurrentAsync(householdId, ct);
            var mc = await db.BudgetMonthCategories.SingleOrDefaultAsync(x => x.BudgetMonthId == current.Id && x.CategoryId == id, ct);
            if (req.ApplyTo == "month")
            {
                if (mc is null)
                    db.BudgetMonthCategories.Add(mc = new BudgetMonthCategory { Id = Guid.NewGuid(), BudgetMonthId = current.Id, CategoryId = id });
                mc.LimitOverrideCents = req.LimitCents == c.MonthlyLimitCents ? null : req.LimitCents;
            }
            else if (req.ApplyTo == "permanent")
            {
                c.MonthlyLimitCents = req.LimitCents;
                if (mc is not null) mc.LimitOverrideCents = null; // the new usual limit applies this month too
            }
            else
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["applyTo"] = ["Use \"month\" or \"permanent\"."] });
            }
            await db.SaveChangesAsync(ct);
            await categorization.RunAsync(householdId, ct); // a renamed, retyped or hidden category changes what fits
            return Results.NoContent();
        });

        g.MapPost("/categories/order", async (ReorderRequest req, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var cats = await db.Categories.Where(c => c.HouseholdId == householdId).ToListAsync(ct);
            var position = 0;
            foreach (var id in req.Ids)
            {
                var c = cats.SingleOrDefault(x => x.Id == id);
                if (c is not null) c.SortOrder = position++;
            }
            foreach (var c in cats.Where(c => !req.Ids.Contains(c.Id)).OrderBy(c => c.SortOrder))
                c.SortOrder = position++;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Categories built from your own transactions (no premade list): what to add, and what you never use.
        g.MapGet("/categories/discover", async (HttpContext ctx, CategoryDiscoveryService discovery, CancellationToken ct) =>
            await discovery.DiscoverAsync(ctx.User.HouseholdId(), ct));

        g.MapPost("/categories/discover/apply", async (ApplyDiscoveryRequest req, HttpContext ctx, CategoryDiscoveryService discovery, CancellationToken ct) =>
            await discovery.ApplyAsync(ctx.User.HouseholdId(), req, ct));

        g.MapGet("/categories/hidden", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await db.Categories.Where(c => c.HouseholdId == ctx.User.HouseholdId() && c.IsHidden)
                .OrderBy(c => c.Name).Select(c => new { c.Id, c.Name, Type = c.Type.ToString(), c.MonthlyLimitCents }).ToListAsync(ct));

        // ---- Months: income, closing, sweeps

        g.MapPut("/months/current/income", async (IncomeRequest req, HttpContext ctx, AppDbContext db, BudgetMonthService months, CancellationToken ct) =>
        {
            if (req.IncomeCents < 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["incomeCents"] = ["Income can't be negative."] });
            var row = await months.EnsureCurrentAsync(ctx.User.HouseholdId(), ct);
            row.IncomeExpectedCents = req.IncomeCents;
            if (req.PartnerCents is { } p)
            {
                if (p < 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["partnerCents"] = ["Can't be negative."] });
                row.PartnerExpectedCents = p;
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // "Close now": freeze an ended month before its grace period is up.
        g.MapPost("/months/{start}/close", async (DateOnly start, HttpContext ctx, AppDbContext db, BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            await months.EnsureCurrentAsync(householdId, ct);
            var row = await db.BudgetMonths.SingleOrDefaultAsync(m => m.HouseholdId == householdId && m.StartDate == start, ct);
            if (row is null) return Results.NotFound();
            try
            {
                await months.CloseAsync(householdId, row, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            return Results.NoContent();
        });

        g.MapPost("/sweeps/{id:guid}/done", (Guid id, HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
            SetSweepDone(id, ctx.User.HouseholdId(), clock.GetUtcNow().UtcDateTime, db, ct));
        g.MapDelete("/sweeps/{id:guid}/done", (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            SetSweepDone(id, ctx.User.HouseholdId(), null, db, ct));

        // ---- Budget generator (SPEC 6.1)

        g.MapGet("/generator", async (HttpContext ctx, BudgetGeneratorService gen, CancellationToken ct) =>
            await gen.ProposeAsync(ctx.User.HouseholdId(), ct));

        g.MapPost("/generator/apply", async (ApplyBudgetRequest req, HttpContext ctx, BudgetGeneratorService gen, CancellationToken ct) =>
        {
            if (req.IncomeCents < 0 || req.Limits.Any(l => l.LimitCents < 0 || l.LimitCents > 10_000_000_00))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["limits"] = ["Amounts must be between $0 and $10,000,000."] });
            await gen.ApplyAsync(ctx.User.HouseholdId(), req, ct);
            return Results.NoContent();
        });

        // ---- Settings: where leftover money goes

        app.MapGet("/api/settings/budget", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var h = await db.Households.SingleAsync(x => x.Id == ctx.User.HouseholdId(), ct);
            return new BudgetSettingsDto(h.SweepTarget, h.SweepSavingsPercent);
        }).RequireAuthorization();

        app.MapPut("/api/settings/budget", async (BudgetSettingsDto req, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            if (!Enum.IsDefined(req.SweepTarget) || req.SweepSavingsPercent is < 0 or > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sweep"] = ["Choose Debt, Savings or Split, with a percent from 0 to 100."] });
            var h = await db.Households.SingleAsync(x => x.Id == ctx.User.HouseholdId(), ct);
            h.SweepTarget = req.SweepTarget;
            h.SweepSavingsPercent = req.SweepSavingsPercent;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static async Task<IResult> SetSweepDone(Guid id, Guid householdId, DateTime? when, AppDbContext db, CancellationToken ct)
    {
        var s = await db.SweepTasks.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
        if (s is null) return Results.NotFound();
        s.DoneUtc = when;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static IResult? Validate(string? name, CategoryType type, long limitCents)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) errors["name"] = ["Name is required (up to 100 characters)."];
        if (!Enum.IsDefined(type)) errors["type"] = ["Unknown category type."];
        if (limitCents is < 0 or > 10_000_000_00) errors["limitCents"] = ["Limit must be between $0 and $10,000,000."];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }
}

/// <summary>Runs <see cref="BudgetGenerator"/> on the household's last complete budget months and applies the result.</summary>
public class BudgetGeneratorService(AppDbContext db, BudgetMonthService months, CategorizationService categorization)
{
    public async Task<GeneratorDto> ProposeAsync(Guid householdId, CancellationToken ct)
    {
        // No premade categories: a household without any sets them up from its own transactions first.
        var needsSetup = !await db.Categories.AnyAsync(c => c.HouseholdId == householdId && !c.IsHidden, ct);

        // File everything that fits first: transactions synced before the categories existed (or before a
        // category was added) would otherwise count as $0 in every category.
        var filed = await categorization.RunAsync(householdId, ct);

        var current = await months.EnsureCurrentAsync(householdId, ct);
        var (_, _, calendar) = await months.ContextAsync(householdId, ct);

        // The last complete budget months that have any transactions (up to six).
        var firstDate = await db.Transactions.Where(t => t.HouseholdId == householdId).MinAsync(t => (DateOnly?)t.Date, ct);
        var history = new List<BudgetMonthRange>();
        if (firstDate is { } first)
        {
            var label = calendar.MonthContaining(current.StartDate).Label;
            for (var i = 0; i < BudgetGenerator.MaxMonths; i++)
            {
                label = label.AddMonths(-1);
                var m = calendar.MonthOf(label.Year, label.Month);
                if (m.End < first) break;
                history.Add(m);
            }
        }

        var from = history.Count > 0 ? history.Min(m => m.Start) : current.StartDate;
        var transactions = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Date >= from && t.Date < current.StartDate && t.Account.IncludedInBudget)
            .Select(t => new BudgetTransaction(t.CategoryId, t.Date, t.AmountCents, t.IsTransfer, t.Pending, t.SplitFactor, t.Kind,
                t.Account.Type == AccountType.CreditCard))
            .ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == householdId && !c.IsHidden)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new GeneratorCategory(c.Id, c.Name, c.Type, c.IsDiscretionary, c.MonthlyLimitCents)).ToListAsync(ct);
        var hasCardDebt = await db.Accounts.AnyAsync(a => a.HouseholdId == householdId && a.IncludedInBudget
            && a.Type == AccountType.CreditCard && a.CurrentBalanceCents < 0, ct);

        var p = BudgetGenerator.Propose(history, categories, transactions, hasCardDebt);
        var partnerName = await db.Households.Where(h => h.Id == householdId).Select(h => h.PartnerName).SingleAsync(ct);
        return new GeneratorDto(p.MonthsUsed, history.OrderBy(m => m.Start).Select(m => new BudgetMonthDtoLite(m.Start, m.End)).ToList(),
            p.IncomeCents, p.IncomeExplanation, p.PartnerCents, partnerName, filed, p.Categories, p.AllocatedCents, p.UnallocatedCents, p.SpendsMoreThanIncome,
            p.UncategorizedCount, hasCardDebt, needsSetup);
    }

    /// <summary>Confirming the proposal sets the usual limits and this month's expected income.</summary>
    public async Task ApplyAsync(Guid householdId, ApplyBudgetRequest req, CancellationToken ct)
    {
        var current = await months.EnsureCurrentAsync(householdId, ct);
        var cats = await db.Categories.Where(c => c.HouseholdId == householdId).ToDictionaryAsync(c => c.Id, ct);
        var overrides = await db.BudgetMonthCategories.Where(x => x.BudgetMonthId == current.Id).ToListAsync(ct);
        foreach (var l in req.Limits)
        {
            if (!cats.TryGetValue(l.CategoryId, out var c)) continue;
            c.MonthlyLimitCents = l.LimitCents;
            foreach (var o in overrides.Where(o => o.CategoryId == c.Id)) o.LimitOverrideCents = null;
        }
        current.IncomeExpectedCents = req.IncomeCents;
        current.PartnerExpectedCents = req.PartnerCents;
        await db.SaveChangesAsync(ct);
    }
}
