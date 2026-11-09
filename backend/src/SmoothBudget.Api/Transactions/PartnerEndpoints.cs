using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Transactions;

/// <param name="PartnerName">The name on the friend's transfers. Read back: every tracked friend's, joined.</param>
/// <param name="FriendId">Whose transfers these are (DECISIONS.md 207, 211); none: the only friend tracked, or one made from the name.</param>
public record PartnerSettingsDto(string? PartnerName, Guid? FriendId = null);
public record PartnerNameSuggestion(string Name, int Count, long TotalCents);

/// <summary>
/// A friend's name as it appears on their transfers (SPEC Appendix D: stored per friend, never hard-coded), and their
/// fixed contributions. Money from that name is that friend's, kept apart from your own income; see DECISIONS.md 97-100
/// and 211 (any number of friends).
/// </summary>
public static class PartnerEndpoints
{
    public static void MapPartnerEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings/partner").RequireAuthorization();

        g.MapGet("", async (HttpContext ctx, CategorizationService categorization, CancellationToken ct) =>
            new PartnerSettingsDto((await categorization.FriendsAsync(ctx.User.HouseholdId(), ct)) is { Count: > 0 } fs ? string.Join(", ", fs.Select(f => f.TransferName)) : null));

        // Saving re-tags every incoming transfer: from that name it's partner money, otherwise your income.
        g.MapPut("", async (PartnerSettingsDto req, HttpContext ctx, CategorizationService categorization, Shared.SharedService shared, CancellationToken ct) =>
        {
            var name = string.IsNullOrWhiteSpace(req.PartnerName) ? null : req.PartnerName.Trim();
            if (name is { Length: > 100 or < 2 })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["partnerName"] = ["Use 2 to 100 characters."] });
            var householdId = ctx.User.HouseholdId();
            if (await shared.SetTransferNameAsync(householdId, req.FriendId, name, ct) is { } problem)
                return Results.Problem(problem, statusCode: StatusCodes.Status409Conflict);
            return Results.Ok(await categorization.RunAsync(householdId, ct));
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

