using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Budget;

/// <summary>
/// Combine categories: <paramref name="FromIds"/> go into an existing category (<paramref name="IntoId"/>) or a new
/// one (<paramref name="NewName"/>). Give exactly one of the two.
/// </summary>
public record MergeCategoriesRequest(List<Guid> FromIds, Guid? IntoId = null, string? NewName = null);

public record MergeCategoriesResult(Guid Id, string Name, int MovedTransactions);

/// <param name="MovedTo">The category its transactions went to, or null when they went to Review.</param>
public record DeleteCategoryResult(string Name, string? MovedTo, int MovedTransactions, int ToReview);

/// <summary>
/// Merges categories into one (DECISIONS.md 144). Everything filed under the old ones moves over: transactions, merchant
/// rules, the partner's contributions and each month's record, with limits added together. The merged category
/// remembers the old names so the app keeps filing those transactions there. The old categories are then deleted; a
/// merge can't be undone, so the screen asks first.
/// </summary>
public static class CategoryMerge
{
    /// <param name="addLimits">
    /// False when deleting a category and moving its transactions (DECISIONS.md 169): the category they go to keeps its
    /// own limit instead of taking the deleted one's on top.
    /// </param>
    /// <returns>The merged category, or the problems with the request.</returns>
    public static async Task<(MergeCategoriesResult? Result, Dictionary<string, string[]>? Errors)> RunAsync(
        AppDbContext db, Guid householdId, MergeCategoriesRequest req, CancellationToken ct, bool addLimits = true)
    {
        static Dictionary<string, string[]> Error(string field, string message) => new() { [field] = [message] };

        var fromIds = (req.FromIds ?? []).Distinct().ToList();
        var newName = req.NewName?.Trim();
        if ((req.IntoId is null) == string.IsNullOrEmpty(newName))
            return (null, Error("into", "Pick a category to merge into, or name a new one."));
        if (newName is { Length: > 100 })
            return (null, Error("newName", "Use up to 100 characters."));

        var all = await db.Categories.Where(c => c.HouseholdId == householdId).ToListAsync(ct);
        if (fromIds.Any(id => all.All(c => c.Id != id)))
            return (null, Error("fromIds", "One of those categories doesn't exist."));

        Category? into;
        var isNew = false;
        if (req.IntoId is { } intoId)
        {
            into = all.SingleOrDefault(c => c.Id == intoId);
            if (into is null)
                return (null, Error("into", "That category doesn't exist."));
        }
        else
        {
            into = all.FirstOrDefault(c => string.Equals(c.Name, newName, StringComparison.OrdinalIgnoreCase));
            if (into is not null && !fromIds.Contains(into.Id))
                return (null, Error("newName", $"You already have {into.Name}. Merge into it instead."));
            if (into is not null)
                into.Name = newName!; // a "new" name that's one of the merged ones: that one stays and takes the others
            else
            {
                var picked = all.Where(c => fromIds.Contains(c.Id)).ToList();
                if (picked.Count == 0)
                    return (null, Error("fromIds", "Pick at least one category to merge."));
                // The kind most of the money was budgeted as, at the highest priority among them (squeezed no sooner than any was).
                var type = picked.GroupBy(c => c.Type)
                    .OrderByDescending(g => g.Sum(c => c.MonthlyLimitCents)).ThenByDescending(g => g.Count()).First().Key;
                into = new Category
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, Name = newName!, Type = type,
                    Priority = type == CategoryType.Variable ? picked.Max(c => c.Priority) : CategoryPriority.Medium,
                    IsSplit = picked.Any(c => c.IsSplit), SplitPercent = picked.FirstOrDefault(c => c.IsSplit)?.SplitPercent ?? 50m,
                    SortOrder = picked.Min(c => c.SortOrder),
                };
                isNew = true;
            }
        }
        fromIds.Remove(into.Id);
        var sources = all.Where(c => fromIds.Contains(c.Id)).ToList();
        if (sources.Count == 0)
            return (null, Error("fromIds", "Pick at least one other category to merge."));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (isNew)
        {
            db.Categories.Add(into);
            await db.SaveChangesAsync(ct);
        }
        var intoKey = into.Id;

