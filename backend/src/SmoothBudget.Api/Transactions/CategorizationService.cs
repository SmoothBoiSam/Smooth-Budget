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
    string? partnerName)
{
    public IReadOnlyDictionary<string, Guid> CategoryIdsByName { get; } = categoryIdsByName;

    public CategoryGuess Guess(Transaction t) => AutoCategorizer.Guess(
        new CategorizationInput(t.Merchant, t.BankCategory, t.BankCategoryPrimary, t.BankConfidence),
        rules, history.GetValueOrDefault(t.Merchant), CategoryIdsByName);

    /// <summary>
    /// Applies automatic decisions to one row: tags partner money (by the name in Settings), then files it
    /// in a category unless the user chose one, it's a transfer, or it's money in (income and partner money
    /// don't take categories).
    /// </summary>
    public void Apply(Transaction t)
    {
        if (TransactionRules.IsMoneyIn(t.Kind) && !t.IsTransfer)
            t.Kind = PartnerDetector.IsFromPartner(t.Description, t.AmountCents, partnerName)
                ? TransactionKind.PartnerContribution
                : TransactionKind.Income;

        if (t.IsTransfer || !TransactionRules.IsAutomatic(t.CategorySource))
            return;

        if (TransactionRules.IsMoneyIn(t.Kind))
        {
            (t.CategoryId, t.CategorySource, t.NeedsReview, t.CategoryReason) = (null, CategorySource.None, false, null);
            return;
        }

        var g = Guess(t);
        // Re-running only improves things: with no opinion now, keep a category that still exists.
        if (g.CategoryId is null && t.CategoryId is { } current && CategoryIdsByName.Values.Contains(current))
            return;
        t.CategoryId = g.CategoryId;
        t.CategorySource = g.Source;
        t.NeedsReview = g.CategoryId is not null && g.NeedsReview;
        t.CategoryReason = g.Reason.Length <= 200 ? g.Reason : g.Reason[..200];
    }
}

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

        var partner = await db.Households.Where(h => h.Id == householdId).Select(h => h.PartnerName).SingleAsync(ct);
        return new CategorizationContext(rules, categories, history, partner);
    }

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
            Filed: spending.Count(t => t.CategoryId is not null && !t.NeedsReview),
            Guesses: spending.Count(t => t.CategoryId is not null && t.NeedsReview),
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
