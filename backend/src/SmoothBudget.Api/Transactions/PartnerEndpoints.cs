using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Transactions;

public record PartnerSettingsDto(string? PartnerName);
public record PartnerNameSuggestion(string Name, int Count, long TotalCents);

/// <summary>
/// The partner's name as it appears on her transfers (SPEC Appendix D: stored in Settings, never hard-coded).
/// Money in from that name is a partner contribution, kept apart from the owner's own income.
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
            // Settle-up starts today, today included: money from her before today is her contribution, as it always was (DECISIONS.md 92, 98).
            if (name is not null)
                h.PartnerSettledThrough ??= Home.HomeSummaryService.TodayIn(h.TimeZoneId, clock).AddDays(-1); // counts from today on
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

        // Who owes whom from split purchases and paying back (SPEC 4.8), since the last time it was marked settled.
        p.MapGet("/settle-up", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await SettleUpAsync(db, ctx.User.HouseholdId(), ct));

        p.MapPost("/settle-up/settled", async (HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var h = await db.Households.SingleAsync(x => x.Id == ctx.User.HouseholdId(), ct);
            h.PartnerSettledThrough = Home.HomeSummaryService.TodayIn(h.TimeZoneId, clock);
            await db.SaveChangesAsync(ct);
            return await SettleUpAsync(db, h.Id, ct);
        });

        // The Partner screen (SPEC 4.8): fixed contributions with this month's status, split categories, settle-up,
        // and her recent transfers.
        p.MapGet("", async (HttpContext ctx, AppDbContext db, Budget.BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var row = await months.EnsureCurrentAsync(householdId, ct);
            var month = await months.LoadAsync(householdId, row, ct);
            var arrangements = await db.PartnerArrangements.AsNoTracking().Where(a => a.HouseholdId == householdId)
                .OrderBy(a => a.CreatedUtc).Include(a => a.Category).ToListAsync(ct);
            var statuses = Budget.ContributionStatusDto.From(month.Contributions, month.Categories);
            var splits = await db.Categories.AsNoTracking()
                .Where(c => c.HouseholdId == householdId && !c.IsHidden && c.Type == Core.Budgeting.CategoryType.Variable)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .Select(c => new SplitCategoryDto(c.Id, c.Name, c.PartnerSharePercent)).ToListAsync(ct);
            var recent = await db.Transactions.AsNoTracking()
                .Where(t => t.HouseholdId == householdId && (t.IsPartnerTransfer || t.PaidByPartner))
                .OrderByDescending(t => t.Date).Take(15)
                .Select(t => new PartnerActivityDto(t.Id, t.Date, t.Merchant, t.AmountCents,
                    t.PaidByPartner ? "paidByPartner" : t.Kind == TransactionKind.PartnerContribution ? "contribution"
                    : t.Kind == TransactionKind.PartnerSettleUp ? "settleUp" : "other",
                    t.CategoryId != null ? t.Category!.Name : null, t.CategorySource == CategorySource.User))
                .ToListAsync(ct);
            return new PartnerOverviewDto(
                await db.Households.Where(h => h.Id == householdId).Select(h => h.PartnerName).SingleAsync(ct),
                arrangements.Select(a => new ArrangementDto(a.Id, a.CategoryId, a.Category.Name, a.AmountCents, a.WindowFromDay, a.WindowToDay,
                    statuses.FirstOrDefault(s => s.ArrangementId == a.Id))).ToList(),
                splits, await SettleUpAsync(db, householdId, ct), recent);
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

        p.MapDelete("/contributions/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await db.PartnerArrangements.Where(x => x.Id == id && x.HouseholdId == ctx.User.HouseholdId()).ExecuteDeleteAsync(ct) > 0
                ? Results.NoContent()
                : Results.NotFound());

        // Split a category by default: her share in percent (0 = not split). Re-files so automatic purchases follow it.
        p.MapPut("/splits/{categoryId:guid}", async (Guid categoryId, SplitRequest req, HttpContext ctx, AppDbContext db,
            CategorizationService categorization, CancellationToken ct) =>
        {
            if (req.PartnerSharePercent is < 0 or >= 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["partnerSharePercent"] = ["Use 0 (not split) up to 99."] });
            var c = await db.Categories.SingleOrDefaultAsync(x => x.Id == categoryId && x.HouseholdId == ctx.User.HouseholdId(), ct);
            if (c is null) return Results.NotFound();
            c.PartnerSharePercent = req.PartnerSharePercent;
            await db.SaveChangesAsync(ct);
            await categorization.RunAsync(c.HouseholdId, ct);
            return Results.NoContent();
        });

        // Something she paid for (SPEC 3: "a $120 purchase by the partner creates $60 owed to the partner"). Your share
        // counts in the category; you owe her it in settle-up. Kept on a "Paid by …" account so it shows everywhere.
        p.MapPost("/purchases", async (PartnerPurchaseRequest req, HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var errors = new Dictionary<string, string[]>();
            if (req.AmountCents is <= 0 or > 1_000_000_00) errors["amountCents"] = ["Enter what she paid."];
            if (req.OwnerSharePercent is < 1 or > 100) errors["ownerSharePercent"] = ["Your share is 1% to 100%."];
            if (string.IsNullOrWhiteSpace(req.Merchant) || req.Merchant.Trim().Length > 200) errors["merchant"] = ["Where was it?"];
            var cat = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == req.CategoryId && c.HouseholdId == householdId, ct);
            if (cat is null) errors["categoryId"] = ["Pick a category."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var h = await db.Households.AsNoTracking().SingleAsync(x => x.Id == householdId, ct);
            var first = h.PartnerName?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0] is { Length: > 0 } f
                ? char.ToUpperInvariant(f[0]) + f[1..].ToLowerInvariant() : "partner";
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.HouseholdId == householdId && a.Source == DataSource.Manual && a.Name.StartsWith("Paid by "), ct);
            if (account is null)
            {
                account = new Account { Id = Guid.NewGuid(), HouseholdId = householdId, Name = $"Paid by {first}", Type = AccountType.Checking,
                    Source = DataSource.Manual, CreatedUtc = clock.GetUtcNow().UtcDateTime };
                db.Accounts.Add(account);
            }
            var share = (req.OwnerSharePercent ?? (cat!.PartnerSharePercent > 0 ? 100 - cat.PartnerSharePercent : 50)) / 100m;
            var merchant = Core.Import.MerchantText.Normalize(req.Merchant.Trim());
            var t = new Transaction
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, AccountId = account.Id, Date = req.Date, AmountCents = req.AmountCents,
                Merchant = merchant, Description = req.Merchant.Trim(), Kind = TransactionKind.Purchase, CategoryId = req.CategoryId,
                CategorySource = CategorySource.User, CategoryReason = $"{first} paid; you entered it.", SplitFactor = share, PaidByPartner = true,
                Source = DataSource.Manual, CreatedUtc = clock.GetUtcNow().UtcDateTime,
            };
            db.Transactions.Add(t);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { t.Id });
        });

        p.MapDelete("/purchases/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await db.Transactions.Where(t => t.Id == id && t.HouseholdId == ctx.User.HouseholdId() && t.PaidByPartner).ExecuteDeleteAsync(ct) > 0
                ? Results.NoContent()
                : Results.NotFound());
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

    public static async Task<SettleUpDto> SettleUpAsync(AppDbContext db, Guid householdId, CancellationToken ct)
    {
        var h = await db.Households.AsNoTracking().Where(x => x.Id == householdId).Select(x => new { x.PartnerName, x.PartnerSettledThrough }).SingleAsync(ct);
        var after = h.PartnerSettledThrough;
        var splits = await CategorizationService.SplitPurchasesAsync(db, householdId, after, ct);
        var transfers = await db.Transactions.AsNoTracking()
            // Only paying back you confirmed: the app's guesses never move money between you (DECISIONS.md 92).
            .Where(t => t.HouseholdId == householdId && t.Kind == TransactionKind.PartnerSettleUp && t.CategorySource == CategorySource.User
                        && (after == null || t.Date > after))
            .Select(t => new { t.Id, t.Date, t.AmountCents, t.Merchant, t.Kind })
            .ToListAsync(ct);
        var b = PartnerMoney.Balance(splits, transfers.Select(t => new PartnerTransfer(t.Id, t.Date, t.AmountCents, t.Kind)).ToList());

        var items = splits.Select(s => new SettleUpItemDto(s.Id, s.Date, s.Merchant, s.PartnerShareCents, "split"))
            .Concat(transfers.Select(t => new SettleUpItemDto(t.Id, t.Date, t.Merchant, t.AmountCents, t.AmountCents < 0 ? "paidByPartner" : "paidByYou")))
            .OrderByDescending(i => i.Date).Take(30).ToList();
        return new SettleUpDto(h.PartnerName, b.OwedToOwnerCents, b.SplitSharesCents, b.SettledByHerCents, b.SettledByOwnerCents, after, items);
    }
}

