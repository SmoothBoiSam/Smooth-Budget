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
    IReadOnlyDictionary<Guid, int>? partnerSharePercent = null,
    IReadOnlyList<SplitPurchase>? recentSplits = null,
    DateOnly? settleUpAfter = null)
{
    public IReadOnlyDictionary<string, Guid> CategoryIdsByName { get; } = categoryIdsByName;

    public CategoryGuess Guess(Transaction t) => AutoCategorizer.Guess(
        new CategorizationInput(t.Merchant, t.BankCategory, t.BankCategoryPrimary, t.BankConfidence, t.Description),
        rules, history.GetValueOrDefault(t.Merchant), CategoryIdsByName);

    /// <summary>
    /// Applies automatic decisions to one row: tags money to and from the partner (by the name in Settings) and
    /// guesses what it was for, files purchases in a category, and applies the category's default split. Never
    /// touches what the user chose, or transfers. Every automatic decision waits for the user (DECISIONS.md 80, 86).
    /// </summary>
    public void Apply(Transaction t)
    {
        if (t.IsTransfer)
            return;
        var userChose = !TransactionRules.IsAutomatic(t.CategorySource);
        var fromPartner = t.AmountCents < 0 && t.Kind is TransactionKind.Income or TransactionKind.PartnerContribution or TransactionKind.PartnerSettleUp
                          && PartnerDetector.IsFromPartner(t.Description, t.AmountCents, partnerName);
        var toPartner = t.AmountCents > 0 && PartnerMoney.IsToPartner(t.Description, t.AmountCents, partnerName);
        t.IsPartnerTransfer = fromPartner || toPartner;

        // Money in: income, or partner money whose purpose the app guesses (her contribution, or paying back).
        if (t.AmountCents < 0 && t.Kind is TransactionKind.Income or TransactionKind.PartnerContribution or TransactionKind.PartnerSettleUp)
        {
            if (userChose)
            {
                // The user said what it was for; only a "contribution" from someone who's no longer the partner reverts.
                if (!fromPartner && t.Kind == TransactionKind.PartnerContribution)
                    t.Kind = TransactionKind.Income;
                return;
            }
            (t.CategoryId, t.CategorySource) = (null, CategorySource.None);
            if (!fromPartner)
            {
                (t.Kind, t.NeedsReview, t.CategoryUnsure, t.CategoryReason) = (TransactionKind.Income, false, false, null);
                return;
            }
            if (settleUpAfter is null || t.Date <= settleUpAfter)
            {
                // Before settle-up tracking started: her money is her contribution, as it always was (DECISIONS.md 92).
                (t.Kind, t.NeedsReview, t.CategoryUnsure, t.CategoryReason) = (TransactionKind.PartnerContribution, false, false, null);
                return;
            }
            var g = PartnerMoney.Guess(new PartnerTransfer(t.Id, t.Date, t.AmountCents, t.Kind), recentSplits ?? [], partnerName!);
            t.Kind = g.Purpose == PartnerPurpose.Contribution ? TransactionKind.PartnerContribution : TransactionKind.PartnerSettleUp;
            (t.NeedsReview, t.CategoryUnsure, t.CategoryReason) = (true, g.Unsure, Clip(g.Reason));
            return;
        }

        if (userChose)
            return;

        var guess = Guess(t);
        // Re-running only improves things: with no opinion now, keep a category that still exists.
        if (guess.CategoryId is null && t.CategoryId is { } current && CategoryIdsByName.Values.Contains(current))
        {
            t.NeedsReview = true; // still automatic, so still waiting for the user
            ApplySplit(t);
            return;
        }
        t.CategoryId = guess.CategoryId;
        t.CategorySource = guess.Source;
        // Every automatic category waits for the user to confirm it (DECISIONS.md 80); "unsure" only sorts the review list.
        t.NeedsReview = guess.CategoryId is not null;
        t.CategoryUnsure = guess.CategoryId is not null && guess.Unsure;
        t.CategoryReason = Clip(guess.Reason);
        if (toPartner && t.CategoryId is null)
            t.CategoryReason = Clip(PartnerMoney.Guess(new PartnerTransfer(t.Id, t.Date, t.AmountCents, t.Kind), [], partnerName!).Reason);
        ApplySplit(t);
    }

    /// <summary>A category split with the partner by default sets the owner's share; the user can change it per purchase.</summary>
    private void ApplySplit(Transaction t)
    {
        var percent = t.CategoryId is { } id ? partnerSharePercent?.GetValueOrDefault(id) ?? 0 : 0;
        t.SplitFactor = percent is > 0 and < 100 ? 1m - percent / 100m : 1m;
    }

    private static string Clip(string reason) => reason.Length <= 200 ? reason : reason[..200];
}

