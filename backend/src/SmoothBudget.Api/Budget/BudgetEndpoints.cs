using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Api.Budget;

/// <param name="Priority">For spending categories (DECISIONS.md 182); left out, important.</param>
public record CategoryRequest(string Name, CategoryType Type, long LimitCents, CategoryPriority? Priority = null);

/// <param name="ApplyTo">"permanent" changes the usual limit from now on; "month" changes only the current budget month.</param>
/// <param name="Automatic">Hand the limit back to the automatic budget (it stops being yours).</param>
/// <param name="Priority">Left out, it stays as it is.</param>
public record UpdateCategoryRequest(string Name, CategoryType Type, long LimitCents, CategoryPriority? Priority, bool IsHidden, string ApplyTo, bool Automatic = false);

public record ReorderRequest(List<Guid> Ids);
/// <param name="Automatic">Go back to income from the Payday forecast.</param>
public record IncomeRequest(long IncomeCents, long? PartnerCents = null, bool Automatic = false);
public record GeneratorLimit(Guid CategoryId, long LimitCents);
public record ApplyBudgetRequest(long IncomeCents, List<GeneratorLimit> Limits, long PartnerCents = 0);
/// <param name="AutoBudget">The automatic budget on or off (null leaves it as it is).</param>
public record BudgetSettingsDto(SweepTarget SweepTarget, int SweepSavingsPercent, bool? AutoBudget = null);

public record GeneratorDto(
    int MonthsUsed, IReadOnlyList<BudgetMonthDtoLite> Months, long IncomeCents, string IncomeExplanation,
    long PartnerCents, string? PartnerName, AutoCategorizeResult Categorization,
    IReadOnlyList<ProposedLimit> Categories, long AllocatedCents, long UnallocatedCents, bool SpendsMoreThanIncome,
    int UncategorizedCount, bool HasCardDebt,
    // True when there are no categories yet: set them up from your transactions first (/api/budget/categories/discover).
    bool NeedsCategorySetup,
    // Partner money is her fixed contributions (milestone 7) rather than a median of what she sent.
    bool PartnerFromArrangements = false,
    // The automatic budget: this is what it applies (nothing to confirm), and what the month still needs after squeezing.
    bool Automatic = false, long ShortCents = 0);

public record BudgetMonthDtoLite(DateOnly Start, DateOnly End);

/// <param name="Month">The budget month's label (the 1st of its calendar month).</param>
/// <param name="Current">This month, still going.</param>
public record CategoryMonthDto(DateOnly Month, DateOnly Start, DateOnly End, long SpentCents, bool Current);
/// <param name="UnsqueezedCents">Where this month's limit would be if the automatic budget hadn't squeezed it to fit the
/// month's money (DECISIONS.md 223); null when it wasn't squeezed.</param>
public record CategoryHistoryDto(IReadOnlyList<CategoryMonthDto> Months, long? UnsqueezedCents = null);

public static class BudgetEndpoints
{
    public static void MapBudgetEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/budget").RequireAuthorization();

        g.MapGet("/summary", async (HttpContext ctx, BudgetSummaryService svc, AutoBudgetService auto, CancellationToken ct) =>
        {
            await auto.EnsureFreshAsync(ctx.User.HouseholdId(), ct);
            return await svc.GetAsync(ctx.User.HouseholdId(), ct);
        });

        // ---- Categories (SPEC 4.3: add, edit, hide, reorder; a limit change can be this month only or permanent)

