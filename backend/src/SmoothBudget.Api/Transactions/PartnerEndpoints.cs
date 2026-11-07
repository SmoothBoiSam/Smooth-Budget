using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Transactions;

public record PartnerSettingsDto(string? PartnerName);
public record PartnerNameSuggestion(string Name, int Count, long TotalCents);

/// <summary>
/// The partner's name as it appears on her transfers (SPEC Appendix D: stored in Settings, never hard-coded).
/// Money from that name is partner money, kept apart from the owner's own income; see DECISIONS.md 97-100.
/// </summary>
public static class PartnerEndpoints
{
    public static void MapPartnerEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings/partner").RequireAuthorization();

        g.MapGet("", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            new PartnerSettingsDto(await db.Households.Where(h => h.Id == ctx.User.HouseholdId()).Select(h => h.PartnerName).SingleAsync(ct)));

        // Saving re-tags every incoming transfer: from that name it's partner money, otherwise your income.
        g.MapPut("", async (PartnerSettingsDto req, HttpContext ctx, AppDbContext db, CategorizationService categorization, TimeProvider clock, CancellationToken ct) =>
        {
            var name = string.IsNullOrWhiteSpace(req.PartnerName) ? null : req.PartnerName.Trim();
            if (name is { Length: > 100 or < 2 })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["partnerName"] = ["Use 2 to 100 characters."] });
            var h = await db.Households.SingleAsync(x => x.Id == ctx.User.HouseholdId(), ct);
            h.PartnerName = name;
            // Labeling starts today, today included: money from her before today is her contribution, as it always was (DECISIONS.md 92, 100).
            if (name is not null)
                h.PartnerLabelsStartAfter ??= Home.HomeSummaryService.TodayIn(h.TimeZoneId, clock).AddDays(-1);
            await db.SaveChangesAsync(ct);
            return Results.Ok(await categorization.RunAsync(h.Id, ct));
        });

        // Senders of incoming Zelle/Venmo-style transfers in the last year, most frequent first, to pick from.
        g.MapGet("/suggestions", async (HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var since = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddYears(-1);
            var incoming = await db.Transactions.AsNoTracking()
                .Where(t => t.HouseholdId == ctx.User.HouseholdId() && t.AmountCents < 0 && !t.IsTransfer && t.Date >= since
                            && (t.Kind == TransactionKind.Income || t.Kind == TransactionKind.PartnerContribution))
                .Select(t => new { t.Description, t.AmountCents })
                .ToListAsync(ct);
            return incoming
                .Select(t => (Name: PartnerDetector.SenderName(t.Description), t.AmountCents))
                .Where(x => x.Name is not null)
                .GroupBy(x => x.Name!)
                .Select(grp => new PartnerNameSuggestion(grp.Key, grp.Count(), grp.Sum(x => -x.AmountCents)))
                .OrderByDescending(s => s.Count).ThenByDescending(s => s.TotalCents)
                .Take(5)
                .ToList();
        });

        var p = app.MapGroup("/api/partner").RequireAuthorization();

        // The Partner screen (SPEC 4.8): fixed contributions with this month's status, and recent transfers between you.
        p.MapGet("", async (HttpContext ctx, AppDbContext db, Budget.BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var row = await months.EnsureCurrentAsync(householdId, ct);
            var month = await months.LoadAsync(householdId, row, ct);
            var arrangements = await db.PartnerArrangements.AsNoTracking().Where(a => a.HouseholdId == householdId)
                .OrderBy(a => a.CreatedUtc).Include(a => a.Category).ToListAsync(ct);
            var statuses = Budget.ContributionStatusDto.From(month.Contributions, month.Categories);
            var recent = await db.Transactions.AsNoTracking()
                .Where(t => t.HouseholdId == householdId && t.IsPartnerTransfer)
                .OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedUtc).Take(15)
                .Select(t => new PartnerActivityDto(t.Id, t.Date, t.AmountCents,
                    t.Kind == TransactionKind.PartnerTransfer ? "waiting"
                    : t.Kind == TransactionKind.PartnerContribution ? "contribution"
                    : t.AmountCents < 0 ? "payback" : "yourShare",
                    t.CategoryId != null ? t.Category!.Name : null))
                .ToListAsync(ct);
            return new PartnerOverviewDto(
                await db.Households.Where(h => h.Id == householdId).Select(h => h.PartnerName).SingleAsync(ct),
                arrangements.Select(a => new ArrangementDto(a.Id, a.CategoryId, a.Category.Name, a.AmountCents, a.WindowFromDay, a.WindowToDay,
                    statuses.FirstOrDefault(s => s.ArrangementId == a.Id))).ToList(),
                recent);
        });

        p.MapPost("/contributions", async (ArrangementRequest req, HttpContext ctx, AppDbContext db, Budget.BudgetMonthService months,
            CategorizationService categorization, TimeProvider clock, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            if (await ValidateAsync(req, householdId, db, ct) is { } problem) return problem;
            var current = await months.EnsureCurrentAsync(householdId, ct);
            var a = new PartnerArrangement
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, CategoryId = req.CategoryId, AmountCents = req.AmountCents,
                WindowFromDay = req.WindowFromDay ?? 18, WindowToDay = req.WindowToDay ?? 24, StartsOn = current.StartDate,
                CreatedUtc = clock.GetUtcNow().UtcDateTime,
            };
            db.PartnerArrangements.Add(a);
            await db.SaveChangesAsync(ct);
            await categorization.MatchContributionsAsync(householdId, ct);
            return Results.Ok(new { a.Id });
        });

        p.MapPut("/contributions/{id:guid}", async (Guid id, ArrangementRequest req, HttpContext ctx, AppDbContext db,
            CategorizationService categorization, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var a = await db.PartnerArrangements.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (a is null) return Results.NotFound();
            if (await ValidateAsync(req, householdId, db, ct) is { } problem) return problem;
            (a.CategoryId, a.AmountCents, a.WindowFromDay, a.WindowToDay) = (req.CategoryId, req.AmountCents, req.WindowFromDay ?? a.WindowFromDay, req.WindowToDay ?? a.WindowToDay);
            await db.SaveChangesAsync(ct);
            await categorization.MatchContributionsAsync(householdId, ct);
            return Results.NoContent();
        });

        // Removing it re-runs categorization, so a transfer it had claimed goes back to waiting for your label.
        p.MapDelete("/contributions/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CategorizationService categorization, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            if (await db.PartnerArrangements.Where(x => x.Id == id && x.HouseholdId == householdId).ExecuteDeleteAsync(ct) == 0)
                return Results.NotFound();
            await categorization.RunAsync(householdId, ct);
            return Results.NoContent();
        });
    }

    private static async Task<IResult?> ValidateAsync(ArrangementRequest r, Guid householdId, AppDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (r.AmountCents is <= 0 or > 1_000_000_00) errors["amountCents"] = ["Enter the amount she sends."];
        if (r.WindowFromDay is < 1 or > 31 || r.WindowToDay is < 1 or > 31 || (r.WindowFromDay ?? 18) > (r.WindowToDay ?? 24))
            errors["windowFromDay"] = ["Use days 1 to 31, from before to."];
        if (!await db.Categories.AnyAsync(c => c.Id == r.CategoryId && c.HouseholdId == householdId, ct))
            errors["categoryId"] = ["Pick the bill it goes toward."];
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }
}

public record ArrangementRequest(Guid CategoryId, long AmountCents, int? WindowFromDay = null, int? WindowToDay = null);
public record ArrangementDto(Guid Id, Guid CategoryId, string CategoryName, long AmountCents, int WindowFromDay, int WindowToDay, Budget.ContributionStatusDto? ThisMonth);
/// <param name="Purpose">"waiting" (not labeled yet), "contribution" (her money in), "payback" (from her, in a category), "yourShare" (to her, in a category).</param>
public record PartnerActivityDto(Guid Id, DateOnly Date, long AmountCents, string Purpose, string? CategoryName);
public record PartnerOverviewDto(string? PartnerName, IReadOnlyList<ArrangementDto> Contributions, IReadOnlyList<PartnerActivityDto> Recent);
