using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Home;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Transactions;

public record TransactionDto(
    Guid Id, DateOnly Date, long AmountCents, string Merchant, string Description, string Kind,
    Guid? CategoryId, string? CategoryName, string CategorySource, bool IsTransfer, bool Pending,
    Guid AccountId, string AccountName, string? BankCategory,
    // An automatic guess waiting for the user to confirm; and why it has this category.
    bool NeedsReview = false, string? CategoryReason = null,
    // The automatic category is a real guess, not just "likely right".
    bool Unsure = false,
    // Money to or from the partner; while it waits for a label, the category the app suggests (counted nowhere).
    bool IsPartnerTransfer = false, Guid? SuggestedCategoryId = null);

public record ConfirmRequest(List<Guid> Ids);
public record ConfirmLikelyRequest(string Scope);
public record ReviewCounts(int Total, int Likely, int Unsure);
/// <param name="Earlier">Before this month, inside the months the budget is built from (from <paramref name="WindowStart"/>).</param>
/// <param name="Older">Before <paramref name="WindowStart"/>: doesn't affect your budget.</param>
public record ReviewSummaryDto(ReviewCounts Month, ReviewCounts Earlier, ReviewCounts Older, DateOnly MonthStart, DateOnly WindowStart);
public record MerchantHistoryDto(string CategoryName, int Count);
public record TransactionContextDto(IReadOnlyList<MerchantHistoryDto> History);

/// <param name="Label">The budget month shown ("October 2026"), when one was asked for by how far back it is.</param>
public record TransactionListDto(DateOnly From, DateOnly To, IReadOnlyList<TransactionDto> Items, string? Label = null);

/// <param name="CategoryId">The category to use; null leaves it uncategorized.</param>
/// <param name="IsTransfer">True marks it as a transfer (never spending).</param>
/// <param name="RulePattern">When set, also creates a merchant rule: any merchant containing this text goes to the category.</param>
/// <param name="PartnerPurpose">"contribution" for money from your partner that's her contribution or just money from her. To say it paid you
/// back for something, send the category instead (DECISIONS.md 100).</param>
public record UpdateTransactionRequest(Guid? CategoryId, bool IsTransfer, string? RulePattern, string? PartnerPurpose = null);

/// <param name="Total">How many matched, even beyond the ones listed.</param>
/// <param name="NetCents">What the matches add up to: money out minus refunds, leaving out transfers and money coming in.</param>
public record SearchResultDto(IReadOnlyList<TransactionDto> Items, int Total, long NetCents);

/// <param name="BillChargesMoved">Filed under a bill: that bill's other charges that moved there too (DECISIONS.md 158).</param>
public record UpdateTransactionResult(TransactionDto Transaction, int RuleAppliedTo, int BillChargesMoved = 0);

public record CategoryDto(Guid Id, string Name, string Type, long MonthlyLimitCents, bool IsDiscretionary, int SortOrder);
public record RuleDto(Guid Id, string Pattern, Guid CategoryId, string CategoryName, DateTime CreatedUtc);

public static partial class TransactionEndpoints
{
    public const int InboxLimit = 500;
    public const int SearchLimit = 200;

    public static void MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();

        // GET /api/transactions                 -> the current budget month
        // GET /api/transactions?from=..&to=..   -> any date range (inclusive)
        // GET /api/transactions?back=2          -> the budget month two before this one (DECISIONS.md 153)
        group.MapGet("/transactions", async (DateOnly? from, DateOnly? to, int? back, HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            string? label = null;
            if (from is null || to is null)
            {
                var h = await db.Households.AsNoTracking().SingleAsync(x => x.Id == householdId, ct);
                var calendar = new BudgetMonthCalendar(h.BudgetMonthStartDay);
                var month = calendar.MonthContaining(HomeSummaryService.TodayIn(h.TimeZoneId, clock));
                for (var i = 0; i < Math.Clamp(back ?? 0, 0, 120); i++)
                    month = calendar.MonthContaining(month.Start.AddDays(-1));
                from ??= month.Start;
                to ??= month.End;
                label = month.Label.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
            }
            var items = await Project(db.Transactions.Where(t => t.HouseholdId == householdId && t.Date >= from && t.Date <= to))
                .ToListAsync(ct);
            return Results.Ok(new TransactionListDto(from.Value, to.Value, items, label));
        });