/// <summary>Filed / Guesses: automatic categories still waiting for the user (likely / unsure). Confirmed ones aren't counted.</summary>
public record AutoCategorizeResult(int Filed, int Guesses, int StillUncategorized, int PartnerContributions);

public record SuggestedCategoryDto(string Name, string Type, bool IsDiscretionary, int TransactionCount);

/// <summary>
/// Automatic categorization for a household (see <see cref="AutoCategorizer"/>). Runs on every import and
/// sync, and again over everything whenever categories, rules or the partner name change, so transactions
/// that arrived before a category existed get filed too. Never touches the user's own choices or transfers.
/// </summary>
public class CategorizationService(AppDbContext db)
{
    public async Task<CategorizationContext> ContextAsync(Guid householdId, CancellationToken ct)
    {
        var rules = await db.CategoryRules.Where(r => r.HouseholdId == householdId)
            .Select(r => new MerchantRule(r.CategoryId, r.Pattern, r.CreatedUtc)).ToListAsync(ct);
        var categories = await db.Categories.Where(c => c.HouseholdId == householdId && !c.IsHidden)
            .ToDictionaryAsync(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase, ct);

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
            .Select(x => new { x.PartnerName, x.PartnerSettledThrough }).SingleAsync(ct);
        var shares = await db.Categories.Where(c => c.HouseholdId == householdId && c.PartnerSharePercent > 0)
            .ToDictionaryAsync(c => c.Id, c => c.PartnerSharePercent, ct);
        var splits = h.PartnerName is null ? [] : await SplitPurchasesAsync(db, householdId, null, ct);
        return new CategorizationContext(rules, categories, history, h.PartnerName, shares, splits, h.PartnerSettledThrough);
    }

    /// <summary>Purchases the owner split with the partner (her share is owed to him), optionally only after a date.</summary>
    public static Task<List<SplitPurchase>> SplitPurchasesAsync(AppDbContext db, Guid householdId, DateOnly? after, CancellationToken ct) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && !t.IsTransfer && t.SplitFactor < 1m && t.CategoryId != null
                        && (after == null || t.Date > after))
            .Select(t => new SplitPurchase(t.Id, t.Date, t.Merchant, t.AmountCents, t.SplitFactor))
            .ToListAsync(ct);

    /// <summary>Re-runs automatic categorization over all of the household's transactions.</summary>
    public async Task<AutoCategorizeResult> RunAsync(Guid householdId, CancellationToken ct)
    {
        var ctx = await ContextAsync(householdId, ct);
        var rows = await db.Transactions.Where(t => t.HouseholdId == householdId).ToListAsync(ct);
        foreach (var t in rows)
            ctx.Apply(t);
        await db.SaveChangesAsync(ct);

        var spending = rows.Where(t => !t.IsTransfer && !TransactionRules.IsMoneyIn(t.Kind) && t.Kind is not (TransactionKind.CardPayment or TransactionKind.Transfer)).ToList();
        return new AutoCategorizeResult(
            Filed: spending.Count(t => t.NeedsReview && !t.CategoryUnsure),
            Guesses: spending.Count(t => t.NeedsReview && t.CategoryUnsure),
            StillUncategorized: spending.Count(t => TransactionRules.NeedsCategory(t.CategoryId is not null, t.IsTransfer, t.Kind, t.AmountCents)),
            PartnerContributions: rows.Count(t => t.Kind == TransactionKind.PartnerContribution));
    }

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
            .Where(t => t.HouseholdId == householdId && t.CategorySource == CategorySource.User && t.CategoryId != null)
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
