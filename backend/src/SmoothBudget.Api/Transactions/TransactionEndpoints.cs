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
    // Money to or from a friend; while it waits for a label, the category the app suggests (counted nowhere).
    bool IsPartnerTransfer = false, Guid? SuggestedCategoryId = null,
    // Which friend (DECISIONS.md 211).
    Guid? FriendId = null, string? FriendName = null,
    // What kind of income (DECISIONS.md 254): yours or, while it waits in Review, the most likely kind; null when not known.
    string? IncomeType = null,
    // A transfer's other account, or the savings account that paid interest (DECISIONS.md 259); the debt it paid or charged it.
    Guid? TransferAccountId = null, string? TransferAccountName = null, Guid? DebtAccountId = null, string? DebtAccountName = null);

/// <param name="As">"paycheck", "friend" (with <paramref name="FriendId"/> or <paramref name="NewFriendName"/>), "bonus" (with
/// <paramref name="ExpectedIncomeId"/> or <paramref name="NewBonusName"/>) or "other" (DECISIONS.md 259).</param>
public record MarkIncomeRequest(string As, Guid? FriendId = null, string? NewFriendName = null, Guid? ExpectedIncomeId = null, string? NewBonusName = null);

/// <summary>A transfer with <paramref name="AccountId"/>, or a hand-tracked loan <paramref name="LoanId"/>, or (neither) an account
/// not in the app; <paramref name="GoalId"/> is the savings goal for money out to savings (DECISIONS.md 259).</summary>
public record TransferRequest(Guid? AccountId, Guid? LoanId = null, Guid? GoalId = null);
/// <summary>Interest charged by a card or loan (<paramref name="DebtId"/>) or paid by a savings account (<paramref name="AccountId"/>).</summary>
public record InterestRequest(Guid? DebtId, Guid? AccountId);
public record IdsRequest(List<Guid> Ids);
public record TransferResultDto(TransactionDto Transaction, bool OtherHalfFound);

/// <summary>Everything the Review card's dropdowns offer (DECISIONS.md 259).</summary>
public record ChoiceAccountDto(Guid Id, string Name, string Type, Guid? DebtId, IReadOnlyList<ChoiceDto> Goals);
public record ChoiceDto(Guid Id, string Name);
public record ChoiceBonusDto(Guid Id, string Name, long AmountCents, DateOnly ExpectedDate, Guid? DepositTransactionId);
public record ChoicesDto(IReadOnlyList<ChoiceAccountDto> Accounts, IReadOnlyList<ChoiceDto> Loans, IReadOnlyList<ChoiceDto> Debts,
    IReadOnlyList<ChoiceDto> Friends, IReadOnlyList<ChoiceBonusDto> Bonuses);

public record ConfirmRequest(List<Guid> Ids);
public record ConfirmLikelyRequest(string Scope);
public record ReviewCounts(int Total, int Likely, int Unsure);
/// <param name="Earlier">Before this month, inside the months the budget is built from (from <paramref name="WindowStart"/>).</param>
/// <param name="Older">Before <paramref name="WindowStart"/>: doesn't affect your budget.</param>
public record ReviewSummaryDto(ReviewCounts Month, ReviewCounts Earlier, ReviewCounts Older, DateOnly MonthStart, DateOnly WindowStart);
public record MerchantHistoryDto(string CategoryName, int Count);
public record TransactionContextDto(IReadOnlyList<MerchantHistoryDto> History);

/// <summary>Where debt and savings money can go, in review (DECISIONS.md 227, 228): your debts, and your savings goals (your savings accounts only when you have no goals).</summary>
public record TowardChoiceDto(Guid Id, string Name, Guid? AccountId = null);
public record TowardChoicesDto(IReadOnlyList<TowardChoiceDto> Debts, IReadOnlyList<TowardChoiceDto> Savings);
/// <param name="Kind">"debt" or "savings".</param>
public record TowardRequest(string Kind, Guid Id);
public record TowardResultDto(string TowardName, string CategoryName);

/// <param name="Label">The budget month shown ("October 2026"), when one was asked for by how far back it is.</param>
public record TransactionListDto(DateOnly From, DateOnly To, IReadOnlyList<TransactionDto> Items, string? Label = null);

/// <param name="CategoryId">The category to use; null leaves it uncategorized.</param>
/// <param name="IsTransfer">True marks it as a transfer (never spending).</param>
/// <param name="RulePattern">Ignored: merchant rules are gone (DECISIONS.md 259).</param>
/// <param name="PartnerPurpose">"contribution" for money from your partner that's her contribution or just money from her. To say it paid you
/// back for something, send the category instead (DECISIONS.md 100).</param>
public record UpdateTransactionRequest(Guid? CategoryId, bool IsTransfer, string? RulePattern, string? PartnerPurpose = null);

/// <param name="Total">How many matched, even beyond the ones listed.</param>
/// <param name="NetCents">What the matches add up to: money out minus refunds, leaving out transfers and money coming in.</param>
public record SearchResultDto(IReadOnlyList<TransactionDto> Items, int Total, long NetCents);

/// <param name="BillChargesMoved">Filed under a bill: that bill's other charges that moved there too (DECISIONS.md 158).</param>
public record UpdateTransactionResult(TransactionDto Transaction, int RuleAppliedTo, int BillChargesMoved = 0);

