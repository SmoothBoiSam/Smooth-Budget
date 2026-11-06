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
    // Money to or from the partner, and the owner's share of a split purchase (1 = not split).
    bool IsPartnerTransfer = false, decimal OwnerShare = 1m);

public record ConfirmRequest(List<Guid> Ids);
public record ConfirmLikelyRequest(string Scope);
public record ReviewCounts(int Total, int Likely, int Unsure);
public record ReviewSummaryDto(ReviewCounts Month, ReviewCounts Earlier);

public record TransactionListDto(DateOnly From, DateOnly To, IReadOnlyList<TransactionDto> Items);

/// <param name="CategoryId">The category to use; null leaves it uncategorized.</param>
/// <param name="IsTransfer">True marks it as a transfer (never spending).</param>
/// <param name="RulePattern">When set, also creates a merchant rule: any merchant containing this text goes to the category.</param>
/// <param name="OwnerShare">Your share of a purchase you split with your partner (0.5 = half each; 1 = not split). Null leaves it.</param>
/// <param name="PartnerPurpose">For money to or from your partner: "contribution" (hers, toward bills), "settleUp" (paying back),
/// or "myShare" (your share of something she bought: spending in the category you pick).</param>
public record UpdateTransactionRequest(Guid? CategoryId, bool IsTransfer, string? RulePattern, decimal? OwnerShare = null, string? PartnerPurpose = null);

public record UpdateTransactionResult(TransactionDto Transaction, int RuleAppliedTo);

public record CategoryDto(Guid Id, string Name, string Type, long MonthlyLimitCents, bool IsDiscretionary, int SortOrder, int PartnerSharePercent = 0);
public record RuleDto(Guid Id, string Pattern, Guid CategoryId, string CategoryName, DateTime CreatedUtc);

public static partial class TransactionEndpoints
{
    public const int InboxLimit = 500;

    public static void MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();

        // GET /api/transactions                 -> the current budget month
        // GET /api/transactions?from=..&to=..   -> any date range (inclusive)
        group.MapGet("/transactions", async (DateOnly? from, DateOnly? to, HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            if (from is null || to is null)
            {
                var h = await db.Households.AsNoTracking().SingleAsync(x => x.Id == householdId, ct);
                var month = new BudgetMonthCalendar(h.BudgetMonthStartDay).MonthContaining(HomeSummaryService.TodayIn(h.TimeZoneId, clock));
                from ??= month.Start;
                to ??= month.End;
            }
            var items = await Project(db.Transactions.Where(t => t.HouseholdId == householdId && t.Date >= from && t.Date <= to))
                .ToListAsync(ct);
            return Results.Ok(new TransactionListDto(from.Value, to.Value, items));
        });

        // Everything that affects spending but has no category yet, newest first (SPEC section 4.3).
        group.MapGet("/transactions/inbox", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await Project(Inbox(db, ctx.User.HouseholdId())).Take(InboxLimit).ToListAsync(ct));

        // Badge counts: uncategorized (all time) and unconfirmed automatic categories this budget month.
        group.MapGet("/transactions/inbox/count", async (HttpContext ctx, AppDbContext db, BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var (start, end) = await CurrentMonthAsync(householdId, db, months, ct);
            return new
            {
                count = await Inbox(db, householdId).CountAsync(ct),
                review = await Review(db, householdId).CountAsync(t => t.Date >= start && t.Date <= end, ct),
            };
        });

        // Unconfirmed automatic categories (every one waits for the user). scope=month (default): this budget
        // month, newest first. scope=earlier: before it, real guesses ("unsure") first.
        group.MapGet("/transactions/review", async (string? scope, HttpContext ctx, AppDbContext db, BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var (start, end) = await CurrentMonthAsync(householdId, db, months, ct);
            var q = Review(db, householdId);
            if (scope == "earlier")
                return await Project(q.Where(t => t.Date < start).OrderByDescending(t => t.CategoryUnsure)).Take(InboxLimit).ToListAsync(ct);
            return await Project(q.Where(t => t.Date >= start && t.Date <= end)).Take(InboxLimit).ToListAsync(ct);
        });

        group.MapGet("/transactions/review/summary", async (HttpContext ctx, AppDbContext db, BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var (start, end) = await CurrentMonthAsync(householdId, db, months, ct);
            var q = Review(db, householdId);
            var month = q.Where(t => t.Date >= start && t.Date <= end);
            var earlier = q.Where(t => t.Date < start);
            return new ReviewSummaryDto(
                new ReviewCounts(await month.CountAsync(ct), await month.CountAsync(t => !t.CategoryUnsure, ct), await month.CountAsync(t => t.CategoryUnsure, ct)),
                new ReviewCounts(await earlier.CountAsync(ct), await earlier.CountAsync(t => !t.CategoryUnsure, ct), await earlier.CountAsync(t => t.CategoryUnsure, ct)));
        });