        g.MapPost("/categories", async (CategoryRequest req, HttpContext ctx, AppDbContext db, CategorizationService categorization, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            if (Validate(req.Name, req.Type, req.LimitCents) is { } problem) return problem;
            var name = req.Name.Trim();
            if (await db.Categories.AnyAsync(c => c.HouseholdId == householdId && c.Name == name, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A category with that name already exists."] });
            var order = await db.Categories.Where(c => c.HouseholdId == householdId).MaxAsync(c => (int?)c.SortOrder, ct) ?? -1;
            var c = new Category
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, Name = name, Type = req.Type, MonthlyLimitCents = req.LimitCents,
                Priority = req.Priority ?? CategoryPriority.Medium, SortOrder = order + 1,
                // A limit you typed is yours; one added with no amount (from Review) is the automatic budget's to set (DECISIONS.md 158).
                LimitSetByUser = req.LimitCents > 0,
            };
            db.Categories.Add(c);
            await db.SaveChangesAsync(ct);
            var filed = await categorization.RunAsync(householdId, ct); // file transactions that fit the new category
            return Results.Ok(new { c.Id, Categorization = filed });
        });

        g.MapPut("/categories/{id:guid}", async (Guid id, UpdateCategoryRequest req, HttpContext ctx, AppDbContext db, BudgetMonthService months, CategorizationService categorization,
            Bills.RecurringService recurring, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var c = await db.Categories.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (c is null) return Results.NotFound();
            if (Validate(req.Name, req.Type, req.LimitCents) is { } problem) return problem;
            var name = req.Name.Trim();
            if (await db.Categories.AnyAsync(x => x.HouseholdId == householdId && x.Name == name && x.Id != id, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A category with that name already exists."] });

            var renamedBill = c.Type == CategoryType.FixedBill && req.Type == CategoryType.FixedBill && c.Name != name;
            c.Name = name;
            c.Type = req.Type;
            if (req.Priority is { } priority) c.Priority = priority;
            c.IsHidden = req.IsHidden;

            var current = await months.EnsureCurrentAsync(householdId, ct);
            var mc = await db.BudgetMonthCategories.SingleOrDefaultAsync(x => x.BudgetMonthId == current.Id && x.CategoryId == id, ct);
            if (req.Automatic)
            {
                c.LimitSetByUser = false; // the automatic budget sets it again on the next refresh
                if (mc is not null) mc.LimitOverrideCents = null;
            }
            else if (req.ApplyTo == "month")
            {
                if (mc is null)
                    db.BudgetMonthCategories.Add(mc = new BudgetMonthCategory { Id = Guid.NewGuid(), BudgetMonthId = current.Id, CategoryId = id });
                mc.LimitOverrideCents = req.LimitCents == c.MonthlyLimitCents ? null : req.LimitCents;
            }
            else if (req.ApplyTo == "permanent")
            {
                if (c.MonthlyLimitCents != req.LimitCents) c.LimitSetByUser = true; // yours from now on: the automatic budget leaves it alone
                c.MonthlyLimitCents = req.LimitCents;
                if (mc is not null) mc.LimitOverrideCents = null; // the new usual limit applies this month too
            }
            else
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["applyTo"] = ["Use \"month\" or \"permanent\"."] });
            }
            await db.SaveChangesAsync(ct);
            if (renamedBill) await recurring.NameBillAfterLineAsync(householdId, id, name, ct); // a bill and its line share a name (DECISIONS.md 279)
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
        // Combine categories into one you have or a new one (DECISIONS.md 144).
        g.MapPost("/categories/merge", async (MergeCategoriesRequest req, HttpContext ctx, AppDbContext db, CategorizationService categorization, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var (result, errors) = await CategoryMerge.RunAsync(db, householdId, req, ct);
            if (errors is not null) return Results.ValidationProblem(errors);
            await categorization.RunAsync(householdId, ct); // file anything waiting that fits the merged category
            return Results.Ok(result);
        });

        // Delete a category (DECISIONS.md 169): its transactions move to another one (?moveToId=), or go to Review for a new one.
        g.MapDelete("/categories/{id:guid}", async (Guid id, Guid? moveToId, HttpContext ctx, AppDbContext db, CategorizationService categorization,
            AutoBudgetService auto, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var name = await db.Categories.Where(c => c.Id == id && c.HouseholdId == householdId).Select(c => c.Name).SingleOrDefaultAsync(ct);
            if (name is null) return Results.NotFound();
            DeleteCategoryResult result;
            if (moveToId is { } to)
            {
                if (to == id) return Results.ValidationProblem(new Dictionary<string, string[]> { ["moveToId"] = ["Pick a different category to move them to."] });
                var (merged, errors) = await CategoryMerge.RunAsync(db, householdId, new MergeCategoriesRequest([id], IntoId: to), ct, addLimits: false);
                if (errors is not null) return Results.ValidationProblem(errors);
                result = new DeleteCategoryResult(name, merged!.Name, merged.MovedTransactions, 0);
            }
            else
            {
                var (toReview, errors) = await CategoryMerge.DeleteAsync(db, householdId, id, ct);
                if (errors is not null) return Results.ValidationProblem(errors);
                result = new DeleteCategoryResult(name, null, 0, toReview!.Value);
            }
            await categorization.RunAsync(householdId, ct); // anything waiting that fits elsewhere gets the app's guess
            await auto.RefreshAsync(householdId, ct);
            return Results.Ok(result);
        });

        // Budget → Categories → "Refresh categories from bank" (DECISIONS.md 176): re-download what the bank says about every
        // transaction, clear every category (yours too), and file them all again from the bank and the app's guesses.
        g.MapPost("/categories/reset-from-bank", async (HttpContext ctx, AppDbContext db, CategorizationService categorization, AutoBudgetService auto,
            Plaid.PlaidOptions plaid, Plaid.PlaidSyncService sync, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var cleared = await categorization.ResetAsync(householdId, ct);
            var banks = new List<string>();
            if (plaid.IsConfigured)
            {
                var items = await db.PlaidItems.Where(i => i.HouseholdId == householdId && i.Status == PlaidItemStatus.Active).ToListAsync(ct);
                foreach (var item in items)
                {
                    item.Cursor = null; // everything again, so the bank's labels are current
                    await db.SaveChangesAsync(ct);
                    try
                    {
                        var r = await sync.SyncAsync(item.Id, ct);
                        if (!r.Ok) banks.Add($"{r.InstitutionName ?? "A bank"} couldn't be reached ({r.Error}), so its last labels were used.");
                    }
                    catch (Exception)
                    {
                        banks.Add($"{item.InstitutionName ?? "A bank"} couldn't be reached, so its last labels were used.");
                    }
                }
            }
            var result = await categorization.RunAsync(householdId, ct);
            await auto.RefreshAsync(householdId, ct);
            return Results.Ok(new ResetCategoriesResult(cleared, result.Filed, result.Guesses, result.StillUncategorized, banks));
        });

        // A category's spending month by month (DECISIONS.md 222): the last six budget months and this one so far, counted
        // the way the budget counts them. Months from before your transactions start are left out rather than shown as $0.
        g.MapGet("/categories/{id:guid}/history", async (Guid id, HttpContext ctx, AppDbContext db, BudgetMonthService months, AutoBudgetService auto, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var cats = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == householdId)
                .Select(c => new BudgetCategory(c.Id, c.Type, c.MonthlyLimitCents)).ToListAsync(ct);
            if (!cats.Any(c => c.Id == id)) return Results.NotFound();
            var (_, today, calendar) = await months.ContextAsync(householdId, ct);
            var current = calendar.MonthContaining(today);
            var ranges = Enumerable.Range(0, 7).Select(i => current.Label.AddMonths(i - 6)).Select(l => calendar.MonthOf(l.Year, l.Month)).ToList();
            var firstSeen = await db.Transactions.Where(t => t.HouseholdId == householdId).MinAsync(t => (DateOnly?)t.Date, ct);
            var txns = await BudgetMonthService.TransactionsAsync(db, householdId, ranges[0].Start, current.End, ct);
            // Squeezed by the automatic budget (DECISIONS.md 223): where it would be with enough money, next to where it is.
            long? unsqueezed = null;
            if (await db.Households.Where(h => h.Id == householdId).Select(h => h.AutoBudget).SingleAsync(ct))
            {
                var (fitted, _, locked) = await auto.PlanAsync(householdId, ct);
                var before = fitted.Unsqueezed?.Categories.FirstOrDefault(l => l.CategoryId == id)?.ProposedCents;
                var after = fitted.Proposal.Categories.FirstOrDefault(l => l.CategoryId == id)?.ProposedCents;
                if (!locked.Contains(id) && before > after) unsqueezed = before;
            }
            return Results.Ok(new CategoryHistoryDto(ranges.Where(r => firstSeen is { } first && r.End >= first).Select(r =>
                new CategoryMonthDto(r.Label, r.Start, r.End,
                    CategoryBreakdownCalculator.Calculate(r, cats, txns.Where(t => t.Date >= r.Start && t.Date <= r.End)).Single(s => s.CategoryId == id).SpentCents,
                    r == current)).ToList(), unsqueezed));
        });

        g.MapGet("/categories/discover",async (HttpContext ctx, CategoryDiscoveryService discovery, CancellationToken ct) =>
            await discovery.DiscoverAsync(ctx.User.HouseholdId(), ct));

        g.MapPost("/categories/discover/apply", async (ApplyDiscoveryRequest req, HttpContext ctx, CategoryDiscoveryService discovery, CancellationToken ct) =>
            await discovery.ApplyAsync(ctx.User.HouseholdId(), req, ct));

        // Archive (DECISIONS.md 180): archived is what "hidden" was. It can come back, and unused ones are suggested.
        g.MapGet("/categories/hidden", async (HttpContext ctx, CategoryArchiveService archive, CancellationToken ct) =>
            await archive.ArchivedAsync(ctx.User.HouseholdId(), ct));

        g.MapGet("/categories/unused", async (HttpContext ctx, CategoryArchiveService archive, CancellationToken ct) =>
            await archive.UnusedAsync(ctx.User.HouseholdId(), ct));

        g.MapPost("/categories/{id:guid}/archive", async (Guid id, HttpContext ctx, CategoryArchiveService archive, CancellationToken ct) =>
            await archive.ArchiveAsync(ctx.User.HouseholdId(), id, ct) is { } r ? Results.Ok(r) : Results.NotFound());

        g.MapPost("/categories/{id:guid}/restore", async (Guid id, HttpContext ctx, CategoryArchiveService archive, CancellationToken ct) =>
            await archive.RestoreAsync(ctx.User.HouseholdId(), id, ct) is { } name ? Results.Ok(new { Name = name }) : Results.NotFound());

        g.MapPost("/categories/{id:guid}/keep", async (Guid id, HttpContext ctx, CategoryArchiveService archive, CancellationToken ct) =>
            await archive.KeepAsync(ctx.User.HouseholdId(), id, ct) ? Results.NoContent() : Results.NotFound());

        // ---- Months: income, closing, sweeps

        g.MapPut("/months/current/income", async (IncomeRequest req, HttpContext ctx, AppDbContext db, BudgetMonthService months, CancellationToken ct) =>
        {
            if (req.IncomeCents < 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["incomeCents"] = ["Income can't be negative."] });
            var row = await months.EnsureCurrentAsync(ctx.User.HouseholdId(), ct);
            row.IncomeExpectedCents = req.IncomeCents;
            row.IncomeSetByUser = !req.Automatic;
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

        // With the automatic budget on, this is how this month's budget is built (what it applies); off, a proposal to apply.
        g.MapGet("/generator", async (HttpContext ctx, BudgetGeneratorService gen, AutoBudgetService auto, AppDbContext db, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var on = await db.Households.Where(h => h.Id == householdId).Select(h => h.AutoBudget).SingleAsync(ct);
            if (!on || !await db.Categories.AnyAsync(c => c.HouseholdId == householdId && !c.IsHidden, ct))
                return await gen.ProposeAsync(householdId, ct);
            var (fitted, dto, _) = await auto.PlanAsync(householdId, ct);
            return dto with { Automatic = true, ShortCents = fitted.ShortCents };
        });

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
            return new BudgetSettingsDto(h.SweepTarget, h.SweepSavingsPercent, h.AutoBudget);
        }).RequireAuthorization();

