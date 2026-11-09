using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Bills;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Home;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Budget;

/// <param name="LastUsed">The last transaction filed under it, if any in the last 18 months.</param>
public record ArchivedCategoryDto(Guid Id, string Name, string Type, long MonthlyLimitCents, DateOnly? LastUsed);

/// <param name="ReleasedToReview">Transactions filed under it in months not closed yet, which now wait in Review for another category.</param>
public record ArchiveResult(string Name, int ReleasedToReview);

/// <summary>
/// Archiving a category (DECISIONS.md 180): it leaves the budget and the lists, its history stays, and it can be restored
/// from Budget → Categories. Archived is the same as the old "hidden". The app suggests archiving categories you
/// haven't used in a while.
/// </summary>
public class CategoryArchiveService(AppDbContext db, CategorizationService categorization, AutoBudgetService auto, RecurringService recurring, TimeProvider clock)
{
    /// <summary>Spending and bill lines unused this long are suggested.</summary>
    public const int UnusedDays = 90;
    /// <summary>A bill line with no detected bill must be unused this long, so a yearly bill is never suggested.</summary>
    public const int UnusedBillMonths = 13;

    public async Task<ArchiveResult?> ArchiveAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        var c = await db.Categories.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
        if (c is null) return null;
        c.IsHidden = true;
        await db.SaveChangesAsync(ct);
        var released = await categorization.ReleaseHiddenAsync(householdId, ct); // this month's spending in it goes to Review
        await categorization.RunAsync(householdId, ct);
        await auto.RefreshAsync(householdId, ct);
        return new ArchiveResult(c.Name, released);
    }

    public async Task<string?> RestoreAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        var c = await db.Categories.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId && x.IsHidden, ct);
        if (c is null) return null;
        c.IsHidden = false;
        c.KeptUnusedUtc = clock.GetUtcNow().UtcDateTime; // just brought back: don't suggest archiving it right away
        await db.SaveChangesAsync(ct);
        await categorization.RunAsync(householdId, ct);
        await auto.RefreshAsync(householdId, ct);
        return c.Name;
    }

    /// <summary>"Keep it": not suggested again until it has gone unused for another <see cref="UnusedDays"/>.</summary>
    public async Task<bool> KeepAsync(Guid householdId, Guid id, CancellationToken ct)
    {
        var c = await db.Categories.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
        if (c is null) return false;
        c.KeptUnusedUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<ArchivedCategoryDto>> ArchivedAsync(Guid householdId, CancellationToken ct)
    {
        var cats = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == householdId && c.IsHidden).OrderBy(c => c.Name).ToListAsync(ct);
        var last = await LastUsedAsync(householdId, ct);
        return cats.Select(c => new ArchivedCategoryDto(c.Id, c.Name, c.Type.ToString(), c.MonthlyLimitCents, last.GetValueOrDefault(c.Id))).ToList();
    }

    /// <summary>
    /// Spending and bill categories you haven't used in a while: nothing filed under one for <see cref="UnusedDays"/>
    /// days, added at least that long ago, and not kept in that time. A bill line also needs every bill detected in it to
    /// have stopped, or, with none detected, no charge for <see cref="UnusedBillMonths"/> months. Savings and debt lines,
    /// Card interest, and a line your partner's contribution goes to are never suggested.
    /// </summary>
    public async Task<List<ArchivedCategoryDto>> UnusedAsync(Guid householdId, CancellationToken ct)
    {
        var tz = await db.Households.Where(h => h.Id == householdId).Select(h => h.TimeZoneId).SingleAsync(ct);
        var today = HomeSummaryService.TodayIn(tz, clock);
        var cutoff = today.AddDays(-UnusedDays);
        var cutoffUtc = clock.GetUtcNow().UtcDateTime.AddDays(-UnusedDays);
        var cats = await db.Categories.AsNoTracking()
            .Where(c => c.HouseholdId == householdId && !c.IsHidden && (c.Type == CategoryType.Variable || c.Type == CategoryType.FixedBill)
                        && c.CreatedUtc <= cutoffUtc && (c.KeptUnusedUtc == null || c.KeptUnusedUtc <= cutoffUtc))
            .OrderBy(c => c.SortOrder).ToListAsync(ct);
        cats = cats.Where(c => !CardInterest.IsInterestCategory(c.Name, c.Type)).ToList();
        if (cats.Count == 0) return [];

        var partnerLines = await db.PartnerArrangements.AsNoTracking().Where(a => a.HouseholdId == householdId).Select(a => a.CategoryId).ToListAsync(ct);
        var last = await LastUsedAsync(householdId, ct);
        var bills = cats.Any(c => c.Type == CategoryType.FixedBill) ? await recurring.CurrentBillsAsync(householdId, ct) : [];

        bool Unused(Category c)
        {
            if (partnerLines.Contains(c.Id)) return false;
            var used = last.GetValueOrDefault(c.Id);
            if (used is { } u && u > cutoff) return false;
            if (c.Type != CategoryType.FixedBill) return true;
            var detected = bills.Where(b => b.CategoryId == c.Id).ToList();
            return detected.Count > 0 ? detected.All(b => b.Stopped) : used is null || used <= today.AddMonths(-UnusedBillMonths);
        }

        return cats.Where(Unused).Select(c => new ArchivedCategoryDto(c.Id, c.Name, c.Type.ToString(), c.MonthlyLimitCents, last.GetValueOrDefault(c.Id))).ToList();
    }

    private async Task<Dictionary<Guid, DateOnly?>> LastUsedAsync(Guid householdId, CancellationToken ct) =>
        await db.Transactions.AsNoTracking().Where(t => t.HouseholdId == householdId && t.CategoryId != null)
            .GroupBy(t => t.CategoryId!.Value)
            .Select(g => new { g.Key, Last = g.Max(t => t.Date) })
            .ToDictionaryAsync(x => x.Key, x => (DateOnly?)x.Last, ct);
}