public record CategoryDto(Guid Id, string Name, string Type, long MonthlyLimitCents, CategoryPriority Priority, int SortOrder);

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

        // The newest few, any month, for Home (DECISIONS.md 171).
        group.MapGet("/transactions/recent", async (int? count, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await Project(db.Transactions.Where(t => t.HouseholdId == ctx.User.HouseholdId())).Take(Math.Clamp(count ?? 5, 1, 20)).ToListAsync(ct));

        // Search every transaction (DECISIONS.md 150, 164). Every filter you set applies at once: words (store, description
        // or category; a number like "12.50" also finds that amount), one category, an amount range, and a date range.
        // Newest first, at most SearchLimit, with how many matched in all and what they add up to.
        group.MapGet("/transactions/search", async (string? q, Guid? categoryId, decimal? min, decimal? max, DateOnly? from, DateOnly? to,
            string? type, Guid? accountId, string? accountType, bool? unconfirmed, Guid? debtId,
            HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var text = (q ?? "").Trim();
            if (text.Length > 100 || min < 0 || max < 0 || (min is { } lo && max is { } hi && lo > hi) || (from is { } f && to is { } t && f > t))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["search"] = ["Check the filters: a low amount or date can't be after the high one."] });
            if (type is not (null or "" or "spending" or "bill" or "income" or "transfer" or "interest" or "debt" or "savings"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["Use spending, bill, income, transfer, interest, debt or savings."] });
            var kindOfAccount = Accounts.AccountBalances.TypeOf(accountType);
            var words = text.Length >= 2 ? text : null;
            if (words is null && categoryId is null && min is null && max is null && from is null && to is null
                && string.IsNullOrEmpty(type) && accountId is null && kindOfAccount is null && unconfirmed != true && debtId is null)
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
            if (accountId is { } acct) matches = matches.Where(x => x.AccountId == acct);
            if (kindOfAccount is { } ak) matches = matches.Where(x => x.Account.Type == ak);
            if (debtId is { } debt) matches = matches.Where(x => x.DebtAccountId == debt);
            if (unconfirmed == true) matches = matches.Where(x => x.NeedsReview || (x.CategoryId == null && !x.IsTransfer && x.Kind != TransactionKind.Income
                && x.Kind != TransactionKind.PartnerContribution && x.Kind != TransactionKind.CardPayment && x.Kind != TransactionKind.Transfer
                && x.Kind != TransactionKind.PartnerTransfer && x.Kind != TransactionKind.Interest && (x.AmountCents > 0 || x.Kind == TransactionKind.Refund)));
            matches = type switch
            {
                "spending" => matches.Where(x => !x.IsTransfer && x.Kind != TransactionKind.Income && x.Kind != TransactionKind.PartnerContribution
                    && x.Kind != TransactionKind.Interest && (x.Category == null ? x.AmountCents > 0 || x.Kind == TransactionKind.Refund : x.Category.Type == CategoryType.Variable)),
                "bill" => matches.Where(x => !x.IsTransfer && x.Category != null && x.Category.Type == CategoryType.FixedBill && x.Category.Name != CardInterest.CategoryName),
                "income" => matches.Where(x => !x.IsTransfer && (x.Kind == TransactionKind.Income || x.Kind == TransactionKind.PartnerContribution)),
                "transfer" => matches.Where(x => x.IsTransfer),
                "interest" => matches.Where(x => x.Kind == TransactionKind.Interest || (x.Category != null && x.Category.Name == CardInterest.CategoryName)),
                // Payments to cards and loans: filed as a debt payment, or a transfer to or from a card (its card-side half too).
                "debt" => matches.Where(x => (x.Category != null && x.Category.Type == CategoryType.DebtPayment)
                    || (x.IsTransfer && (x.Account.Type == AccountType.CreditCard || (x.TransferAccount != null && x.TransferAccount.Type == AccountType.CreditCard)))),
                "savings" => matches.Where(x => (x.Category != null && x.Category.Type == CategoryType.SavingsTransfer)
                    || (x.IsTransfer && (x.Account.Type == AccountType.Savings || (x.TransferAccount != null && x.TransferAccount.Type == AccountType.Savings)) && x.CategoryId == null)),
                _ => matches,
            };

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

        // "Use most likely" (DECISIONS.md 259): settle these as the app has them, the way you'd confirm each one.
        group.MapPost("/transactions/most-likely", async (IdsRequest req, HttpContext ctx, AppDbContext db, CategorizationService categorization,
            Payday.PaydayService payday, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var rows = await Todo(db, householdId).Where(t => req.Ids.Contains(t.Id)).ToListAsync(ct);
            return Results.Ok(new { confirmed = await UseMostLikelyAsync(db, householdId, rows, categorization, payday, ct) });
        });

        // "Use most likely for all", in one scope of Review (DECISIONS.md 259).
        group.MapPost("/transactions/review/most-likely", async (ConfirmLikelyRequest req, HttpContext ctx, AppDbContext db, BudgetMonthService months,
            CategorizationService categorization, Payday.PaydayService payday, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var w = await WindowAsync(householdId, months, ct);
            var rows = await InScope(Todo(db, householdId), req.Scope, w).ToListAsync(ct);
            return Results.Ok(new { confirmed = await UseMostLikelyAsync(db, householdId, rows, categorization, payday, ct) });
        });

        // Everything the Review card's dropdowns offer (DECISIONS.md 259): your accounts (with each card's debt and each
        // savings account's goals), loans you track by hand, every debt that can charge interest, friends, and bonuses.
        group.MapGet("/transactions/choices", async (HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var debts = await db.DebtAccounts.AsNoTracking().Where(d => d.HouseholdId == householdId && !d.IsHidden)
                .OrderByDescending(d => d.BalanceCents).Select(d => new { d.Id, d.Name, d.AccountId, Mask = d.Account != null ? d.Account.Mask : null, Nick = d.Account != null ? d.Account.Nickname : null }).ToListAsync(ct);
            var goals = await db.SavingsGoals.AsNoTracking().Where(g => g.HouseholdId == householdId && g.AccountId != null)
                .OrderBy(g => g.SortOrder).ThenBy(g => g.Name).Select(g => new { g.Id, g.Name, g.AccountId }).ToListAsync(ct);
            var accounts = await db.Accounts.AsNoTracking().Where(a => a.HouseholdId == householdId)
                .OrderBy(a => a.Type).ThenByDescending(a => a.IncludedInBudget).ThenBy(a => a.Name).ToListAsync(ct);
            var friends = await db.Friends.AsNoTracking().Where(f => f.HouseholdId == householdId).OrderBy(f => f.Name)
                .Select(f => new ChoiceDto(f.Id, f.Name)).ToListAsync(ct);
            var since = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-Payday.ExpectedIncomes.ShownDays);
            var bonuses = await db.ExpectedIncomes.AsNoTracking().Where(x => x.HouseholdId == householdId && x.ExpectedDate >= since)
                .OrderBy(x => x.ExpectedDate).Select(x => new ChoiceBonusDto(x.Id, x.Name, x.AmountCents, x.ExpectedDate, x.DepositTransactionId)).ToListAsync(ct);
            static string Kind(AccountType t) => t switch { AccountType.Savings => "savings", AccountType.CreditCard => "card", _ => "checking" };
            return new ChoicesDto(
                accounts.Select(a => new ChoiceAccountDto(a.Id, TransferFiling.Label(a.Name, a.Mask, a.Nickname), Kind(a.Type), debts.FirstOrDefault(d => d.AccountId == a.Id)?.Id,
                    goals.Where(g => g.AccountId == a.Id).Select(g => new ChoiceDto(g.Id, g.Name)).ToList())).ToList(),
                debts.Where(d => d.AccountId == null).Select(d => new ChoiceDto(d.Id, d.Name)).ToList(),
                debts.Select(d => new ChoiceDto(d.Id, TransferFiling.Label(d.Name, d.Mask, d.Nick))).ToList(),
                friends, bonuses);
        });

        // A transfer between your accounts (DECISIONS.md 259): where it went, or where it came from. Its other half, already
        // here on that account, is filed with it and leaves Review; one that hasn't come in yet will be when it does.
        group.MapPost("/transactions/{id:guid}/transfer", async (Guid id, TransferRequest req, HttpContext ctx, AppDbContext db, Payday.PaydayService payday, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var t = await db.Transactions.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (t is null) return Results.NotFound();
            if (req.AccountId is { } aid && (aid == t.AccountId || !await db.Accounts.AnyAsync(a => a.Id == aid && a.HouseholdId == householdId, ct)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["accountId"] = ["Pick one of your other accounts."] });
            if (req.LoanId is { } lid && !await db.DebtAccounts.AnyAsync(d => d.Id == lid && d.HouseholdId == householdId && d.AccountId == null, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["loanId"] = ["Unknown loan."] });
            if (req.GoalId is { } gid && !await db.SavingsGoals.AnyAsync(g => g.Id == gid && g.HouseholdId == householdId, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["goalId"] = ["Unknown savings goal."] });

            // Paired with a row somewhere else before: that one goes back to Review.
            if (t.TransferGroupId is { } g && await db.Transactions.AnyAsync(o => o.TransferGroupId == g && o.Id != t.Id && o.AccountId != req.AccountId, ct))
                await TransferFiling.UnpairAsync(db, t, ct);
            var name = await new TransferFiling(db, householdId).FileAsync(t, req.AccountId, req.LoanId, req.GoalId, create: true, ct);
            (t.CategorySource, t.NeedsReview, t.CategoryUnsure) = (CategorySource.User, false, false);
            t.CategoryReason = Clip($"Transfer {(t.AmountCents > 0 ? "to" : "from")} {name}: you said so.");
            await db.SaveChangesAsync(ct);
            var found = req.AccountId is not null && await TransferFiling.CompleteAsync(db, householdId, ct, t.Id) > 0
                        || (t.TransferGroupId is { } tg && await db.Transactions.AnyAsync(o => o.TransferGroupId == tg && o.Id != t.Id, ct));
            await payday.MatchDepositsAsync(householdId, ct); // a deposit that was a paycheck or a bonus lets go of it
            return Results.Ok(new TransferResultDto(await Project(db.Transactions.Where(x => x.Id == t.Id)).SingleAsync(ct), found));
        });

        // Interest (DECISIONS.md 259): charged by a card or loan (Card interest, tied to that debt, DECISIONS.md 232), or paid
        // by a savings account (counted nowhere: not income, not spending).
        group.MapPost("/transactions/{id:guid}/interest", async (Guid id, InterestRequest req, HttpContext ctx, AppDbContext db,
            Debt.CardInterestService interestService, Payday.PaydayService payday, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var t = await db.Transactions.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (t is null) return Results.NotFound();
            var debt = req.DebtId is { } did ? await db.DebtAccounts.Include(d => d.Account).SingleOrDefaultAsync(d => d.Id == did && d.HouseholdId == householdId, ct) : null;
            var savings = req.AccountId is { } sid ? await db.Accounts.SingleOrDefaultAsync(a => a.Id == sid && a.HouseholdId == householdId && a.Type == AccountType.Savings, ct) : null;
            if ((debt is null) == (savings is null))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["debtId"] = ["Pick the card, loan or savings account."] });

            await TransferFiling.UnpairAsync(db, t, ct);
            (t.IsTransfer, t.IncomeType, t.FriendId, t.IsPartnerTransfer, t.SuggestedCategoryId, t.CountsForMonthStart) = (false, null, null, false, null, null);
            (t.CategorySource, t.NeedsReview, t.CategoryUnsure) = (CategorySource.User, false, false);
            if (debt is not null)
            {
                var line = await interestService.LineAsync(householdId, ct);
                (t.CategoryId, t.DebtAccountId, t.TransferAccountId) = (line.Id, debt.Id, null);
                t.Kind = t.AmountCents > 0 ? TransactionKind.Fee : TransactionKind.Refund;
                t.CategoryReason = Clip($"Interest on {TransferFiling.Label(debt.Name, debt.Account?.Mask, debt.Account?.Nickname)}: you said so.");
            }
            else
            {
                (t.CategoryId, t.DebtAccountId, t.TransferAccountId, t.Kind) = (null, null, savings!.Id, TransactionKind.Interest);
                t.CategoryReason = Clip($"Interest from {TransferFiling.Label(savings.Name, savings.Mask, savings.Nickname)}: you said so.");
            }
            await db.SaveChangesAsync(ct);
            await payday.MatchDepositsAsync(householdId, ct);
            return Results.Ok(await Project(db.Transactions.Where(x => x.Id == t.Id)).SingleAsync(ct));
        });

        // "Apply to all past transactions from this merchant" (DECISIONS.md 259): every other one from the same merchant, the
        // same way in or out, that you haven't settled yourself, is filed the way you just filed this one. Not for transfers.
        group.MapPost("/transactions/{id:guid}/apply-to-merchant", async (Guid id, HttpContext ctx, AppDbContext db, CategorizationService categorization,
            Payday.PaydayService payday, Bills.RecurringService recurring, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var t = await db.Transactions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (t is null) return Results.NotFound();
            if (t.IsTransfer || t.CategorySource != CategorySource.User) return Results.Ok(new { applied = 0 });
            var moneyIn = t.AmountCents < 0;
            var others = await db.Transactions
                .Where(x => x.HouseholdId == householdId && x.Id != t.Id && x.Merchant == t.Merchant && (x.AmountCents < 0) == moneyIn
                            && x.CategorySource != CategorySource.User && x.CategorySource != CategorySource.BestGuess)
                .ToListAsync(ct);
            foreach (var x in others)
            {
                if (x.IsTransfer) await TransferFiling.UnpairAsync(db, x, ct);
                (x.Kind, x.CategoryId, x.IncomeType, x.FriendId, x.IsPartnerTransfer, x.DebtAccountId, x.TransferAccountId) =
                    (t.Kind, t.CategoryId, t.IncomeType, t.FriendId, t.IsPartnerTransfer, t.DebtAccountId, t.TransferAccountId);
                (x.IsTransfer, x.SuggestedCategoryId, x.CategorySource, x.NeedsReview, x.CategoryUnsure) = (false, null, CategorySource.User, false, false);
                x.CategoryReason = Clip($"Filed like {titleCase(t.Merchant)} on {t.Date:MMM d}: you applied it to this merchant.");
            }
            await db.SaveChangesAsync(ct);
            if (others.Count > 0)
            {
                await categorization.MatchContributionsAsync(householdId, ct);
                await payday.MatchDepositsAsync(householdId, ct);
                if (t.CategoryId is not null) await recurring.KeepBillsTogetherAsync(householdId, ct, t.Id);
            }
            return Results.Ok(new { applied = others.Count });
        });

        // One transaction, to open its edit pop-up from a list that shows less, like Past income (DECISIONS.md 258).
        group.MapGet("/transactions/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await Project(db.Transactions.Where(t => t.Id == id && t.HouseholdId == ctx.User.HouseholdId())).SingleOrDefaultAsync(ct) is { } t
                ? Results.Ok(t)
                : Results.NotFound());

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

        group.MapPatch("/transactions/{id:guid}", async (Guid id, UpdateTransactionRequest req, HttpContext ctx, AppDbContext db,
            Bills.RecurringService recurring, Payday.PaydayService payday, CancellationToken ct) =>
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
                t.IncomeType = null;
                if (req.PartnerPurpose != "contribution" || t.AmountCents >= 0)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["partnerPurpose"] = ["That doesn't fit this transaction."] });
                (t.Kind, t.CategoryId, t.SuggestedCategoryId, t.CategorySource, t.NeedsReview, t.CategoryUnsure, t.IsTransfer) =
                    (TransactionKind.PartnerContribution, null, null, CategorySource.User, false, false, false);
                t.CategoryReason = "Your friend's money: you said so.";
                await db.SaveChangesAsync(ct);
                return Results.Ok(new UpdateTransactionResult(await Project(db.Transactions.Where(x => x.Id == t.Id)).SingleAsync(ct), 0));
            }

            if (!req.IsTransfer && t.IsTransfer)
                await TransferFiling.UnpairAsync(db, t, ct);
            t.TransferAccountId = null;
            t.CategoryId = req.CategoryId;
            t.SuggestedCategoryId = null;
            t.IncomeType = null; // filed as something else, it's no longer the income you said (DECISIONS.md 254)
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

            // Nothing is left behind from how it was filed before (DECISIONS.md 251). The debt it was tied to goes unless it's
            // still on a debt line or Card interest. Filed under spending or a bill, it is what it's filed as: a purchase, or
            // (money in) a refund to that category; never also a card payment, your income or a friend's money in, which
            // would count it twice.
            var filedAs = req.CategoryId is { } filedId
                ? await db.Categories.Where(c => c.Id == filedId).Select(c => new { c.Name, c.Type }).SingleAsync(ct) : null;
            if (filedAs is null || (filedAs.Type != CategoryType.DebtPayment && !CardInterest.IsInterestCategory(filedAs.Name, filedAs.Type)))
                t.DebtAccountId = null;
            if (!t.IsTransfer && filedAs is { Type: CategoryType.Variable or CategoryType.FixedBill }
                && t.Kind is TransactionKind.CardPayment or TransactionKind.Transfer or TransactionKind.Income or TransactionKind.PartnerContribution or TransactionKind.Interest)
                t.Kind = t.AmountCents > 0 ? TransactionKind.Purchase : TransactionKind.Refund;
            await db.SaveChangesAsync(ct);
            await payday.MatchDepositsAsync(householdId, ct); // a deposit that was a paycheck or a bonus lets go of it

            // Filed under a bill: the bill's other charges go there too, so its budget line sees the whole bill (DECISIONS.md 158).
            var moved = req.CategoryId is { } billId && !req.IsTransfer
                        && await db.Categories.AnyAsync(c => c.Id == billId && c.Type == CategoryType.FixedBill, ct)
                ? await recurring.KeepBillsTogetherAsync(householdId, ct, t.Id) : 0;

            var dto = await Project(db.Transactions.Where(x => x.Id == t.Id)).SingleAsync(ct);
            return Results.Ok(new UpdateTransactionResult(dto, 0, moved));
        });

        // Money in is your income (DECISIONS.md 254): a paycheck, a friend's contribution, or other income like a bonus.
        // A paycheck counts as the check for its pay date; other income adds to the month's money; a friend's
        // contribution counts toward what they send. Whatever it was filed as before is cleared, so nothing counts twice.
        group.MapPost("/transactions/{id:guid}/income", async (Guid id, MarkIncomeRequest req, HttpContext ctx, AppDbContext db,
            CategorizationService categorization, Payday.PaydayService payday, Shared.SharedService shared, TimeProvider clock, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var t = await db.Transactions.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (t is null) return Results.NotFound();
            if (t.AmountCents >= 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["as"] = ["Only money in can be income."] });
            Friend? friend = null;
            ExpectedIncome? bonus = null;
            if (req.As == "friend")
            {
                if (req.FriendId is null && !string.IsNullOrWhiteSpace(req.NewFriendName))
                {
                    var (added, problem) = await shared.AddFriendAsync(householdId, req.NewFriendName, ct);
                    if (added is null)
                        return Results.ValidationProblem(new Dictionary<string, string[]> { ["newFriendName"] = [problem ?? "Check the name."] });
                    req = req with { FriendId = added.Id };
                }
                friend = req.FriendId is { } fid ? await db.Friends.SingleOrDefaultAsync(f => f.Id == fid && f.HouseholdId == householdId, ct) : null;
                if (friend is null)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["friendId"] = ["Pick which friend sent it."] });
            }
            else if (req.As == "bonus")
            {
                if (req.ExpectedIncomeId is { } xid)
                    bonus = await db.ExpectedIncomes.SingleOrDefaultAsync(x => x.Id == xid && x.HouseholdId == householdId, ct);
                else if (req.NewBonusName?.Trim() is { Length: > 0 and <= 100 } bonusName)
                    db.ExpectedIncomes.Add(bonus = new ExpectedIncome
                    {
                        Id = Guid.NewGuid(), HouseholdId = householdId, Name = bonusName, AmountCents = -t.AmountCents, ExpectedDate = t.Date,
                        CreatedUtc = clock.GetUtcNow().UtcDateTime,
                    });
                if (bonus is null)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["expectedIncomeId"] = ["Pick the bonus, or name a new one."] });
            }
            else if (req.As is not ("paycheck" or "other"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["as"] = ["Say paycheck, friend, bonus or other."] });

            // It's this bonus now, or no bonus at all (DECISIONS.md 259).
            await db.ExpectedIncomes.Where(x => x.HouseholdId == householdId && x.DepositTransactionId == t.Id && (bonus == null || x.Id != bonus.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DepositTransactionId, (Guid?)null), ct);
            if (bonus is not null) bonus.DepositTransactionId = t.Id;

            await TransferFiling.UnpairAsync(db, t, ct);
            (t.CategoryId, t.SuggestedCategoryId, t.CategorySource, t.NeedsReview, t.CategoryUnsure, t.DebtAccountId, t.TransferAccountId) =
                (null, null, CategorySource.User, false, false, null, null);
            if (t.IsTransfer) (t.IsTransfer, t.TransferGroupId) = (false, null);
            if (friend is not null)
            {
                (t.Kind, t.IncomeType, t.IsPartnerTransfer, t.FriendId) = (TransactionKind.PartnerContribution, IncomeType.Friend, true, friend.Id);
                t.CategoryReason = $"From {friend.Name}: you said so.";
            }
            else
            {
                (t.Kind, t.IncomeType, t.IsPartnerTransfer, t.FriendId, t.CountsForMonthStart) =
                    (TransactionKind.Income, req.As == "paycheck" ? IncomeType.Paycheck : IncomeType.Other, false, null, null);
                t.CategoryReason = Clip(req.As == "paycheck" ? "Your paycheck: you said so." : bonus is not null ? $"Your {bonus.Name}: you said so." : "Other income: you said so.");
            }
            await db.SaveChangesAsync(ct);
            // Which check or contribution it is, right away.
            await categorization.MatchContributionsAsync(householdId, ct);
            await payday.MatchDepositsAsync(householdId, ct);
            return Results.Ok(await Project(db.Transactions.Where(x => x.Id == t.Id)).SingleAsync(ct));
        });

        // Debt and savings money in review goes toward an account, not a category (DECISIONS.md 227).
        group.MapGet("/transactions/toward", async (HttpContext ctx, AppDbContext db, Savings.SavingsService savings, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            await savings.EnsureStarterAsync(householdId, ct); // the starter goal, as the Savings tab makes it
            var debts = await db.DebtAccounts.AsNoTracking()
                .Where(d => d.HouseholdId == householdId && !d.IsHidden && (d.AccountId == null || d.Account!.IncludedInBudget))
                .OrderByDescending(d => d.BalanceCents).Select(d => new { d.Id, d.Name, d.AccountId, Mask = d.Account != null ? d.Account.Mask : null, Nick = d.Account != null ? d.Account.Nickname : null }).ToListAsync(ct);
            var accounts = await db.Accounts.AsNoTracking()
                .Where(a => a.HouseholdId == householdId && a.Type == AccountType.Savings && a.IncludedInBudget)
                .OrderBy(a => a.Name).Select(a => new { a.Id, a.Name, a.Mask, a.Nickname }).ToListAsync(ct);
            var goals = await db.SavingsGoals.AsNoTracking().Where(g => g.HouseholdId == householdId)
                .OrderBy(g => g.SortOrder).ThenBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync(ct);
            static string Label(string name, string? mask, string? nick) => TransferFiling.Label(name, mask, nick);
            // Savings is your goals: the account it lands in is the same either way (DECISIONS.md 228).
            return new TowardChoicesDto(
                debts.Select(d => new TowardChoiceDto(d.Id, Label(d.Name, d.Mask, d.Nick), d.AccountId)).ToList(),
                goals.Count > 0
                    ? goals.Select(g => new TowardChoiceDto(g.Id, g.Name)).ToList()
                    : accounts.Select(a => new TowardChoiceDto(a.Id, Label(a.Name, a.Mask, a.Nickname))).ToList());
        });

        // It went toward this debt or savings: filed under the debt or savings line for it (the one named after it, else
        // your first, else a new one), with where it went as the reason (DECISIONS.md 227).
        group.MapPost("/transactions/{id:guid}/toward", async (Guid id, TowardRequest req, HttpContext ctx, AppDbContext db, Debt.CardInterestService interestService, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var t = await db.Transactions.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (t is null) return Results.NotFound();
            // Interest charged on a card or loan is a debt line too (DECISIONS.md 232): it goes to Card interest, tied to that debt.
            var interest = req.Kind == "interest";
            var type = req.Kind switch { "debt" or "interest" => CategoryType.DebtPayment, "savings" => CategoryType.SavingsTransfer, _ => (CategoryType?)null };
            if (type is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Debt, interest or savings."] });

            // The name to show, and names a line for it could have (a goal tied to a savings account counts as that account's).
            string? toward = null;
            var names = new List<string>();
            Guid? debtId = null;
            if (type == CategoryType.DebtPayment)
            {
                var d = await db.DebtAccounts.AsNoTracking().Where(x => x.Id == req.Id && x.HouseholdId == householdId)
                    .Select(x => new { x.Id, x.Name, Mask = x.Account != null ? x.Account.Mask : null, Nick = x.Account != null ? x.Account.Nickname : null }).SingleOrDefaultAsync(ct);
                if (d is not null) (toward, names, debtId) = (TransferFiling.Label(d.Name, d.Mask, d.Nick), [d.Name], d.Id);
            }
            else if (await db.Accounts.AsNoTracking().Where(a => a.Id == req.Id && a.HouseholdId == householdId && a.Type == AccountType.Savings)
                         .Select(a => new { a.Name, a.Mask, a.Nickname }).SingleOrDefaultAsync(ct) is { } a)
            {
                toward = TransferFiling.Label(a.Name, a.Mask, a.Nickname);
                names = [a.Name, .. await db.SavingsGoals.AsNoTracking().Where(g => g.AccountId == req.Id).OrderBy(g => g.SortOrder).Select(g => g.Name).ToListAsync(ct)];
            }
            else if (await db.SavingsGoals.AsNoTracking().Where(g => g.Id == req.Id && g.HouseholdId == householdId).Select(g => g.Name).SingleOrDefaultAsync(ct) is { } goal)
                (toward, names) = (goal, [goal]);
            if (toward is null) return Results.NotFound();

            if (interest)
            {
                var interestCat = await interestService.LineAsync(householdId, ct);
                (t.CategoryId, t.SuggestedCategoryId, t.CategorySource, t.NeedsReview, t.CategoryUnsure) = (interestCat.Id, null, CategorySource.User, false, false);
                t.DebtAccountId = debtId;
                t.CategoryReason = $"Interest on {toward}: you said so.";
                if (t.Kind == TransactionKind.PartnerTransfer) t.Kind = TransactionKind.Purchase;
                await db.SaveChangesAsync(ct);
                return Results.Ok(new TowardResultDto(toward, interestCat.Name));
            }

            var lines = await db.Categories.Where(c => c.HouseholdId == householdId && c.Type == type).OrderBy(c => c.IsHidden).ThenBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
            var cat = names.Select(n => lines.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(c => c is not null)
                      ?? lines.FirstOrDefault(c => !c.IsHidden);
            if (cat is null)
            {
                var name = type == CategoryType.DebtPayment ? "Debt payments" : "Savings";
                var all = await db.Categories.Where(c => c.HouseholdId == householdId).Select(c => new { c.Name, c.SortOrder }).ToListAsync(ct);
                for (var n = 2; all.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
                    name = $"{(type == CategoryType.DebtPayment ? "Debt payments" : "Savings")} ({n})";
                db.Categories.Add(cat = new Category { Id = Guid.NewGuid(), HouseholdId = householdId, Name = name, Type = type.Value, SortOrder = all.Count == 0 ? 0 : all.Max(c => c.SortOrder) + 1 });
            }
            cat.IsHidden = false;

            (t.CategoryId, t.SuggestedCategoryId, t.CategorySource, t.NeedsReview, t.CategoryUnsure) = (cat.Id, null, CategorySource.User, false, false);
            t.DebtAccountId = debtId;
            t.CategoryReason = $"Toward {toward}: you said so.";
            if (t.Kind == TransactionKind.PartnerTransfer)
                t.Kind = t.AmountCents < 0 ? TransactionKind.Refund : TransactionKind.Purchase;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new TowardResultDto(toward, cat.Name));
        });

        group.MapGet("/budget/categories", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await db.Categories.Where(c => c.HouseholdId == ctx.User.HouseholdId() && !c.IsHidden)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .Select(c => new CategoryDto(c.Id, c.Name, c.Type.ToString(), c.MonthlyLimitCents, c.Priority, c.SortOrder))
                .ToListAsync(ct));

    }

    private static string Clip(string s) => s.Length <= 200 ? s : s[..200];

    private static string titleCase(string s) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

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
                                   && t.Kind != TransactionKind.Interest
                                   && (t.AmountCents > 0 || t.Kind == TransactionKind.Refund));

    /// <summary>Confirms automatic categories. A transfer with your partner that's still waiting needs a label, not a ✓, so it's skipped.</summary>
    private static Task<int> Confirm(IQueryable<Transaction> q, CancellationToken ct) =>
        q.Where(t => t.Kind != TransactionKind.PartnerTransfer).ExecuteUpdateAsync(s => s.SetProperty(t => t.NeedsReview, false)
            .SetProperty(t => t.CategoryUnsure, false)
            .SetProperty(t => t.CategorySource, CategorySource.User)
            .SetProperty(t => t.CategoryReason, "You confirmed this."), ct);

    /// <summary>
    /// "Use most likely" (DECISIONS.md 259): each row is settled as the app has it, like confirming it yourself. Where the app
    /// has no answer, the old best guess fills in (DECISIONS.md 102): money from a friend is their money, or pays back the
    /// category it looks like; income with no kind is other income; spending with no category goes under "Other". A
    /// transfer's other half follows it, and paychecks and bonuses are matched again. Returns how many it settled.
    /// </summary>
    public static async Task<int> UseMostLikelyAsync(AppDbContext db, Guid householdId, List<Transaction> rows, CategorizationService categorization,
        Payday.PaydayService payday, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        Guid? otherId = null;
        async Task<Guid> OtherAsync()
        {
            if (otherId is { } id) return id;
            var other = await db.Categories.FirstOrDefaultAsync(c => c.HouseholdId == householdId && c.Name.ToLower() == "other", ct);
            if (other is null)
            {
                var order = await db.Categories.Where(c => c.HouseholdId == householdId).MaxAsync(c => (int?)c.SortOrder, ct) ?? -1;
                db.Categories.Add(other = new Category { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Other", Type = CategoryType.Variable, MonthlyLimitCents = 0, SortOrder = order + 1 });
            }
            other.IsHidden = false;
            otherId = other.Id;
            return other.Id;
        }

        foreach (var t in rows)
        {
            if (t.Kind == TransactionKind.PartnerTransfer && t.AmountCents < 0)
                (t.Kind, t.CategoryId, t.IncomeType) = t.SuggestedCategoryId is { } paid
                    ? (TransactionKind.Refund, (Guid?)paid, (IncomeType?)null)
                    : (TransactionKind.PartnerContribution, (Guid?)null, (IncomeType?)IncomeType.Friend);
            else if (t.Kind == TransactionKind.PartnerTransfer)
                (t.Kind, t.CategoryId) = (TransactionKind.Purchase, t.SuggestedCategoryId ?? await OtherAsync());
            else if (t.Kind == TransactionKind.PartnerContribution)
                t.IncomeType = IncomeType.Friend;
            else if (t.Kind == TransactionKind.Income && !t.IsTransfer)
                t.IncomeType ??= IncomeType.Other;
            else if (TransactionRules.NeedsCategory(t.CategoryId is not null, t.IsTransfer, t.Kind, t.AmountCents))
                t.CategoryId = await OtherAsync();
            (t.CategorySource, t.NeedsReview, t.CategoryUnsure, t.SuggestedCategoryId) = (CategorySource.User, false, false, null);
            t.CategoryReason = "You confirmed the most likely.";
        }
        await db.SaveChangesAsync(ct);
        await TransferFiling.CompleteAsync(db, householdId, ct);
        await categorization.MatchContributionsAsync(householdId, ct);
        await payday.MatchDepositsAsync(householdId, ct);
        return rows.Count;
    }

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

    /// <summary>
    /// Everything waiting for you (DECISIONS.md 259): every transaction the app filed that you haven't confirmed (spending,
    /// bills, income, transfers, interest), and purchases with no category at all.
    /// </summary>
    public static IQueryable<Transaction> Todo(AppDbContext db, Guid householdId) =>
        db.Transactions.Where(t => t.HouseholdId == householdId
            && (t.NeedsReview
                || (t.CategoryId == null && !t.IsTransfer && t.Kind != TransactionKind.Income && t.Kind != TransactionKind.PartnerContribution
                    && t.Kind != TransactionKind.CardPayment && t.Kind != TransactionKind.Transfer && t.Kind != TransactionKind.PartnerTransfer
                    && t.Kind != TransactionKind.Interest && (t.AmountCents > 0 || t.Kind == TransactionKind.Refund))));

    private static IQueryable<Transaction> InScope(IQueryable<Transaction> q, string scope, ReviewWindow w) => scope switch
    {
        "earlier" => q.Where(t => t.Date < w.MonthStart && t.Date >= w.WindowStart),
        "older" => q.Where(t => t.Date < w.WindowStart),
        "all" => q.Where(t => t.Date < w.MonthStart),
        _ => q.Where(t => t.Date >= w.MonthStart && t.Date <= w.MonthEnd),
    };

    /// <summary>Everything the app filed that you haven't confirmed yet (DECISIONS.md 259).</summary>
    public static IQueryable<Transaction> Review(AppDbContext db, Guid householdId) =>
        db.Transactions.Where(t => t.HouseholdId == householdId && t.NeedsReview);

    public static IQueryable<TransactionDto> Project(IQueryable<Transaction> q) =>
        q.OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedUtc).ThenBy(t => t.Id)
            .Select(t => new TransactionDto(
                t.Id, t.Date, t.AmountCents, t.Merchant, t.Description, t.Kind.ToString(),
                t.CategoryId, t.Category != null ? t.Category.Name : null, t.CategorySource.ToString(), t.IsTransfer, t.Pending,
                t.AccountId, t.Account.Nickname ?? t.Account.Name, t.BankCategory, t.NeedsReview, t.CategoryReason, t.CategoryUnsure, t.IsPartnerTransfer, t.SuggestedCategoryId,
                t.FriendId, t.Friend != null ? t.Friend.Name : null,
                t.IncomeType == IncomeType.Paycheck ? "paycheck" : t.IncomeType == IncomeType.Other ? "other" : t.IncomeType == IncomeType.Friend ? "friend" : null,
                t.TransferAccountId, t.TransferAccount != null ? (t.TransferAccount.Nickname != null ? t.TransferAccount.Nickname : t.TransferAccount.Mask != null ? t.TransferAccount.Name + " ••" + t.TransferAccount.Mask : t.TransferAccount.Name) : null,
                t.DebtAccountId, t.DebtAccount != null ? (t.DebtAccount.Account != null && t.DebtAccount.Account.Nickname != null ? t.DebtAccount.Account.Nickname : t.DebtAccount.Name) : null));
}
