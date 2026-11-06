using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
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
    bool NeedsReview = false, string? CategoryReason = null);

public record ConfirmRequest(List<Guid> Ids);

public record TransactionListDto(DateOnly From, DateOnly To, IReadOnlyList<TransactionDto> Items);

/// <param name="CategoryId">The category to use; null leaves it uncategorized.</param>
/// <param name="IsTransfer">True marks it as a transfer (never spending).</param>
/// <param name="RulePattern">When set, also creates a merchant rule: any merchant containing this text goes to the category.</param>
public record UpdateTransactionRequest(Guid? CategoryId, bool IsTransfer, string? RulePattern);

public record UpdateTransactionResult(TransactionDto Transaction, int RuleAppliedTo);

public record CategoryDto(Guid Id, string Name, string Type, long MonthlyLimitCents, bool IsDiscretionary, int SortOrder);
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

        group.MapGet("/transactions/inbox/count", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            new { count = await Inbox(db, ctx.User.HouseholdId()).CountAsync(ct), review = await Review(db, ctx.User.HouseholdId()).CountAsync(ct) });

        // Automatic guesses to confirm (filed and counted, but not certain), newest first.
        group.MapGet("/transactions/review", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await Project(Review(db, ctx.User.HouseholdId())).Take(InboxLimit).ToListAsync(ct));

        // "Looks right": confirming guesses makes them the user's own choice (and teaches the categorizer).
        group.MapPost("/transactions/review/confirm", async (ConfirmRequest req, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var n = await db.Transactions
                .Where(t => t.HouseholdId == ctx.User.HouseholdId() && req.Ids.Contains(t.Id) && t.NeedsReview && t.CategoryId != null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.NeedsReview, false)
                    .SetProperty(t => t.CategorySource, CategorySource.User)
                    .SetProperty(t => t.CategoryReason, "You confirmed this."), ct);
            return Results.Ok(new { confirmed = n });
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

            t.CategoryId = req.CategoryId;
            t.CategorySource = CategorySource.User; // locks this row against rules and transfer detection
            t.NeedsReview = false;
            t.CategoryReason = "You chose this.";
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
                        .SetProperty(x => x.NeedsReview, false)
                        .SetProperty(x => x.CategoryReason, "Your rule for \"" + pattern + "\"."), ct);
            }
            await db.SaveChangesAsync(ct);

            var dto = await Project(db.Transactions.Where(x => x.Id == t.Id)).SingleAsync(ct);
            return Results.Ok(new UpdateTransactionResult(dto, applied));
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
                                   && (t.AmountCents > 0 || t.Kind == TransactionKind.Refund));

    /// <summary>Automatic guesses the user hasn't confirmed yet.</summary>
    public static IQueryable<Transaction> Review(AppDbContext db, Guid householdId) =>
        db.Transactions.Where(t => t.HouseholdId == householdId && t.NeedsReview && t.CategoryId != null && !t.IsTransfer);

    private static IQueryable<TransactionDto> Project(IQueryable<Transaction> q) =>
        q.OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedUtc).ThenBy(t => t.Id)
            .Select(t => new TransactionDto(
                t.Id, t.Date, t.AmountCents, t.Merchant, t.Description, t.Kind.ToString(),
                t.CategoryId, t.Category != null ? t.Category.Name : null, t.CategorySource.ToString(), t.IsTransfer, t.Pending,
                t.AccountId, t.Account.Name, t.BankCategory, t.NeedsReview, t.CategoryReason));

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
