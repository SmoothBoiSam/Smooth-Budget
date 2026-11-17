using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Transactions;

/// <summary>A friend whose transfers are tracked (DECISIONS.md 211): their name in the app, the name on their transfers, and when labeling started.</summary>
public sealed record FriendTransfers(Guid Id, string Name, string TransferName, DateOnly? LabelsStartAfter);

/// <summary>What categorization needs about one household, loaded once per import, sync or back-fill.</summary>
public sealed class CategorizationContext(
    IReadOnlyDictionary<string, Guid> categoryIdsByName,
    IReadOnlyDictionary<string, MerchantHistory> history,
    IReadOnlyList<FriendTransfers> friends,
    IReadOnlyList<RecentPurchase>? recentPurchases = null)
{
    public IReadOnlyDictionary<string, Guid> CategoryIdsByName { get; } = categoryIdsByName;

    public CategoryGuess Guess(Transaction t) => AutoCategorizer.Guess(
        new CategorizationInput(t.Merchant, t.BankCategory, t.BankCategoryPrimary, t.BankConfidence, t.Description),
        history.GetValueOrDefault(t.Merchant), CategoryIdsByName);

    /// <summary>
    /// Applies automatic decisions to one row: recognizes money to and from a friend (by the name on their transfers), files
    /// purchases in a category, and tells income from partner money. Never touches what the user chose, or transfers.
    /// Every automatic category waits for the user (DECISIONS.md 80). Money between you and your partner waits too,
    /// counted nowhere, with the app's suggestion, until you say what it was for (DECISIONS.md 100); her fixed
    /// contribution is matched separately (<see cref="CategorizationService.MatchContributionsAsync"/>).
    /// </summary>
    public void Apply(Transaction t)
    {
        if (t.IsTransfer)
            return;
        // Income you said what it is (DECISIONS.md 254): a friend's contribution stays theirs even when the transfer doesn't
        // carry their name, and a paycheck or other income stays yours even when it does.
        if (!TransactionRules.IsAutomatic(t.CategorySource) && t.AmountCents < 0)
        {
            if (t.IncomeType == IncomeType.Friend && t.Kind == TransactionKind.PartnerContribution && t.FriendId is not null)
            {
                t.IsPartnerTransfer = true;
                return;
            }
            if (t.IncomeType is not null && t.Kind == TransactionKind.Income)
            {
                (t.IsPartnerTransfer, t.FriendId) = (false, null);
                return;
            }
        }
        // Whose it is: the first friend whose transfer name it carries.
        var from = t.AmountCents < 0 && t.Kind is TransactionKind.Income or TransactionKind.PartnerContribution or TransactionKind.PartnerTransfer
            ? friends.FirstOrDefault(f => PartnerDetector.IsFromPartner(t.Description, t.AmountCents, f.TransferName)) : null;
        var to = t.AmountCents > 0 && t.Kind is TransactionKind.Purchase or TransactionKind.Other or TransactionKind.PartnerTransfer
            ? friends.FirstOrDefault(f => PartnerMoney.IsToPartner(t.Description, t.AmountCents, f.TransferName)) : null;
        var friend = from ?? to;
        var (fromPartner, toPartner) = (from is not null, to is not null);
        t.IsPartnerTransfer = friend is not null;
        t.FriendId = friend?.Id;

        if (!TransactionRules.IsAutomatic(t.CategorySource))
        {
            // You said what it was for; only a "contribution" from someone who's no longer the partner reverts.
            if (t.AmountCents < 0 && !fromPartner && t.Kind == TransactionKind.PartnerContribution)
                t.Kind = TransactionKind.Income;
            return;
        }
        // Settled without being your own pick (DECISIONS.md 252): a rule you made with "always" settled what was already
        // here (224), and card interest is filed for you (159). Re-running leaves those as they are: interest stays on its
        // line, and the rest only come back to Review if the answer changes.
        var settled = !t.NeedsReview && t.CategoryId is not null && !t.IsPartnerTransfer;
        if (settled && CardInterest.IsCharge(t.Merchant, t.Description, t.BankCategory, t.AmountCents))
            return;
        t.SuggestedCategoryId = null;
        var labeling = friend?.LabelsStartAfter is { } start && t.Date > start; // before that, as it always was

        if (fromPartner)
        {
            if (labeling)
                Wait(t, PartnerMoney.Suggest(t.Date, t.AmountCents, recentPurchases ?? [], friend!.Name));
            else
                (t.Kind, t.CategoryId, t.CategorySource, t.NeedsReview, t.CategoryUnsure, t.CategoryReason) =
                    (TransactionKind.PartnerContribution, null, CategorySource.None, false, false, null);
            return;
        }
        // Interest a savings account paid you: most likely interest, counted nowhere (DECISIONS.md 259).
        if (t.AmountCents < 0 && t.Kind is TransactionKind.Income or TransactionKind.Interest && LooksLikeInterestEarned(t))
        {
            if (t.Kind != TransactionKind.Interest) t.NeedsReview = true;
            (t.Kind, t.CategoryId, t.CategorySource, t.CategoryUnsure, t.TransferAccountId, t.IncomeType) =
                (TransactionKind.Interest, null, CategorySource.None, false, t.AccountId, null);
            t.CategoryReason = "Looks like interest your account paid you.";
            return;
        }
        if (t.AmountCents < 0 && t.Kind is TransactionKind.Income or TransactionKind.PartnerContribution or TransactionKind.PartnerTransfer)
        {
            // Your income. New deposits wait in Review for you to say what they are (DECISIONS.md 254, see AskAboutIncome);
            // a re-run leaves that as it is, so the income already here doesn't all come back.
            if (t.Kind != TransactionKind.Income) t.NeedsReview = false;
            (t.Kind, t.CategoryId, t.CategorySource, t.CategoryUnsure, t.CategoryReason) =
                (TransactionKind.Income, null, CategorySource.None, false, null);
            return;
        }

        var guess = Guess(t);
        if (toPartner && labeling)
        {
            // Your share of something she bought, or something else: you say. What you chose for her before is a suggestion.
            var asked = PartnerMoney.Suggest(t.Date, t.AmountCents, [], friend!.Name);
            Wait(t, new PartnerSuggestion(guess.CategoryId, asked.Reason));
            return;
        }
        if (t.Kind == TransactionKind.PartnerTransfer)
            t.Kind = TransactionKind.Purchase; // no longer to the partner (the name changed)

        // Re-running only improves things: with no opinion now, keep a category that still exists.
        if (guess.CategoryId is null && t.CategoryId is { } current && CategoryIdsByName.Values.Contains(current))
        {
            if (!settled) t.NeedsReview = true; // still automatic, so still waiting for the user
            return;
        }
        var same = settled && guess.CategoryId == t.CategoryId;
        t.CategoryId = guess.CategoryId;
        t.CategorySource = guess.Source;
        // Every automatic category waits for the user to confirm it (DECISIONS.md 80); "unsure" only sorts the review list.
        t.NeedsReview = guess.CategoryId is not null && !same;
        t.CategoryUnsure = guess.CategoryId is not null && guess.Unsure && !same;
        t.CategoryReason = Clip(guess.Reason);
    }

    /// <summary>
    /// A new deposit that's your income waits in Review for you to say what it is: a paycheck, a friend's contribution or
    /// other income (DECISIONS.md 254). A paycheck the app matches to a pay date leaves Review by itself.
    /// </summary>
    public static void AskAboutIncome(Transaction t)
    {
        if (!t.IsTransfer && !t.IsPartnerTransfer && t.Kind == TransactionKind.Income && t.AmountCents < 0 && TransactionRules.IsAutomatic(t.CategorySource))
            t.NeedsReview = true;
    }

    private static bool LooksLikeInterestEarned(Transaction t) =>
        t.BankCategory is "INCOME_INTEREST_EARNED" || t.BankCategoryPrimary is "INCOME_INTEREST_EARNED"
        || System.Text.RegularExpressions.Regex.IsMatch($"{t.Merchant} {t.Description}", @"\bINTEREST (PAYMENT|PAID|EARNED|CREDIT)\b|\bINT(EREST)? PMT\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Money between you, waiting for a label: no category (so it counts nowhere), the suggestion pre-selected in Review.</summary>
    private static void Wait(Transaction t, PartnerSuggestion s)
    {
        (t.Kind, t.CategoryId, t.CategorySource, t.NeedsReview) = (TransactionKind.PartnerTransfer, null, CategorySource.None, true);
        (t.SuggestedCategoryId, t.CategoryUnsure, t.CategoryReason) = (s.CategoryId, s.CategoryId is null, Clip(s.Reason));
    }

    private static string Clip(string reason) => reason.Length <= 200 ? reason : reason[..200];
}

/// <summary>Filed / Guesses: automatic categories still waiting for the user (likely / unsure). Confirmed ones aren't counted.</summary>
public record AutoCategorizeResult(int Filed, int Guesses, int StillUncategorized, int PartnerContributions);

/// <summary>Cleared: categories wiped; Filed / Guesses / StillUncategorized: where they landed again; BankNotes: banks that couldn't be reached.</summary>
public record ResetCategoriesResult(int Cleared, int Filed, int Guesses, int StillUncategorized, IReadOnlyList<string> BankNotes);

public record SuggestedCategoryDto(string Name, string Type, bool IsDiscretionary, int TransactionCount);

/// <summary>
/// Automatic categorization for a household (see <see cref="AutoCategorizer"/>). Runs on every import and
/// sync, and again over everything whenever categories, rules or the partner name change, so transactions
/// that arrived before a category existed get filed too. Never touches the user's own choices or transfers.
/// </summary>
public class CategorizationService(AppDbContext db, TimeProvider clock)
{
    public async Task<CategorizationContext> ContextAsync(Guid householdId, CancellationToken ct)
    {
        var shown = await db.Categories.Where(c => c.HouseholdId == householdId && !c.IsHidden)
            .Select(c => new { c.Name, c.Id, c.MergedNames }).ToListAsync(ct);
        var categories = shown.ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        // A merged category answers to the names it absorbed too (DECISIONS.md 144), unless one is back as its own.
        foreach (var c in shown)
            foreach (var old in c.MergedNames)
                categories.TryAdd(old, c.Id);

        // What the user chose by hand, per merchant (only categories that still exist).
        var choices = await db.Transactions
            .Where(t => t.HouseholdId == householdId && t.CategorySource == CategorySource.User && t.CategoryId != null && !t.IsTransfer)
            .GroupBy(t => new { t.Merchant, t.CategoryId })
            .Select(g => new { g.Key.Merchant, CategoryId = g.Key.CategoryId!.Value, Count = g.Count() })
            .ToListAsync(ct);
        var live = categories.Values.ToHashSet();
        var history = choices.Where(c => live.Contains(c.CategoryId)).GroupBy(c => c.Merchant)
            .ToDictionary(g => g.Key, g => new MerchantHistory(g.Key, g.ToDictionary(x => x.CategoryId, x => x.Count)));

        var friends = await FriendsAsync(householdId, ct);
        // Your purchases from the last few months, to recognize "half of the groceries" when a friend pays you back.
        var since = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-120);
        var recent = friends.Count == 0 ? [] : await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Date >= since && t.AmountCents > 0 && !t.IsTransfer && !t.IsPartnerTransfer
                        && t.CategoryId != null && t.Kind == TransactionKind.Purchase)
            .Select(t => new RecentPurchase(t.Date, t.Merchant, t.AmountCents, t.CategoryId!.Value))
            .ToListAsync(ct);
        return new CategorizationContext(categories, history, friends, recent);
    }

    /// <summary>Friends whose transfers are tracked, oldest first (so the first one added wins a tie).</summary>
    public async Task<List<FriendTransfers>> FriendsAsync(Guid householdId, CancellationToken ct) =>
        await db.Friends.AsNoTracking().Where(f => f.HouseholdId == householdId && f.TransferName != null)
            .OrderBy(f => f.CreatedUtc).ThenBy(f => f.Name)
            .Select(f => new FriendTransfers(f.Id, f.Name, f.TransferName!, f.LabelsStartAfter)).ToListAsync(ct);


    /// <summary>
    /// Marks the transfer that matched each fixed contribution as that friend's contribution (DECISIONS.md 97, 211): the
    /// largest from them inside the window, even an unusual amount like $200 that would otherwise look like paying back.
    /// Each friend's transfers only match their own contributions. Runs after every import, sync and re-run. Never touches
    /// what you labeled yourself.
    /// </summary>
    public async Task MatchContributionsAsync(Guid householdId, CancellationToken ct)
    {
        var arrangements = await db.PartnerArrangements.AsNoTracking().Where(a => a.HouseholdId == householdId)
            .OrderBy(a => a.CreatedUtc).Include(a => a.Category).Include(a => a.Friend).ToListAsync(ct);
        if (arrangements.Count == 0)
            return;
        var h = await db.Households.AsNoTracking().SingleAsync(x => x.Id == householdId, ct);
        var calendar = new BudgetMonthCalendar(h.BudgetMonthStartDay);
        var today = Home.HomeSummaryService.TodayIn(h.TimeZoneId, clock);
        var from = arrangements.Min(a => a.StartsOn);
        var rows = await db.Transactions
            .Where(t => t.HouseholdId == householdId && t.IsPartnerTransfer && t.AmountCents < 0 && !t.IsTransfer && (t.Date >= from || t.CountsForMonthStart >= from))
            .ToListAsync(ct);

        // Each friend's transfers, month by month. A transfer sent early counts in the month you moved it to (DECISIONS.md 166).
        foreach (var month in rows.GroupBy(t => (t.FriendId, Month: t.CountsForMonthStart is { } m ? calendar.MonthContaining(m) : calendar.MonthContaining(t.Date))))
        {
            var active = arrangements.Where(a => a.FriendId == month.Key.FriendId && a.StartsOn <= month.Key.Month.End).ToList();
            if (active.Count == 0)
                continue;
            var first = active[0].Friend.Name;
            var statuses = PartnerContributions.Match(
                active.Select(a => new ContributionArrangement(a.Id, a.CategoryId, a.AmountCents, a.WindowFromDay, a.WindowToDay)).ToList(),
                month.Key.Month,
                month.Select(t => new PartnerDeposit(t.Id, t.Date, -t.AmountCents,
                    t.CategorySource == CategorySource.User && t.Kind == TransactionKind.PartnerContribution,
                    t.CategorySource == CategorySource.User && t.Kind != TransactionKind.PartnerContribution, t.CountsForMonthStart)).ToList(),
                today);
            foreach (var s in statuses.Where(s => s.MatchedDepositId is not null))
            {
                var t = month.First(x => x.Id == s.MatchedDepositId);
                if (!TransactionRules.IsAutomatic(t.CategorySource))
                    continue;
                var a = active.First(x => x.Id == s.ArrangementId);
                // Already their contribution and not waiting: it stays settled (DECISIONS.md 259: only new ones are reviewed).
                var settled = t.Kind == TransactionKind.PartnerContribution && !t.NeedsReview;
                (t.Kind, t.CategoryId, t.SuggestedCategoryId) = (TransactionKind.PartnerContribution, null, null);
                t.CategoryUnsure = s.State == ContributionState.DifferentAmount;
                t.CategoryReason = s.State == ContributionState.Received
                    ? $"{first}'s contribution toward {a.Category.Name}."
                    : $"{first}'s contribution toward {a.Category.Name}: {Money(s.ReceivedCents)}, not the usual {Money(s.ExpectedCents)}.";
                if (!settled && (a.Friend.LabelsStartAfter is not { } start || t.Date > start))
                    t.NeedsReview = true; // still yours to confirm
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static string Money(long cents) => (cents / 100m).ToString("C", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    /// <summary>Re-runs automatic categorization over all of the household's transactions.</summary>
    /// <summary>
    /// Spending in a category you've hidden is out of the budget, so in months not closed yet it goes back to be filed
    /// again (the app's guess, or Review) instead of silently counting nowhere (DECISIONS.md 177). Returns how many.
    /// </summary>
    public async Task<int> ReleaseHiddenAsync(Guid householdId, CancellationToken ct)
    {
        var openFrom = await db.BudgetMonths.Where(m => m.HouseholdId == householdId && m.ClosedUtc == null).MinAsync(m => (DateOnly?)m.StartDate, ct);
        if (openFrom is not { } from)
            return 0;
        return await db.Transactions
            .Where(t => t.HouseholdId == householdId && !t.IsTransfer && t.Date >= from && t.CategoryId != null && t.Category!.IsHidden)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CategoryId, (Guid?)null).SetProperty(t => t.CategorySource, CategorySource.None)
                .SetProperty(t => t.NeedsReview, false).SetProperty(t => t.CategoryUnsure, false)
                .SetProperty(t => t.CategoryReason, "Its category is hidden, so it needs another."), ct);
    }

    public async Task<AutoCategorizeResult> RunAsync(Guid householdId, CancellationToken ct)
    {
        await ReleaseHiddenAsync(householdId, ct);
        var ctx = await ContextAsync(householdId, ct);
        var rows = await db.Transactions.Where(t => t.HouseholdId == householdId).ToListAsync(ct);
        foreach (var t in rows)
            ctx.Apply(t);
        await db.SaveChangesAsync(ct);
        await MatchContributionsAsync(householdId, ct);

        var spending = rows.Where(t => !t.IsTransfer && !TransactionRules.IsMoneyIn(t.Kind) && t.Kind is not (TransactionKind.CardPayment or TransactionKind.Transfer)).ToList();
        return new AutoCategorizeResult(
            Filed: spending.Count(t => t.NeedsReview && !t.CategoryUnsure),
            Guesses: spending.Count(t => t.NeedsReview && t.CategoryUnsure),
            StillUncategorized: spending.Count(t => TransactionRules.NeedsCategory(t.CategoryId is not null, t.IsTransfer, t.Kind, t.AmountCents)),
            PartnerContributions: rows.Count(t => t.Kind == TransactionKind.PartnerContribution));
    }

    /// <summary>
    /// Starts every category over (DECISIONS.md 176): clears every category on spending, including the ones you picked by
    /// hand, so <see cref="RunAsync"/> files them again from your merchant rules, the merchant name and the bank's
    /// category alone. Transfers, and money between you and your partner that you labeled, are left as they are.
    /// Returns how many were cleared.
    /// </summary>
    public async Task<int> ResetAsync(Guid householdId, CancellationToken ct) =>
        await db.Transactions
            .Where(t => t.HouseholdId == householdId && !t.IsTransfer && t.CategorySource != CategorySource.Transfer
                        && !(t.CategorySource == CategorySource.User && (t.IsPartnerTransfer || t.Kind == TransactionKind.PartnerContribution)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CategoryId, (Guid?)null)
                .SetProperty(t => t.SuggestedCategoryId, (Guid?)null)
                .SetProperty(t => t.CategorySource, CategorySource.None)
                .SetProperty(t => t.NeedsReview, false)
                .SetProperty(t => t.CategoryUnsure, false)
                .SetProperty(t => t.CategoryReason, (string?)null), ct);

    /// <summary>Categories worth adding: groups of uncategorized transactions the bank labels alike (e.g. 104 "Transportation").</summary>
    public async Task<List<SuggestedCategoryDto>> SuggestionsAsync(Guid householdId, CancellationToken ct)
    {
        var ctx = await ContextAsync(householdId, ct);
        var rows = await TransactionEndpoints.Inbox(db, householdId).AsNoTracking().ToListAsync(ct);
        return rows
            .Select(t => ctx.Guess(t).Suggestion)
            .OfType<SuggestedCategory>()
            .Where(s => !ctx.CategoryIdsByName.ContainsKey(s.Name))
            .GroupBy(s => s.Name)
            .Select(g => new SuggestedCategoryDto(g.Key, g.First().Type.ToString(), g.First().IsDiscretionary, g.Count()))
            .Where(s => s.TransactionCount >= 2)
            .OrderByDescending(s => s.TransactionCount)
            .ToList();
    }
}

public record DiscoveredCategoryDto(
    string Name, string Type, bool IsDiscretionary, bool Custom, int TransactionCount, int MonthsSeen, long MonthlyCents,
    IReadOnlyList<string> Examples, string Reason,
    // "new" (would be created), "exists" (you already have it), or "hidden" (you have it hidden; it would come back).
    string Status,
    // One bill per line: its merchant, and the grouped category it comes out of.
    string? MerchantPattern = null, string? Replaces = null);

public record UnusedCategoryDto(Guid Id, string Name, string Reason);

public record CategoryDiscoveryDto(int MonthsAnalyzed, IReadOnlyList<DiscoveredCategoryDto> Proposed, IReadOnlyList<UnusedCategoryDto> Unused, int Unmatched);

public record NewCategoryRequest(string Name, CategoryType Type, bool IsDiscretionary, string? MerchantPattern = null, string? Replaces = null);
public record ApplyDiscoveryRequest(List<NewCategoryRequest> Create, List<Guid> HideIds);

/// <summary>
/// Builds a household's categories from its own transactions (see <see cref="CategoryDiscovery"/>) instead of a
/// fixed starter list: proposes what fits, offers to hide what's unused, and applies the user's choices.
/// </summary>
public class CategoryDiscoveryService(AppDbContext db, CategorizationService categorization, TimeProvider clock)
{
    public const int HistoryMonths = 12;

    public async Task<CategoryDiscoveryDto> DiscoverAsync(Guid householdId, CancellationToken ct)
    {
        var since = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddMonths(-HistoryMonths);
        var txns = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Date >= since && t.Account.IncludedInBudget)
            .Select(t => new DiscoveryTxn(
                t.Date, t.AmountCents, t.Merchant, t.Description, t.BankCategory, t.BankCategoryPrimary, t.Kind, t.IsTransfer,
                t.IsTransfer && t.Kind == TransactionKind.CardPayment && t.AmountCents > 0 && t.Account.Type != AccountType.CreditCard,
                t.IsTransfer && t.AmountCents > 0 && t.Category != null && t.Category.Type == CategoryType.SavingsTransfer,
                t.CategorySource == CategorySource.User && !t.IsTransfer && t.Category != null ? t.Category.Name : null,
                t.Category != null ? t.Category.Type : CategoryType.Variable,
                t.Category != null && t.Category.Priority == CategoryPriority.Low))
            .ToListAsync(ct);

        var categories = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == householdId).ToListAsync(ct);
        var confirmed = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && (t.CategorySource == CategorySource.User || t.CategorySource == CategorySource.BestGuess) && t.CategoryId != null) // in use, even as a best guess (so "Other" isn't offered for hiding)
            .GroupBy(t => t.CategoryId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var existing = categories.Select(c => new ExistingCategory(c.Id, c.Name, c.Type, c.IsHidden, confirmed.GetValueOrDefault(c.Id))).ToList();

        var r = CategoryDiscovery.Discover(txns, existing);
        return new CategoryDiscoveryDto(r.MonthsAnalyzed,
            r.Proposed.Select(p =>
            {
                var match = categories.FirstOrDefault(c => string.Equals(c.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                var status = match is null ? "new" : match.IsHidden ? "hidden" : "exists";
                return new DiscoveredCategoryDto(p.Name, p.Type.ToString(), p.IsDiscretionary, p.Custom, p.TransactionCount, p.MonthsSeen,
                    p.MonthlyCents, p.Examples, p.Reason, status, p.MerchantPattern, p.Replaces);
            }).ToList(),
            r.Unused.Select(u => new UnusedCategoryDto(u.Id, u.Name, u.Reason)).ToList(),
            r.Unmatched);
    }

    /// <summary>Creates (or un-hides) the chosen categories with $0 limits, hides the chosen unused ones, then re-files everything.</summary>
    public async Task<AutoCategorizeResult> ApplyAsync(Guid householdId, ApplyDiscoveryRequest req, CancellationToken ct)
    {
        var categories = await db.Categories.Where(c => c.HouseholdId == householdId).ToListAsync(ct);
        var order = categories.Count == 0 ? 0 : categories.Max(c => c.SortOrder) + 1;
        var moves = new List<(Guid? From, string Pattern, Guid To, string Name)>();
        foreach (var n in req.Create)
        {
            var name = n.Name.Trim();
            if (name.Length is 0 or > 100 || !Enum.IsDefined(n.Type))
                continue;
            var c = categories.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (c is not null)
                c.IsHidden = false;
            else
            {
                c = new Category
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, Name = name, Type = n.Type, MonthlyLimitCents = 0,
                    Priority = n.Type == CategoryType.Variable && n.IsDiscretionary ? CategoryPriority.Low : CategoryPriority.Medium, SortOrder = order++,
                };
                db.Categories.Add(c);
                categories.Add(c);
            }

            // One bill per line: the bill's past charges move out of the grouped category it's split from (the user asked
            // for the split, so their own too), so the most likely category for its next charges is this line (DECISIONS.md 259).
            var pattern = n.MerchantPattern?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(pattern) || pattern.Length > 100)
                continue;
            var from = categories.FirstOrDefault(x => n.Replaces is not null && string.Equals(x.Name, n.Replaces, StringComparison.OrdinalIgnoreCase));
            moves.Add((from?.Id, pattern, c.Id, c.Name));
        }
        foreach (var c in categories.Where(c => req.HideIds.Contains(c.Id)))
            c.IsHidden = true;
        await db.SaveChangesAsync(ct);
        // You asked for the merchant's own line, so its charges move there as your choice (DECISIONS.md 259): the ones in the
        // grouped category, and the ones the app filed anywhere else. Their next charges follow from that.
        foreach (var (fromId, pattern, toId, name) in moves) // after saving, so the new categories exist
            await db.Transactions
                .Where(t => t.HouseholdId == householdId && !t.IsTransfer && t.Merchant.ToUpper().Contains(pattern) && t.AmountCents > 0
                            && (t.CategoryId == fromId || t.CategorySource == CategorySource.None || t.CategorySource == CategorySource.Bank
                                || t.CategorySource == CategorySource.Learned || t.CategorySource == CategorySource.Rule))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, toId).SetProperty(t => t.CategorySource, CategorySource.User)
                    .SetProperty(t => t.NeedsReview, false).SetProperty(t => t.CategoryUnsure, false)
                    .SetProperty(t => t.CategoryReason, "You gave " + name + " its own line."), ct);
        return await categorization.RunAsync(householdId, ct);
    }
}