        // Move everything over before the old categories go (deleting one clears its transactions' category).
        var moved = await db.Transactions.Where(t => t.HouseholdId == householdId && t.CategoryId != null && fromIds.Contains(t.CategoryId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, intoKey), ct);
        await db.Transactions.Where(t => t.HouseholdId == householdId && t.SuggestedCategoryId != null && fromIds.Contains(t.SuggestedCategoryId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.SuggestedCategoryId, intoKey), ct);
        await db.PartnerArrangements.Where(a => a.HouseholdId == householdId && fromIds.Contains(a.CategoryId))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.CategoryId, intoKey), ct);

        // Each month's record becomes one row for the merged category, with the limits and spending added together.
        var usual = sources.Append(into).ToDictionary(c => c.Id, c => c.MonthlyLimitCents);
        var everyId = fromIds.Append(intoKey).ToList();
        var rows = await db.BudgetMonthCategories.Where(m => everyId.Contains(m.CategoryId)).ToListAsync(ct);
        foreach (var g in rows.GroupBy(r => r.BudgetMonthId).Select(g => g.ToList()))
        {
            long? Sum(Func<BudgetMonthCategory, long?> pick) => g.Any(r => pick(r) is not null) ? g.Sum(r => pick(r) ?? 0) : null;
            // A "this month only" limit on any of them: that month the merged one gets each one's limit for it, added up.
            var keep = g.FirstOrDefault(r => r.CategoryId == intoKey);
            var monthLimit = !addLimits ? keep?.LimitOverrideCents
                : g.Any(r => r.LimitOverrideCents is not null)
                    ? everyId.Sum(id => g.FirstOrDefault(r => r.CategoryId == id)?.LimitOverrideCents ?? usual[id])
                    : (long?)null;
            if (keep is null)
                db.BudgetMonthCategories.Add(keep = new BudgetMonthCategory { Id = Guid.NewGuid(), BudgetMonthId = g[0].BudgetMonthId, CategoryId = intoKey });
            (keep.LimitOverrideCents, keep.ClosingLimitCents, keep.ClosingSpentCents) = (monthLimit, Sum(r => r.ClosingLimitCents), Sum(r => r.ClosingSpentCents));
            db.BudgetMonthCategories.RemoveRange(g.Where(r => r.CategoryId != intoKey));
        }

        if (addLimits)
        {
            into.MonthlyLimitCents += sources.Sum(c => c.MonthlyLimitCents);
            into.LimitSetByUser |= sources.Any(c => c.LimitSetByUser);
        }
        into.IsHidden = false;
        into.MergedNames = into.MergedNames
            .Concat(sources.SelectMany(c => c.MergedNames.Prepend(c.Name)))
            .Where(n => !string.Equals(n, into.Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        db.Categories.RemoveRange(sources);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (new MergeCategoriesResult(intoKey, into.Name, moved), null);
    }

    /// <summary>
    /// Deletes a category without moving its transactions anywhere (DECISIONS.md 169): they lose their category and go
    /// to Review, where the app's guess for each waits for you. Its merchant rules go with it. A category your partner's
    /// contribution goes toward can't be deleted this way, since that money needs a bill line.
    /// </summary>
    /// <returns>How many transactions went to Review, or the problem.</returns>
    public static async Task<(int? ToReview, Dictionary<string, string[]>? Errors)> DeleteAsync(AppDbContext db, Guid householdId, Guid id, CancellationToken ct)
    {
        var cat = await db.Categories.SingleOrDefaultAsync(c => c.Id == id && c.HouseholdId == householdId, ct);
        if (cat is null)
            return (null, new() { ["id"] = ["That category doesn't exist."] });
        if (await db.PartnerArrangements.AnyAsync(a => a.HouseholdId == householdId && a.CategoryId == id, ct))
            return (null, new() { ["moveToId"] = [$"A friend's contribution goes toward {cat.Name}. Pick a category to move it and its transactions to."] });

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var reason = $"You deleted {cat.Name}. Pick a new category.";
        var toReview = await db.Transactions.Where(t => t.HouseholdId == householdId && t.CategoryId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CategoryId, (Guid?)null).SetProperty(t => t.CategorySource, CategorySource.None)
                .SetProperty(t => t.NeedsReview, true).SetProperty(t => t.CategoryUnsure, true).SetProperty(t => t.CategoryReason, reason), ct);
        await db.Transactions.Where(t => t.HouseholdId == householdId && t.SuggestedCategoryId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.SuggestedCategoryId, (Guid?)null), ct);
        await db.BudgetMonthCategories.Where(m => m.CategoryId == id).ExecuteDeleteAsync(ct);
        db.Categories.Remove(cat); // its merchant rules cascade
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (toReview, null);
    }
}