        // Search every transaction (DECISIONS.md 150, 164). Every filter you set applies at once: words (store, description
        // or category; a number like "12.50" also finds that amount), one category, an amount range, and a date range.
        // Newest first, at most SearchLimit, with how many matched in all and what they add up to.
        group.MapGet("/transactions/search", async (string? q, Guid? categoryId, decimal? min, decimal? max, DateOnly? from, DateOnly? to,
            HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var text = (q ?? "").Trim();
            if (text.Length > 100 || min < 0 || max < 0 || (min is { } lo && max is { } hi && lo > hi) || (from is { } f && to is { } t && f > t))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["search"] = ["Check the filters: a low amount or date can't be after the high one."] });
            var words = text.Length >= 2 ? text : null;
            if (words is null && categoryId is null && min is null && max is null && from is null && to is null)
                return Results.Ok(new SearchResultDto([], 0, 0));

            var householdId = ctx.User.HouseholdId();
            var matches = db.Transactions.Where(x => x.HouseholdId == householdId);
            if (words is not null)
            {
                var like = "%" + words.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                long? cents = decimal.TryParse(words.TrimStart('$').Replace(",", ""), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var dollars) && dollars > 0 && dollars < 10_000_000
                    ? (long)Math.Round(dollars * 100) : null;
                matches = matches.Where(x => EF.Functions.ILike(x.Merchant, like, "\\") || EF.Functions.ILike(x.Description, like, "\\")
                    || (x.Category != null && EF.Functions.ILike(x.Category.Name, like, "\\"))
                    || (cents != null && (x.AmountCents == cents || x.AmountCents == -cents)));
            }
            if (categoryId is { } cat) matches = matches.Where(x => x.CategoryId == cat);
            // Amounts are sizes: a $40 refund matches $30 to $50 just like a $40 charge.
            if (min is { } minDollars)
            {
                var c = (long)Math.Round(minDollars * 100);
                matches = matches.Where(x => x.AmountCents >= c || x.AmountCents <= -c);
            }
            if (max is { } maxDollars)
            {
                var c = (long)Math.Round(maxDollars * 100);
                matches = matches.Where(x => x.AmountCents <= c && x.AmountCents >= -c);
            }
            if (from is { } start) matches = matches.Where(x => x.Date >= start);
            if (to is { } end) matches = matches.Where(x => x.Date <= end);