        app.MapPut("/api/settings/budget", async (BudgetSettingsDto req, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            if (!Enum.IsDefined(req.SweepTarget) || req.SweepSavingsPercent is < 0 or > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sweep"] = ["Choose Debt, Savings or Split, with a percent from 0 to 100."] });
            var h = await db.Households.SingleAsync(x => x.Id == ctx.User.HouseholdId(), ct);
            h.SweepTarget = req.SweepTarget;
            h.SweepSavingsPercent = req.SweepSavingsPercent;
            if (req.AutoBudget is { } auto) h.AutoBudget = auto;
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
public class BudgetGeneratorService(AppDbContext db, BudgetMonthService months, CategorizationService categorization, Debt.DebtService debts, Bills.RecurringService recurring, Savings.SavingsService savings)
{
    public async Task<GeneratorDto> ProposeAsync(Guid householdId, CancellationToken ct) => (await BuildAsync(householdId, null, true, ct)).Dto;

    /// <summary>
    /// Builds the proposal. The automatic budget passes its overrides (Payday income, your own limits) and skips re-filing
    /// transactions, which every import and sync already does.
    /// </summary>
    public async Task<(BudgetProposal Proposal, GeneratorDto Dto, bool SavingsFromGoals)> BuildAsync(Guid householdId, BudgetOverrides? overrides, bool runCategorization, CancellationToken ct)
    {
        // No premade categories: a household without any sets them up from its own transactions first.
        var needsSetup = !await db.Categories.AnyAsync(c => c.HouseholdId == householdId && !c.IsHidden, ct);

        // File everything that fits first: transactions synced before the categories existed (or before a
        // category was added) would otherwise count as $0 in every category.
        var filed = runCategorization ? await categorization.RunAsync(householdId, ct) : new AutoCategorizeResult(0, 0, 0, 0);

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
        var transactions = await BudgetMonthService.TransactionsAsync(db, householdId, from, current.StartDate.AddDays(-1), ct);
        var categories = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == householdId && !c.IsHidden)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new GeneratorCategory(c.Id, c.Name, c.Type, c.Priority, c.MonthlyLimitCents)).ToListAsync(ct);
        var hasCardDebt = await db.Accounts.AnyAsync(a => a.HouseholdId == householdId && a.IncludedInBudget && a.ClosedOn == null
            && a.Type == AccountType.CreditCard && a.CurrentBalanceCents < 0, ct);

        var cardDebts = await debts.CardInputsAsync(householdId, ct); // milestone 5: minimums + extra to the highest APR
        var bills = await recurring.CurrentBillsAsync(householdId, ct); // milestone 6: bills at their current price
        var amounts = await db.PartnerArrangements.Where(a => a.HouseholdId == householdId).Select(a => a.AmountCents).ToListAsync(ct);
        long? contributions = amounts.Count > 0 ? amounts.Sum() : null; // none set up: the median of what she sent
        var plan = await savings.PlanAsync(householdId, ct); // what's left, shared by your savings/debt split (DECISIONS.md 321)
        var p = BudgetGenerator.Propose(history, categories, transactions, hasCardDebt, cardDebts, bills, contributions, plan, overrides);
        var partnerName = await Shared.SharedService.TrackedNamesAsync(db, householdId, ct);
        return (p, new GeneratorDto(p.MonthsUsed, history.OrderBy(m => m.Start).Select(m => new BudgetMonthDtoLite(m.Start, m.End)).ToList(),
            p.IncomeCents, p.IncomeExplanation, p.PartnerCents, partnerName, filed, p.Categories, p.AllocatedCents, p.UnallocatedCents, p.SpendsMoreThanIncome,
            p.UncategorizedCount, hasCardDebt, needsSetup, contributions is not null), plan is not null);
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
