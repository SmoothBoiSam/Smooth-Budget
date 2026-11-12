using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Debt;

/// <summary>
/// Card interest as a bill (DECISIONS.md 159), listed with debt (232). When you carry card debt or have been charged
/// interest, a "Card interest" line is added, and interest charges move into it from spending categories (where they
/// counted against Left to Spend), each tied to the card or loan it was charged on. The automatic budget plans that line
/// from your balances and APRs.
/// </summary>
public class CardInterestService(AppDbContext db)
{
    public const string Reason = "Card interest: a bill planned from your card balances, so it doesn't come out of Left to Spend.";

    /// <returns>How many interest charges moved.</returns>
    public async Task<int> EnsureAsync(Guid householdId, CancellationToken ct)
    {
        var categories = await db.Categories.Where(c => c.HouseholdId == householdId).ToListAsync(ct);
        var cat = categories.FirstOrDefault(c => string.Equals(c.Name.Trim(), CardInterest.CategoryName, StringComparison.OrdinalIgnoreCase));
        // You hid it, or made it something other than a bill: interest is yours to file.
        if (cat is not null && (cat.IsHidden || cat.Type != CategoryType.FixedBill)) return 0;

        var filedId = cat?.Id ?? Guid.Empty;
        var candidates = await db.Transactions
            .Where(t => t.HouseholdId == householdId && t.AmountCents > 0 && !t.IsTransfer && (t.CategoryId == null || t.CategoryId != filedId)
                        && (t.Merchant.ToUpper().Contains("INTEREST") || t.Merchant.ToUpper().Contains("FINANCE CHARGE")
                            || t.Description.ToUpper().Contains("INTEREST") || t.Description.ToUpper().Contains("FINANCE CHARGE")
                            || t.BankCategory == "BANK_FEES_INTEREST_CHARGE" || t.BankCategory == "Interest"))
            .Include(t => t.Category)
            .ToListAsync(ct);
        // Leave alone what you filed yourself somewhere other than spending (a bill or debt line of your own). Interest in
        // "Fees & interest" moves whatever its wording (DECISIONS.md 232): that line is for fees now.
        var charges = candidates.Where(t => (CardInterest.IsCharge(t.Merchant, t.Description, t.BankCategory, t.AmountCents) || InFees(t))
                                            && !(t.CategorySource == CategorySource.User && t.Category is not null && t.Category.Type != CategoryType.Variable)).ToList();
        var linked = await LinkAsync(householdId, cat?.Id, charges, ct);

        var owesInterest = await db.DebtAccounts.AnyAsync(d => d.HouseholdId == householdId && !d.IsHidden && d.BalanceCents > 0 && d.AprBps > 0, ct);
        if (charges.Count == 0 && (cat is not null || !owesInterest))
        {
            if (linked) await db.SaveChangesAsync(ct);
            return 0;
        }

        if (cat is null)
        {
            cat = new Category
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, Name = CardInterest.CategoryName, Type = CategoryType.FixedBill,
                SortOrder = categories.Count == 0 ? 0 : categories.Max(c => c.SortOrder) + 1,
            };
            db.Categories.Add(cat);
        }
        foreach (var t in charges)
        {
            t.CategoryId = cat.Id;
            t.SuggestedCategoryId = null;
            if (t.CategorySource != CategorySource.User) t.CategorySource = CategorySource.Bank;
            t.NeedsReview = false;
            t.CategoryUnsure = false;
            t.CategoryReason = Reason;
        }
        await db.SaveChangesAsync(ct);
        return charges.Count;
    }

    private static bool InFees(Transaction t) =>
        t.AmountCents > 0 && t.Category is { Type: CategoryType.Variable } c && string.Equals(c.Name.Trim(), "Fees & interest", StringComparison.OrdinalIgnoreCase)
        && $"{t.Merchant} {t.Description}".Contains("INTEREST", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ties interest charges to the card or loan they were charged on (DECISIONS.md 232): the debt for the account the
    /// charge is on. Covers the charges about to move and ones already in the line without a debt yet.
    /// </summary>
    /// <returns>True when anything changed.</returns>
    private async Task<bool> LinkAsync(Guid householdId, Guid? lineId, IReadOnlyList<Transaction> moving, CancellationToken ct)
    {
        var debts = await db.DebtAccounts.AsNoTracking().Where(d => d.HouseholdId == householdId && d.AccountId != null)
            .Select(d => new { d.Id, AccountId = d.AccountId!.Value }).ToListAsync(ct);
        if (debts.Count == 0) return false;
        var byAccount = debts.GroupBy(d => d.AccountId).ToDictionary(g => g.Key, g => g.First().Id);
        var filed = lineId is { } id
            ? await db.Transactions.Where(t => t.HouseholdId == householdId && t.CategoryId == id && t.DebtAccountId == null).ToListAsync(ct)
            : [];
        var changed = false;
        foreach (var t in moving.Concat(filed).Where(t => t.DebtAccountId is null))
            if (byAccount.TryGetValue(t.AccountId, out var debt))
                (t.DebtAccountId, changed) = (debt, true);
        return changed;
    }

    /// <summary>The Card interest line, made (or brought back) when you file a charge there yourself.</summary>
    public async Task<Category> LineAsync(Guid householdId, CancellationToken ct)
    {
        var all = await db.Categories.Where(c => c.HouseholdId == householdId).ToListAsync(ct);
        var cat = all.FirstOrDefault(c => string.Equals(c.Name.Trim(), CardInterest.CategoryName, StringComparison.OrdinalIgnoreCase));
        if (cat is null)
        {
            cat = new Category
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, Name = CardInterest.CategoryName, Type = CategoryType.FixedBill,
                SortOrder = all.Count == 0 ? 0 : all.Max(c => c.SortOrder) + 1,
            };
            db.Categories.Add(cat);
        }
        cat.IsHidden = false;
        cat.Type = CategoryType.FixedBill; // it works as a bill underneath, listed with debt
        return cat;
    }
}