            var total = await matches.CountAsync(ct);
            // Money out minus refunds, leaving out transfers and money coming in (paychecks).
            var net = await matches.Where(x => !x.IsTransfer && x.Kind != TransactionKind.Income && x.Kind != TransactionKind.PartnerContribution)
                .SumAsync(x => (long?)x.AmountCents, ct) ?? 0;
            return Results.Ok(new SearchResultDto(await Project(matches).Take(SearchLimit).ToListAsync(ct), total, net));
        });

        // Everything that affects spending but has no category yet, newest first (SPEC section 4.3).
        group.MapGet("/transactions/inbox", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await Project(Inbox(db, ctx.User.HouseholdId())).Take(InboxLimit).ToListAsync(ct));

        // Badge counts: purchases with no category and unconfirmed automatic categories, both this budget month.
        group.MapGet("/transactions/inbox/count", async (HttpContext ctx, AppDbContext db, BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var w = await WindowAsync(householdId, months, ct);
            return new
            {
                count = await Inbox(db, householdId).CountAsync(t => t.Date >= w.MonthStart && t.Date <= w.MonthEnd, ct),
                review = await Review(db, householdId).CountAsync(t => t.Date >= w.MonthStart && t.Date <= w.MonthEnd, ct),
            };
        });

        // Everything waiting for you (unconfirmed categories, purchases with no category, partner transfers to label),
        // by scope (DECISIONS.md 102): "month" (this budget month), "earlier" (before it, inside the months the budget is
        // built from), "older" (before those: doesn't affect your budget), or "all" earlier ones.
        group.MapGet("/transactions/review", async (string? scope, HttpContext ctx, AppDbContext db, BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var w = await WindowAsync(householdId, months, ct);
            var q = InScope(Todo(db, householdId), scope ?? "month", w);
            return await Project(q.OrderByDescending(t => t.CategoryId == null).ThenByDescending(t => t.CategoryUnsure)).Take(InboxLimit).ToListAsync(ct);
        });

        group.MapGet("/transactions/review/summary", async (HttpContext ctx, AppDbContext db, BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var w = await WindowAsync(householdId, months, ct);
            async Task<ReviewCounts> Count(string scope)
            {
                var q = InScope(Todo(db, householdId), scope, w);
                var likely = await q.CountAsync(t => t.CategoryId != null && !t.CategoryUnsure && t.NeedsReview, ct);
                var total = await q.CountAsync(ct);
                return new ReviewCounts(total, likely, total - likely);
            }
            return new ReviewSummaryDto(await Count("month"), await Count("earlier"), await Count("older"), w.MonthStart, w.WindowStart);
        });

        // Confirming makes the category the user's own (and teaches the categorizer that merchant).
        group.MapPost("/transactions/review/confirm", async (ConfirmRequest req, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            Results.Ok(new { confirmed = await Confirm(Review(db, ctx.User.HouseholdId()).Where(t => req.Ids.Contains(t.Id)), ct) }));

        // Bulk-confirm everything the categorizer was confident about (never the "unsure" ones), in one scope.
        group.MapPost("/transactions/review/confirm-likely", async (ConfirmLikelyRequest req, HttpContext ctx, AppDbContext db, BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var w = await WindowAsync(householdId, months, ct);
            var q = InScope(Review(db, householdId).Where(t => !t.CategoryUnsure && t.Kind != TransactionKind.PartnerTransfer), req.Scope, w);
            return Results.Ok(new { confirmed = await Confirm(q, ct) });
        });

        // "I don't remember": keep the app's best guess, or "Other" when it has none (DECISIONS.md 102).
        group.MapPost("/transactions/{id:guid}/best-guess", async (Guid id, HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var rows = await Todo(db, householdId).Where(t => t.Id == id).ToListAsync(ct);
            if (rows.Count == 0) return Results.NotFound();
            await BestGuessAsync(db, householdId, rows, clock, ct);
            return Results.Ok(await Project(db.Transactions.Where(t => t.Id == id)).SingleAsync(ct));
        });

        group.MapPost("/transactions/review/best-guess", async (ConfirmLikelyRequest req, HttpContext ctx, AppDbContext db, BudgetMonthService months, TimeProvider clock, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var w = await WindowAsync(householdId, months, ct);
            var rows = await InScope(Todo(db, householdId), req.Scope, w).ToListAsync(ct);
            await BestGuessAsync(db, householdId, rows, clock, ct);
            return Results.Ok(new { settled = rows.Count });
        });

        // A memory aid for a card: how you filed this merchant before (the same-day list went with DECISIONS.md 145).
        group.MapGet("/transactions/{id:guid}/context", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var t = await db.Transactions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (t is null) return Results.NotFound();
            var history = await db.Transactions.AsNoTracking()
                .Where(x => x.HouseholdId == householdId && x.Merchant == t.Merchant && x.Id != id && x.CategoryId != null
                            && x.CategorySource == CategorySource.User && !x.IsTransfer)
                .Select(x => x.Category!.Name).ToListAsync(ct);
            var counts = history.GroupBy(n => n).Select(g => new MerchantHistoryDto(g.Key, g.Count()))
                .OrderByDescending(x => x.Count).ThenBy(x => x.CategoryName, StringComparer.Ordinal).Take(4).ToList();
            return Results.Ok(new TransactionContextDto(counts));
        });

        group.MapPost("/transactions/auto-categorize", async (HttpContext ctx, CategorizationService svc, CancellationToken ct) =>
            await svc.RunAsync(ctx.User.HouseholdId(), ct));

        group.MapGet("/transactions/suggested-categories", async (HttpContext ctx, CategorizationService svc, CancellationToken ct) =>
            await svc.SuggestionsAsync(ctx.User.HouseholdId(), ct));

        group.MapPatch("/transactions/{id:guid}", async (Guid id, UpdateTransactionRequest req, HttpContext ctx, AppDbContext db, TimeProvider clock,
            Bills.RecurringService recurring, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var t = await db.Transactions.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (t is null)
                return Results.NotFound();
            if (req.CategoryId is { } cid && !await db.Categories.AnyAsync(c => c.Id == cid && c.HouseholdId == householdId, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["categoryId"] = ["Unknown category."] });

            // Money from your partner that's her contribution or just money from her (DECISIONS.md 100): partner money in.
            if (req.PartnerPurpose is not null)
            {
                if (req.PartnerPurpose != "contribution" || t.AmountCents >= 0)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["partnerPurpose"] = ["That doesn't fit this transaction."] });
                (t.Kind, t.CategoryId, t.SuggestedCategoryId, t.CategorySource, t.NeedsReview, t.CategoryUnsure, t.IsTransfer) =
                    (TransactionKind.PartnerContribution, null, null, CategorySource.User, false, false, false);
                t.CategoryReason = "Your partner's money: you said so.";
                await db.SaveChangesAsync(ct);
                return Results.Ok(new UpdateTransactionResult(await Project(db.Transactions.Where(x => x.Id == t.Id)).SingleAsync(ct), 0));
            }

            t.CategoryId = req.CategoryId;
            t.SuggestedCategoryId = null;
            t.CategorySource = CategorySource.User; // locks this row against rules and transfer detection
            t.NeedsReview = false;
            t.CategoryUnsure = false;
            t.CategoryReason = "You chose this.";
            // Money between you, labeled with what it was for: from her it pays back that category (like a refund);
            // to her it's your share of something, spending in that category.
            if (t.Kind == TransactionKind.PartnerTransfer && req.CategoryId is not null)
                t.Kind = t.AmountCents < 0 ? TransactionKind.Refund : TransactionKind.Purchase;
            if (req.IsTransfer && !t.IsTransfer)
            {
                t.IsTransfer = true;
                if (t.Kind is not (TransactionKind.CardPayment or TransactionKind.Transfer))
                    t.Kind = TransactionKind.Transfer;
            }
            else if (!req.IsTransfer && t.IsTransfer)
            {
                t.IsTransfer = false;
                t.TransferGroupId = null;
                if (t.Kind is TransactionKind.CardPayment or TransactionKind.Transfer)
                    t.Kind = t.AmountCents > 0 ? TransactionKind.Purchase : TransactionKind.Income;
            }

            var applied = 0;
            var pattern = NormalizePattern(req.RulePattern);
            if (pattern is not null)
            {
                if (req.CategoryId is null || req.IsTransfer)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["rulePattern"] = ["A rule needs a category and can't be a transfer."] });

                var rule = await db.CategoryRules.SingleOrDefaultAsync(r => r.HouseholdId == householdId && r.Pattern == pattern, ct);
                if (rule is null)
                    db.CategoryRules.Add(rule = new CategoryRule { Id = Guid.NewGuid(), HouseholdId = householdId, Pattern = pattern, CreatedUtc = clock.GetUtcNow().UtcDateTime });
                rule.CategoryId = req.CategoryId.Value;
                await db.SaveChangesAsync(ct);

                // Rules outrank bank categories, so apply to matching rows that only had a bank category or none.
                // Merchants are stored upper case, and so is the pattern.
                applied = await db.Transactions
                    .Where(x => x.HouseholdId == householdId && x.Id != t.Id && !x.IsTransfer
                                && (x.CategorySource == CategorySource.None || x.CategorySource == CategorySource.Bank || x.CategorySource == CategorySource.Learned)
                                && x.Merchant.ToUpper().Contains(pattern))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.CategoryId, req.CategoryId)
                        .SetProperty(x => x.CategorySource, CategorySource.Rule)
                        .SetProperty(x => x.NeedsReview, true) // filed by your rule, but still yours to confirm
                        .SetProperty(x => x.CategoryUnsure, false)
                        .SetProperty(x => x.CategoryReason, "Your rule for \"" + pattern + "\"."), ct);
            }
            await db.SaveChangesAsync(ct);

            // Filed under a bill: the bill's other charges go there too, so its budget line sees the whole bill (DECISIONS.md 158).
            var moved = req.CategoryId is { } billId && !req.IsTransfer
                        && await db.Categories.AnyAsync(c => c.Id == billId && c.Type == CategoryType.FixedBill, ct)
                ? await recurring.KeepBillsTogetherAsync(householdId, ct, t.Id) : 0;

            var dto = await Project(db.Transactions.Where(x => x.Id == t.Id)).SingleAsync(ct);
            return Results.Ok(new UpdateTransactionResult(dto, applied, moved));
        });

        group.MapGet("/budget/categories", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await db.Categories.Where(c => c.HouseholdId == ctx.User.HouseholdId() && !c.IsHidden)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .Select(c => new CategoryDto(c.Id, c.Name, c.Type.ToString(), c.MonthlyLimitCents, c.IsDiscretionary, c.SortOrder))
                .ToListAsync(ct));

        group.MapGet("/rules", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await db.CategoryRules.Where(r => r.HouseholdId == ctx.User.HouseholdId())
                .OrderBy(r => r.Pattern)
                .Select(r => new RuleDto(r.Id, r.Pattern, r.CategoryId, r.Category.Name, r.CreatedUtc))
                .ToListAsync(ct));

        // Deleting a rule leaves already-categorized transactions as they are.
        group.MapDelete("/rules/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await db.CategoryRules.Where(r => r.Id == id && r.HouseholdId == ctx.User.HouseholdId()).ExecuteDeleteAsync(ct) > 0
                ? Results.NoContent()
                : Results.NotFound());
    }

    /// <summary>Same rule as <see cref="TransactionRules.NeedsCategory"/>, written so the database can run it.</summary>
    public static IQueryable<Transaction> Inbox(AppDbContext db, Guid householdId) =>
        db.Transactions.Where(t => t.HouseholdId == householdId
                                   && t.CategoryId == null
                                   && !t.IsTransfer
                                   && t.Kind != TransactionKind.Income
                                   && t.Kind != TransactionKind.PartnerContribution
                                   && t.Kind != TransactionKind.CardPayment
                                   && t.Kind != TransactionKind.Transfer
                                   && t.Kind != TransactionKind.PartnerTransfer
                                   && (t.AmountCents > 0 || t.Kind == TransactionKind.Refund));

    /// <summary>Confirms automatic categories. A transfer with your partner that's still waiting needs a label, not a ✓, so it's skipped.</summary>
    private static Task<int> Confirm(IQueryable<Transaction> q, CancellationToken ct) =>
        q.Where(t => t.Kind != TransactionKind.PartnerTransfer).ExecuteUpdateAsync(s => s.SetProperty(t => t.NeedsReview, false)
            .SetProperty(t => t.CategoryUnsure, false)
            .SetProperty(t => t.CategorySource, CategorySource.User)
            .SetProperty(t => t.CategoryReason, "You confirmed this."), ct);

    /// <summary>This budget month, and where the months the budget is built from begin (the generator's last 6).</summary>
    public sealed record ReviewWindow(DateOnly MonthStart, DateOnly MonthEnd, DateOnly WindowStart);

    private static async Task<ReviewWindow> WindowAsync(Guid householdId, BudgetMonthService months, CancellationToken ct)
    {
        var row = await months.EnsureCurrentAsync(householdId, ct);
        var (_, _, calendar) = await months.ContextAsync(householdId, ct);
        // Step back whole budget months: the generator uses the last 6 complete ones.
        var windowStart = row.StartDate;
        for (var i = 0; i < Core.Budgeting.BudgetGenerator.MaxMonths; i++)
            windowStart = calendar.MonthContaining(windowStart.AddDays(-1)).Start;
        return new ReviewWindow(row.StartDate, row.EndDate, windowStart);
    }

    /// <summary>Everything waiting for you: unconfirmed automatic categories and partner transfers, and purchases with no category.</summary>
    public static IQueryable<Transaction> Todo(AppDbContext db, Guid householdId) =>
        db.Transactions.Where(t => t.HouseholdId == householdId && !t.IsTransfer
            && ((t.NeedsReview && (t.CategoryId != null || t.Kind == TransactionKind.PartnerContribution || t.Kind == TransactionKind.PartnerTransfer))
                || (t.CategoryId == null && t.Kind != TransactionKind.Income && t.Kind != TransactionKind.PartnerContribution
                    && t.Kind != TransactionKind.CardPayment && t.Kind != TransactionKind.Transfer && t.Kind != TransactionKind.PartnerTransfer
                    && (t.AmountCents > 0 || t.Kind == TransactionKind.Refund))));

    private static IQueryable<Transaction> InScope(IQueryable<Transaction> q, string scope, ReviewWindow w) => scope switch
    {
        "earlier" => q.Where(t => t.Date < w.MonthStart && t.Date >= w.WindowStart),
        "older" => q.Where(t => t.Date < w.WindowStart),
        "all" => q.Where(t => t.Date < w.MonthStart),
        _ => q.Where(t => t.Date >= w.MonthStart && t.Date <= w.MonthEnd),
    };

    /// <summary>
    /// "I don't remember" (DECISIONS.md 102): keep the app's guess, or file it under "Other" (created when first needed) when
    /// there's none. Money from your partner with no suggestion counts as her money; with one, it pays back that category.
    /// Settled like your own choice, but marked as a best guess and never used to teach the categorizer.
    /// </summary>
    private static async Task BestGuessAsync(AppDbContext db, Guid householdId, List<Transaction> rows, TimeProvider clock, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        Guid? otherId = null;
        async Task<Guid> OtherAsync()
        {
            if (otherId is { } id) return id;
            var other = await db.Categories.FirstOrDefaultAsync(c => c.HouseholdId == householdId && c.Name.ToLower() == "other", ct);
            if (other is null)
            {
                var order = await db.Categories.Where(c => c.HouseholdId == householdId).MaxAsync(c => (int?)c.SortOrder, ct) ?? -1;
                db.Categories.Add(other = new Category
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Other", Type = Core.Budgeting.CategoryType.Variable,
                    MonthlyLimitCents = 0, SortOrder = order + 1,
                });
            }
            other.IsHidden = false;
            otherId = other.Id;
            return other.Id;
        }

        foreach (var t in rows)
        {
            if (t.Kind == TransactionKind.PartnerTransfer && t.AmountCents < 0)
            {
                (t.Kind, t.CategoryId) = t.SuggestedCategoryId is { } paid ? (TransactionKind.Refund, paid) : (TransactionKind.PartnerContribution, (Guid?)null);
            }
            else if (t.Kind == TransactionKind.PartnerTransfer)
            {
                (t.Kind, t.CategoryId) = (TransactionKind.Purchase, t.SuggestedCategoryId ?? await OtherAsync());
            }
            else if (t.CategoryId is null)
            {
                t.CategoryId = await OtherAsync();
            }
            (t.CategorySource, t.NeedsReview, t.CategoryUnsure, t.SuggestedCategoryId) = (CategorySource.BestGuess, false, false, null);
            t.CategoryReason = "Best guess: you didn't remember.";
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Automatic categories the user hasn't confirmed yet.</summary>
    public static IQueryable<Transaction> Review(AppDbContext db, Guid householdId) =>
        db.Transactions.Where(t => t.HouseholdId == householdId && t.NeedsReview && !t.IsTransfer
            && (t.CategoryId != null || t.Kind == TransactionKind.PartnerContribution || t.Kind == TransactionKind.PartnerTransfer));

    private static IQueryable<TransactionDto> Project(IQueryable<Transaction> q) =>
        q.OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedUtc).ThenBy(t => t.Id)
            .Select(t => new TransactionDto(
                t.Id, t.Date, t.AmountCents, t.Merchant, t.Description, t.Kind.ToString(),
                t.CategoryId, t.Category != null ? t.Category.Name : null, t.CategorySource.ToString(), t.IsTransfer, t.Pending,
                t.AccountId, t.Account.Name, t.BankCategory, t.NeedsReview, t.CategoryReason, t.CategoryUnsure, t.IsPartnerTransfer, t.SuggestedCategoryId));

    public static string? NormalizePattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;
        var p = Whitespace().Replace(pattern, " ").Trim().ToUpperInvariant();
        return p.Length > 200 ? p[..200] : p;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