        // A friend's money (SPEC 4.8): their fixed contributions with this month's status, and recent transfers between you.
        // No friend given (older clients): everyone's.
        p.MapGet("", async (Guid? friendId, HttpContext ctx, AppDbContext db, Budget.BudgetMonthService months, CategorizationService categorization, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var row = await months.EnsureCurrentAsync(householdId, ct);
            var month = await months.LoadAsync(householdId, row, ct);
            var arrangements = await db.PartnerArrangements.AsNoTracking().Where(a => a.HouseholdId == householdId && (friendId == null || a.FriendId == friendId))
                .OrderBy(a => a.CreatedUtc).Include(a => a.Category).ToListAsync(ct);
            var statuses = Budget.ContributionStatusDto.From(month.Contributions, month.Categories, await Shared.SharedService.SendersAsync(db, householdId, ct));
            var names = (await categorization.FriendsAsync(householdId, ct)).Where(f => friendId == null || f.Id == friendId).Select(f => f.TransferName).ToList();
            var recent = await db.Transactions.AsNoTracking()
                .Where(t => t.HouseholdId == householdId && t.IsPartnerTransfer && (friendId == null || t.FriendId == friendId))
                .OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedUtc).Take(15)
                .Select(t => new PartnerActivityDto(t.Id, t.Date, t.AmountCents,
                    t.Kind == TransactionKind.PartnerTransfer ? "waiting"
                    : t.Kind == TransactionKind.PartnerContribution ? "contribution"
                    : t.AmountCents < 0 ? "payback" : "yourShare",
                    t.CategoryId != null ? t.Category!.Name : null))
                .ToListAsync(ct);
            var counted = await db.Transactions.AsNoTracking()
                .Where(t => t.HouseholdId == householdId && t.IsPartnerTransfer && (friendId == null || t.FriendId == friendId) && t.CountsForMonthStart == month.Range.Start)
                .OrderBy(t => t.Date).Select(t => new EarlyDepositDto(t.Id, t.Date, -t.AmountCents)).ToListAsync(ct);
            var mine = arrangements.Select(a => a.Id).ToHashSet();
            var waiting = statuses.Any(s => mine.Contains(s.ArrangementId) && s.State is "expected" or "late" or "differentAmount");
            return new PartnerOverviewDto(
                names.Count > 0 ? string.Join(", ", names) : null,
                arrangements.Select(a => new ArrangementDto(a.Id, a.CategoryId, a.Category.Name, a.AmountCents, a.WindowFromDay, a.WindowToDay,
                    statuses.FirstOrDefault(s => s.ArrangementId == a.Id))).ToList(),
                recent,
                waiting ? (await EarlyCandidatesAsync(householdId, month.Range, db, months, ct, friendId)).Select(d => new EarlyDepositDto(d.Id, d.Date, d.AmountCents)).ToList() : [],
                counted);
        });

        // Her contribution sent early (DECISIONS.md 166): a transfer from just before this budget month counts toward it.
        p.MapPost("/early/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, Budget.BudgetMonthService months,
            CategorizationService categorization, Budget.AutoBudgetService auto, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var row = await months.EnsureCurrentAsync(householdId, ct);
            var range = (await months.LoadAsync(householdId, row, ct)).Range;
            if (!(await EarlyCandidatesAsync(householdId, range, db, months, ct)).Any(d => d.Id == id))
                return Results.NotFound();
            var t = await db.Transactions.SingleAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            (t.CountsForMonthStart, t.Kind, t.CategoryId, t.SuggestedCategoryId, t.CategorySource, t.NeedsReview, t.CategoryUnsure, t.CategoryReason) =
                (range.Start, TransactionKind.PartnerContribution, null, null, CategorySource.User, false, false,
                    $"Sent early: counts toward the budget month starting {range.Start:MMM d}.");
            await db.SaveChangesAsync(ct);
            await categorization.MatchContributionsAsync(householdId, ct);
            await auto.RefreshAsync(householdId, ct);
            return Results.NoContent();
        });

        // Undo: it goes back to the month it landed in, and waits for your label again.
        p.MapDelete("/early/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CategorizationService categorization,
            Budget.AutoBudgetService auto, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var t = await db.Transactions.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId && x.CountsForMonthStart != null, ct);
            if (t is null) return Results.NotFound();
            (t.CountsForMonthStart, t.CategorySource, t.CategoryReason) = (null, CategorySource.None, null);
            await db.SaveChangesAsync(ct);
            await categorization.RunAsync(householdId, ct);
            await auto.RefreshAsync(householdId, ct);
            return Results.NoContent();
        });

        p.MapPost("/contributions", async (ArrangementRequest req, HttpContext ctx, AppDbContext db, Budget.BudgetMonthService months,
            CategorizationService categorization, TimeProvider clock, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            if (await ValidateAsync(req, householdId, db, ct) is { } problem) return problem;
            // Whose it is; no friend given (older clients): the only friend whose transfers are tracked.
            var tracked = await categorization.FriendsAsync(householdId, ct);
            var friendId = req.FriendId is { } given
                ? await db.Friends.AnyAsync(f => f.Id == given && f.HouseholdId == householdId, ct) ? given : (Guid?)null
                : tracked.Count == 1 ? tracked[0].Id : null;
            if (friendId is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["friendId"] = ["Pick the friend who sends it."] });
            var current = await months.EnsureCurrentAsync(householdId, ct);
            var a = new PartnerArrangement
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, FriendId = friendId.Value, CategoryId = req.CategoryId, AmountCents = req.AmountCents,
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

    /// <summary>A friend's transfers (anyone's, with no friend given) from the days before <paramref name="range"/> that weren't the contribution of the month they landed in.</summary>
    private static async Task<IReadOnlyList<Core.Budgeting.PartnerDeposit>> EarlyCandidatesAsync(
        Guid householdId, Core.Budgeting.BudgetMonthRange range, AppDbContext db, Budget.BudgetMonthService months, CancellationToken ct, Guid? friendId = null)
    {
        var from = range.Start.AddDays(-Core.Budgeting.PartnerContributions.EarlyDays);
        var deposits = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.IsPartnerTransfer && (friendId == null || t.FriendId == friendId)
                        && t.AmountCents < 0 && !t.IsTransfer && t.Date >= from && t.Date < range.Start)
            .Select(t => new Core.Budgeting.PartnerDeposit(t.Id, t.Date, -t.AmountCents,
                t.CategorySource == CategorySource.User && t.Kind == TransactionKind.PartnerContribution,
                t.CategorySource == CategorySource.User && t.Kind != TransactionKind.PartnerContribution, t.CountsForMonthStart))
            .ToListAsync(ct);
        if (deposits.Count == 0) return [];
        var previous = new Core.Budgeting.BudgetMonthCalendar(await db.Households.Where(h => h.Id == householdId).Select(h => h.BudgetMonthStartDay).SingleAsync(ct))
            .MonthContaining(range.Start.AddDays(-1));
        var matched = (await months.ContributionsAsync(householdId, previous, ct)).Where(s => s.MatchedDepositId is not null).Select(s => s.MatchedDepositId!.Value).ToHashSet();
        return Core.Budgeting.PartnerContributions.EarlyCandidates(range, deposits, matched);
    }

    private static async Task<IResult?> ValidateAsync(ArrangementRequest r, Guid householdId, AppDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (r.AmountCents is <= 0 or > 1_000_000_00) errors["amountCents"] = ["Enter the amount they send."];
        if (r.WindowFromDay is < 1 or > 31 || r.WindowToDay is < 1 or > 31 || (r.WindowFromDay ?? 18) > (r.WindowToDay ?? 24))
            errors["windowFromDay"] = ["Use days 1 to 31, from before to."];
        if (!await db.Categories.AnyAsync(c => c.Id == r.CategoryId && c.HouseholdId == householdId, ct))
            errors["categoryId"] = ["Pick the bill it goes toward."];
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }
}

/// <param name="FriendId">Who sends it (DECISIONS.md 211). Only read when adding one; none: the only friend whose transfers are tracked.</param>
public record ArrangementRequest(Guid CategoryId, long AmountCents, int? WindowFromDay = null, int? WindowToDay = null, Guid? FriendId = null);
public record ArrangementDto(Guid Id, Guid CategoryId, string CategoryName, long AmountCents, int WindowFromDay, int WindowToDay, Budget.ContributionStatusDto? ThisMonth);
/// <param name="Purpose">"waiting" (not labeled yet), "contribution" (her money in), "payback" (from her, in a category), "yourShare" (to her, in a category).</param>
public record PartnerActivityDto(Guid Id, DateOnly Date, long AmountCents, string Purpose, string? CategoryName);
/// <param name="EarlyCandidates">Her transfers from just before this month that may be this month's contribution sent early.</param>
/// <param name="CountedEarly">Transfers you moved into this month.</param>
public record PartnerOverviewDto(string? PartnerName, IReadOnlyList<ArrangementDto> Contributions, IReadOnlyList<PartnerActivityDto> Recent,
    IReadOnlyList<EarlyDepositDto> EarlyCandidates, IReadOnlyList<EarlyDepositDto> CountedEarly);
public record EarlyDepositDto(Guid Id, DateOnly Date, long AmountCents);