        // Confirming makes the category the user's own (and teaches the categorizer that merchant).
        group.MapPost("/transactions/review/confirm", async (ConfirmRequest req, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            Results.Ok(new { confirmed = await Confirm(Review(db, ctx.User.HouseholdId()).Where(t => req.Ids.Contains(t.Id)), ct) }));

        // Bulk-confirm everything the categorizer was confident about (never the "unsure" ones), in one scope.
        group.MapPost("/transactions/review/confirm-likely", async (ConfirmLikelyRequest req, HttpContext ctx, AppDbContext db, BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var (start, end) = await CurrentMonthAsync(householdId, db, months, ct);
            var q = Review(db, householdId).Where(t => !t.CategoryUnsure);
            q = req.Scope switch
            {
                "earlier" => q.Where(t => t.Date < start),
                "month" => q.Where(t => t.Date >= start && t.Date <= end),
                _ => q,
            };
            return Results.Ok(new { confirmed = await Confirm(q, ct) });
        });

        group.MapPost("/transactions/auto-categorize", async (HttpContext ctx, CategorizationService svc, CancellationToken ct) =>
            await svc.RunAsync(ctx.User.HouseholdId(), ct));

        group.MapGet("/transactions/suggested-categories", async (HttpContext ctx, CategorizationService svc, CancellationToken ct) =>
            await svc.SuggestionsAsync(ctx.User.HouseholdId(), ct));

        group.MapPatch("/transactions/{id:guid}", async (Guid id, UpdateTransactionRequest req, HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var t = await db.Transactions.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (t is null)
                return Results.NotFound();
            if (req.CategoryId is { } cid && !await db.Categories.AnyAsync(c => c.Id == cid && c.HouseholdId == householdId, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["categoryId"] = ["Unknown category."] });

            if (req.OwnerShare is { } share && (share is <= 0m or > 1m || t.AmountCents < 0 && t.Kind != TransactionKind.Refund))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["ownerShare"] = ["Your share must be more than 0% and at most 100%, on a purchase or refund."] });

            // What money to or from the partner was for (DECISIONS.md 86).
            switch (req.PartnerPurpose)
            {
                case null:
                    break;
                case "contribution" when t.AmountCents < 0:
                case "settleUp":
                    t.Kind = req.PartnerPurpose == "contribution" ? TransactionKind.PartnerContribution : TransactionKind.PartnerSettleUp;
                    (t.CategoryId, t.CategorySource, t.NeedsReview, t.CategoryUnsure, t.IsTransfer, t.SplitFactor) =
                        (null, CategorySource.User, false, false, false, 1m);
                    t.CategoryReason = req.PartnerPurpose == "contribution" ? "Your partner's contribution toward bills." : "Paying back between you and your partner.";
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(new UpdateTransactionResult(await Project(db.Transactions.Where(x => x.Id == t.Id)).SingleAsync(ct), 0));
                case "myShare" when t.AmountCents > 0 && req.CategoryId is not null:
                    t.Kind = TransactionKind.Purchase; // your share of something she bought: spending in that category
                    break;
                default:
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["partnerPurpose"] = ["That doesn't fit this transaction."] });
            }

            t.CategoryId = req.CategoryId;
            t.CategorySource = CategorySource.User; // locks this row against rules and transfer detection
            t.NeedsReview = false;
            t.CategoryUnsure = false;
            t.CategoryReason = "You chose this.";
            if (req.OwnerShare is { } own)
                t.SplitFactor = own;
            if (t.Kind == TransactionKind.PartnerSettleUp && req.PartnerPurpose is null && req.CategoryId is not null)
                t.Kind = TransactionKind.Purchase; // filed in a category: it's spending after all
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

            var dto = await Project(db.Transactions.Where(x => x.Id == t.Id)).SingleAsync(ct);
            return Results.Ok(new UpdateTransactionResult(dto, applied));
        });

        group.MapGet("/budget/categories", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await db.Categories.Where(c => c.HouseholdId == ctx.User.HouseholdId() && !c.IsHidden)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .Select(c => new CategoryDto(c.Id, c.Name, c.Type.ToString(), c.MonthlyLimitCents, c.IsDiscretionary, c.SortOrder, c.PartnerSharePercent))
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
                                   && t.Kind != TransactionKind.PartnerSettleUp
                                   && (t.AmountCents > 0 || t.Kind == TransactionKind.Refund));

    private static Task<int> Confirm(IQueryable<Transaction> q, CancellationToken ct) =>
        q.ExecuteUpdateAsync(s => s.SetProperty(t => t.NeedsReview, false)
            .SetProperty(t => t.CategoryUnsure, false)
            .SetProperty(t => t.CategorySource, CategorySource.User)
            .SetProperty(t => t.CategoryReason, "You confirmed this."), ct);

    private static async Task<(DateOnly Start, DateOnly End)> CurrentMonthAsync(Guid householdId, AppDbContext db, BudgetMonthService months, CancellationToken ct)
    {
        var row = await months.EnsureCurrentAsync(householdId, ct);
        return (row.StartDate, row.EndDate);
    }

    /// <summary>Automatic categories the user hasn't confirmed yet.</summary>
    public static IQueryable<Transaction> Review(AppDbContext db, Guid householdId) =>
        db.Transactions.Where(t => t.HouseholdId == householdId && t.NeedsReview && !t.IsTransfer
            && (t.CategoryId != null || t.Kind == TransactionKind.PartnerContribution || t.Kind == TransactionKind.PartnerSettleUp));

    private static IQueryable<TransactionDto> Project(IQueryable<Transaction> q) =>
        q.OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedUtc).ThenBy(t => t.Id)
            .Select(t => new TransactionDto(
                t.Id, t.Date, t.AmountCents, t.Merchant, t.Description, t.Kind.ToString(),
                t.CategoryId, t.Category != null ? t.Category.Name : null, t.CategorySource.ToString(), t.IsTransfer, t.Pending,
                t.AccountId, t.Account.Name, t.BankCategory, t.NeedsReview, t.CategoryReason, t.CategoryUnsure, t.IsPartnerTransfer, t.SplitFactor));

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