/// <param name="EffectCents">For "split": her share. For transfers: the amount (negative = she sent it).</param>
public record SettleUpItemDto(Guid TransactionId, DateOnly Date, string Merchant, long EffectCents, string Kind);

/// <param name="OwedToYouCents">Positive: your partner owes you. Negative: you owe her.</param>
public record SettleUpDto(
    string? PartnerName, long OwedToYouCents, long SplitSharesCents, long PaidBackByPartnerCents, long PaidBackByYouCents,
    DateOnly? SettledThrough, IReadOnlyList<SettleUpItemDto> Items);

public record ArrangementRequest(Guid CategoryId, long AmountCents, int? WindowFromDay = null, int? WindowToDay = null);
public record ArrangementDto(Guid Id, Guid CategoryId, string CategoryName, long AmountCents, int WindowFromDay, int WindowToDay, Budget.ContributionStatusDto? ThisMonth);
public record SplitCategoryDto(Guid CategoryId, string Name, int PartnerSharePercent);
public record SplitRequest(int PartnerSharePercent);
/// <param name="OwnerSharePercent">Your share (default: the category's split, else half).</param>
public record PartnerPurchaseRequest(DateOnly Date, string Merchant, long AmountCents, Guid CategoryId, int? OwnerSharePercent = null);
/// <param name="Purpose">"contribution", "settleUp", "paidByPartner" or "other" (money you sent her for something, filed in a category).</param>
public record PartnerActivityDto(Guid Id, DateOnly Date, string Merchant, long AmountCents, string Purpose, string? CategoryName, bool Confirmed);
public record PartnerOverviewDto(
    string? PartnerName, IReadOnlyList<ArrangementDto> Contributions, IReadOnlyList<SplitCategoryDto> Splits,
    SettleUpDto SettleUp, IReadOnlyList<PartnerActivityDto> Recent);
