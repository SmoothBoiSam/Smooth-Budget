using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Debt;

/// <summary>
/// Card interest as a bill (DECISIONS.md 159). When you carry card debt or have been charged interest, a "Card interest"
/// bill line is added, and interest charges move into it from spending categories (where they counted against Left to
/// Spend). The automatic budget plans that line from your balances and APRs.
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
        // Leave alone what you filed yourself somewhere other than spending (a bill or debt line of your own).
        var charges = candidates.Where(t => CardInterest.IsCharge(t.Merchant, t.Description, t.BankCategory, t.AmountCents)
                                            && !(t.CategorySource == CategorySource.User && t.Category is not null && t.Category.Type != CategoryType.Variable)).ToList();

        var owesInterest = await db.DebtAccounts.AnyAsync(d => d.HouseholdId == householdId && !d.IsHidden && d.BalanceCents > 0 && d.AprBps > 0, ct);
        if (charges.Count == 0 && (cat is not null || !owesInterest)) return 0;

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
}
