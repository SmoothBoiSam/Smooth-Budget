using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Transactions;

/// <summary>What categorization needs about one household, loaded once per import, sync or back-fill.</summary>
public sealed class CategorizationContext(
    IReadOnlyCollection<MerchantRule> rules,
    IReadOnlyDictionary<string, Guid> categoryIdsByName,
    IReadOnlyDictionary<string, MerchantHistory> history,
    string? partnerName,
    IReadOnlyList<RecentPurchase>? recentPurchases = null,
    DateOnly? partnerLabelsStartAfter = null)
{
    public IReadOnlyDictionary<string, Guid> CategoryIdsByName { get; } = categoryIdsByName;

    public CategoryGuess Guess(Transaction t) => AutoCategorizer.Guess(
        new CategorizationInput(t.Merchant, t.BankCategory, t.BankCategoryPrimary, t.BankConfidence, t.Description),
        rules, history.GetValueOrDefault(t.Merchant), CategoryIdsByName);

    /// <summary>
    /// Applies automatic decisions to one row: recognizes money to and from the partner (by the name in Settings), files
    /// purchases in a category, and tells income from partner money. Never touches what the user chose, or transfers.
    /// Every automatic category waits for the user (DECISIONS.md 80). Money between you and your partner waits too,
    /// counted nowhere, with the app's suggestion, until you say what it was for (DECISIONS.md 100); her fixed
    /// contribution is matched separately (<see cref="CategorizationService.MatchContributionsAsync"/>).
    /// </summary>
    public void Apply(Transaction t)
    {
        if (t.IsTransfer)
            return;
        var fromPartner = t.AmountCents < 0 && t.Kind is TransactionKind.Income or TransactionKind.PartnerContribution or TransactionKind.PartnerTransfer
                          && PartnerDetector.IsFromPartner(t.Description, t.AmountCents, partnerName);
        var toPartner = t.AmountCents > 0 && t.Kind is TransactionKind.Purchase or TransactionKind.Other or TransactionKind.PartnerTransfer
                        && PartnerMoney.IsToPartner(t.Description, t.AmountCents, partnerName);
        t.IsPartnerTransfer = fromPartner || toPartner;

        if (!TransactionRules.IsAutomatic(t.CategorySource))
        {
            // You said what it was for; only a "contribution" from someone who's no longer the partner reverts.
            if (t.AmountCents < 0 && !fromPartner && t.Kind == TransactionKind.PartnerContribution)
                t.Kind = TransactionKind.Income;
            return;
        }
        t.SuggestedCategoryId = null;
        var labeling = partnerLabelsStartAfter is { } start && t.Date > start; // before that, as it always was

        if (fromPartner)
        {
            if (labeling)
                Wait(t, PartnerMoney.Suggest(t.Date, t.AmountCents, recentPurchases ?? [], partnerName!));
            else
                (t.Kind, t.CategoryId, t.CategorySource, t.NeedsReview, t.CategoryUnsure, t.CategoryReason) =
                    (TransactionKind.PartnerContribution, null, CategorySource.None, false, false, null);
            return;
        }
        if (t.AmountCents < 0 && t.Kind is TransactionKind.Income or TransactionKind.PartnerContribution or TransactionKind.PartnerTransfer)
        {
            (t.Kind, t.CategoryId, t.CategorySource, t.NeedsReview, t.CategoryUnsure, t.CategoryReason) =
                (TransactionKind.Income, null, CategorySource.None, false, false, null);
            return;
        }

        var guess = Guess(t);
        if (toPartner && labeling)
        {
            // Your share of something she bought, or something else: you say. What you chose for her before is a suggestion.
            var asked = PartnerMoney.Suggest(t.Date, t.AmountCents, [], partnerName!);
            Wait(t, new PartnerSuggestion(guess.CategoryId, asked.Reason));
            return;
        }
        if (t.Kind == TransactionKind.PartnerTransfer)
            t.Kind = TransactionKind.Purchase; // no longer to the partner (the name changed)

        // Re-running only improves things: with no opinion now, keep a category that still exists.
        if (guess.CategoryId is null && t.CategoryId is { } current && CategoryIdsByName.Values.Contains(current))
        {
            t.NeedsReview = true; // still automatic, so still waiting for the user
            return;
        }
        t.CategoryId = guess.CategoryId;
        t.CategorySource = guess.Source;
        // Every automatic category waits for the user to confirm it (DECISIONS.md 80); "unsure" only sorts the review list.
        t.NeedsReview = guess.CategoryId is not null;
        t.CategoryUnsure = guess.CategoryId is not null && guess.Unsure;
        t.CategoryReason = Clip(guess.Reason);
    }

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
        var rules = await db.CategoryRules.Where(r => r.HouseholdId == householdId)
            .Select(r => new MerchantRule(r.CategoryId, r.Pattern, r.CreatedUtc)).ToListAsync(ct);
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

        var h = await db.Households.Where(x => x.Id == householdId)
            .Select(x => new { x.PartnerName, x.PartnerLabelsStartAfter }).SingleAsync(ct);
        // Your purchases from the last few months, to recognize "half of the groceries" when she pays you back.
        var since = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-120);
        var recent = h.PartnerName is null ? [] : await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Date >= since && t.AmountCents > 0 && !t.IsTransfer && !t.IsPartnerTransfer
                        && t.CategoryId != null && t.Kind == TransactionKind.Purchase)
            .Select(t => new RecentPurchase(t.Date, t.Merchant, t.AmountCents, t.CategoryId!.Value))
            .ToListAsync(ct);
        return new CategorizationContext(rules, categories, history, h.PartnerName, recent, h.PartnerLabelsStartAfter);
    }


    /// <summary>
    /// Marks the transfer that matched each fixed contribution as her contribution (DECISIONS.md 97): the largest from her
    /// inside the window, even an unusual amount like $200 that would otherwise look like paying back. Runs after every
    /// import, sync and re-run. Never touches what you labeled yourself.
    /// </summary>
    public async Task MatchContributionsAsync(Guid householdId, CancellationToken ct)
    {
        var arrangements = await db.PartnerArrangements.AsNoTracking().Where(a => a.HouseholdId == householdId)
            .OrderBy(a => a.CreatedUtc).Include(a => a.Category).ToListAsync(ct);
        if (arrangements.Count == 0)
            return;
        var h = await db.Households.AsNoTracking().SingleAsync(x => x.Id == householdId, ct);
        var calendar = new BudgetMonthCalendar(h.BudgetMonthStartDay);
        var today = Home.HomeSummaryService.TodayIn(h.TimeZoneId, clock);
        var from = arrangements.Min(a => a.StartsOn);
        var rows = await db.Transactions
            .Where(t => t.HouseholdId == householdId && t.IsPartnerTransfer && t.AmountCents < 0 && !t.IsTransfer && (t.Date >= from || t.CountsForMonthStart >= from))
            .ToListAsync(ct);
        var first = h.PartnerName is { } n ? n.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0] : "Your partner";
        first = first.Length > 1 ? char.ToUpperInvariant(first[0]) + first[1..].ToLowerInvariant() : first;

        // A transfer sent early counts in the month you moved it to (DECISIONS.md 166).
        foreach (var month in rows.GroupBy(t => t.CountsForMonthStart is { } m ? calendar.MonthContaining(m) : calendar.MonthContaining(t.Date)))
        {
            var active = arrangements.Where(a => a.StartsOn <= month.Key.End).ToList();
            var statuses = PartnerContributions.Match(
                active.Select(a => new ContributionArrangement(a.Id, a.CategoryId, a.AmountCents, a.WindowFromDay, a.WindowToDay)).ToList(),
                month.Key,
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
                (t.Kind, t.CategoryId, t.SuggestedCategoryId) = (TransactionKind.PartnerContribution, null, null);
                t.CategoryUnsure = s.State == ContributionState.DifferentAmount;
                t.CategoryReason = s.State == ContributionState.Received
                    ? $"{first}'s contribution toward {a.Category.Name}."
                    : $"{first}'s contribution toward {a.Category.Name}: {Money(s.ReceivedCents)}, not the usual {Money(s.ExpectedCents)}.";
                if (h.PartnerLabelsStartAfter is not { } start || t.Date > start)
                    t.NeedsReview = true; // still yours to confirm
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static string Money(long cents) => (cents / 100m).ToString("C", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    /// <summary>Re-runs automatic categorization over all of the household's transactions.</summary>
    public async Task<AutoCategorizeResult> RunAsync(Guid householdId, CancellationToken ct)
    {
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
                t.Category != null && t.Category.IsDiscretionary))
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
        var rules = await db.CategoryRules.Where(r => r.HouseholdId == householdId).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var moves = new List<(Guid From, string Pattern, Guid To)>();
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
                    IsDiscretionary = n.Type == CategoryType.Variable && n.IsDiscretionary, SortOrder = order++,
                };
                db.Categories.Add(c);
                categories.Add(c);
            }

            // One bill per line: a merchant rule keeps future charges on this line, and the bill's past charges
            // move out of the grouped category it's split from (the user asked for the split, so their own too).
            var pattern = n.MerchantPattern?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(pattern) || pattern.Length > 100)
                continue;
            var rule = rules.FirstOrDefault(r => r.Pattern == pattern);
            if (rule is null)
            {
                rule = new CategoryRule { Id = Guid.NewGuid(), HouseholdId = householdId, Pattern = pattern, CategoryId = c.Id, CreatedUtc = now };
                db.CategoryRules.Add(rule);
                rules.Add(rule);
            }
            else
                rule.CategoryId = c.Id;
            var from = categories.FirstOrDefault(x => n.Replaces is not null && string.Equals(x.Name, n.Replaces, StringComparison.OrdinalIgnoreCase));
            if (from is not null)
                moves.Add((from.Id, pattern, c.Id));
        }
        foreach (var c in categories.Where(c => req.HideIds.Contains(c.Id)))
            c.IsHidden = true;
        await db.SaveChangesAsync(ct);
        foreach (var (fromId, pattern, toId) in moves) // after saving, so the new categories exist
            await db.Transactions
                .Where(t => t.HouseholdId == householdId && t.CategoryId == fromId && t.Merchant.ToUpper().Contains(pattern))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, toId), ct);
        return await categorization.RunAsync(householdId, ct);
    }
}
