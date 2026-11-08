using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Home;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Bills;

/// <param name="Status">"active", "priceUp", "priceDown", "possiblyCanceled" or "canceled".</param>
/// <param name="InBillCategory">Its charges are filed in a bill category, so the budget plans for it as a bill.</param>
public record RecurringItemDto(
    string Key, string Name, string DetectedName, string Kind, string Frequency, string Status,
    long AmountCents, long? PreviousAmountCents, DateOnly? PriceChangedOn, long MonthlyCents,
    DateOnly LastDate, DateOnly NextExpected, int? DueDay, int Occurrences, string AccountName,
    Guid? CategoryId, string? CategoryName, bool InBillCategory, bool Confirmed, bool PossibleDuplicate, bool MerchantWide);

public record RecurringSummaryDto(
    long MonthlyCents, long AnnualCents, int AlertCount,
    IReadOnlyList<RecurringItemDto> Items, IReadOnlyList<RecurringItemDto> Dismissed);

public record UpcomingBillDto(string Name, long AmountCents, DateOnly Date);

/// <param name="AcknowledgePrice">"Got it" on a price change: the flag clears until the price changes again.</param>
public record RecurringUpdateRequest(
    string Key, string? Name = null, RecurringKind? Kind = null, int? DueDay = null, bool ClearDueDay = false,
    bool? Confirmed = null, bool? Dismissed = null, bool? Canceled = null, bool AcknowledgePrice = false);

public record TrackAsBillRequest(string Key);
public record MoveBillRequest(string Key, Guid CategoryId);

/// <summary>
/// Bills and subscriptions (SPEC 4.7): runs <see cref="RecurringDetector"/> over the last 18 months each time
/// (it's fast, and it never goes stale), then applies what you decided about each item.
/// </summary>
public class RecurringService(AppDbContext db, CategorizationService categorization, TimeProvider clock)
{
    public const int HistoryMonths = 18;

    public async Task<RecurringSummaryDto> SummaryAsync(Guid householdId, CancellationToken ct)
    {
        var all = await ItemsAsync(householdId, ct);
        var live = all.Where(i => !i.Dismissed).Select(i => i.Dto).ToList();
        var counting = live.Where(i => i.Status is not ("possiblyCanceled" or "canceled")).ToList();
        // Bills and subscriptions are one list now (DECISIONS.md 143): one monthly total, and what that is over a year.
        var monthly = counting.Sum(i => i.MonthlyCents);
        return new RecurringSummaryDto(
            monthly, monthly * 12,
            live.Count(i => i.Status is "priceUp" or "priceDown" or "possiblyCanceled" || i.PossibleDuplicate),
            live, all.Where(i => i.Dismissed).Select(i => i.Dto).ToList());
    }

    /// <summary>The next few bills and subscriptions due from today (Home, SPEC 4.2).</summary>
    public async Task<List<UpcomingBillDto>> UpcomingAsync(Guid householdId, int count, CancellationToken ct)
    {
        var today = await TodayAsync(householdId, ct);
        return (await ItemsAsync(householdId, ct))
            .Where(i => !i.Dismissed && i.Dto.Status is not ("possiblyCanceled" or "canceled") && i.Dto.NextExpected >= today)
            .OrderBy(i => i.Dto.NextExpected).Take(count)
            .Select(i => new UpcomingBillDto(i.Dto.Name, i.Dto.AmountCents, i.Dto.NextExpected)).ToList();
    }

    /// <summary>Current prices of the bills filed in bill categories, for the budget generator (milestone 6).</summary>
    public async Task<List<CurrentBill>> CurrentBillsAsync(Guid householdId, CancellationToken ct) =>
        (await ItemsAsync(householdId, ct))
            .Where(i => !i.Dismissed && i.Dto.InBillCategory && i.Dto.CategoryId is not null && i.Dto.Status is not ("possiblyCanceled" or "canceled"))
            .Select(i => new CurrentBill(i.Dto.CategoryId!.Value, i.Dto.Name, i.Dto.MonthlyCents)).ToList();

    public async Task<bool> UpdateAsync(Guid householdId, RecurringUpdateRequest req, CancellationToken ct)
    {
        var series = (await DetectAsync(householdId, ct)).FirstOrDefault(s => s.Key == req.Key);
        var row = await db.RecurringItems.SingleOrDefaultAsync(r => r.HouseholdId == householdId && r.Key == req.Key, ct);
        if (series is null && row is null)
            return false;
        if (row is null)
        {
            row = new RecurringItem { Id = Guid.NewGuid(), HouseholdId = householdId, Key = req.Key };
            db.RecurringItems.Add(row);
        }
        if (req.Name is not null) row.Name = string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim();
        if (req.Kind is { } kind) row.Kind = kind;
        if (req.DueDay is { } day) row.DueDay = day;
        if (req.ClearDueDay) row.DueDay = null;
        if (req.Confirmed is { } c) row.Confirmed = c;
        if (req.Dismissed is { } d) row.Dismissed = d;
        if (req.Canceled is { } canceled) row.CanceledOn = canceled ? await TodayAsync(householdId, ct) : null;
        if (req.AcknowledgePrice && series is not null) row.AcknowledgedAmountCents = series.AmountCents;
        row.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Gives a detected item its own bill line (DECISIONS.md 94): a bill category named after it, its charges moved
    /// there (yours to keep, since you asked), and a merchant rule so future charges follow, unless one merchant bills
    /// several things, where a rule can't tell them apart.
    /// </summary>
    public async Task<(Guid CategoryId, string Name)?> TrackAsBillAsync(Guid householdId, string key, CancellationToken ct)
    {
        var items = await ItemsAsync(householdId, ct);
        var item = items.FirstOrDefault(i => i.Dto.Key == key);
        if (item is null) return null;
        var name = item.Dto.Name.Length > 100 ? item.Dto.Name[..100] : item.Dto.Name;

        var categories = await db.Categories.Where(c => c.HouseholdId == householdId).ToListAsync(ct);
        var cat = categories.FirstOrDefault(c => c.Type == CategoryType.FixedBill && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (cat is null)
        {
            var unique = name;
            for (var n = 2; categories.Any(c => string.Equals(c.Name, unique, StringComparison.OrdinalIgnoreCase)); n++)
                unique = $"{name} ({n})";
            cat = new Category
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, Name = unique, Type = CategoryType.FixedBill,
                MonthlyLimitCents = item.Dto.MonthlyCents, SortOrder = categories.Count == 0 ? 0 : categories.Max(c => c.SortOrder) + 1,
            };
            db.Categories.Add(cat);
        }
        cat.IsHidden = false;
        await db.SaveChangesAsync(ct);
        await FileUnderAsync(householdId, item, cat.Id, "You made this its own bill.", ct);
        return (cat.Id, cat.Name);
    }

    /// <summary>
    /// Files a bill's charges under a category you pick (DECISIONS.md 146): its past charges move there, and a merchant
    /// rule sends future ones too (unless one merchant bills several things, where a rule can't tell them apart).
    /// </summary>
    public async Task<(Guid CategoryId, string Name)?> MoveToCategoryAsync(Guid householdId, string key, Guid categoryId, CancellationToken ct)
    {
        var cat = await db.Categories.SingleOrDefaultAsync(c => c.Id == categoryId && c.HouseholdId == householdId, ct);
        if (cat is null) return null;
        var item = (await ItemsAsync(householdId, ct)).FirstOrDefault(i => i.Dto.Key == key);
        if (item is null) return null;
        cat.IsHidden = false;
        await db.SaveChangesAsync(ct);
        await FileUnderAsync(householdId, item, cat.Id, $"You filed this bill under {cat.Name}.", ct);
        return (cat.Id, cat.Name);
    }

    private async Task FileUnderAsync(Guid householdId, Item item, Guid categoryId, string reason, CancellationToken ct)
    {
        var s = item.Series;
        var key = item.Dto.Key;
        if (item.Dto.MerchantWide)
        {
            var rule = await db.CategoryRules.SingleOrDefaultAsync(r => r.HouseholdId == householdId && r.Pattern == s.MerchantKey, ct);
            if (rule is null)
                db.CategoryRules.Add(rule = new CategoryRule { Id = Guid.NewGuid(), HouseholdId = householdId, Pattern = s.MerchantKey, CreatedUtc = clock.GetUtcNow().UtcDateTime });
            rule.CategoryId = categoryId;
        }
        var ids = s.TransactionIds.ToList();
        await db.Transactions.Where(t => t.HouseholdId == householdId && ids.Contains(t.Id))
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.CategoryId, categoryId).SetProperty(t => t.CategorySource, CategorySource.User)
                .SetProperty(t => t.NeedsReview, false).SetProperty(t => t.CategoryUnsure, false)
                .SetProperty(t => t.CategoryReason, reason), ct);
        var row = await db.RecurringItems.SingleOrDefaultAsync(r => r.HouseholdId == householdId && r.Key == key, ct);
        if (row is null)
            db.RecurringItems.Add(row = new RecurringItem { Id = Guid.NewGuid(), HouseholdId = householdId, Key = key });
        row.Confirmed = true;
        row.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await categorization.RunAsync(householdId, ct); // the rule picks up anything else from that merchant
    }

    private sealed record Item(RecurringSeries Series, RecurringItemDto Dto, bool Dismissed);

    private async Task<List<Item>> ItemsAsync(Guid householdId, CancellationToken ct)
    {
        var series = await DetectAsync(householdId, ct);
        var rows = await db.RecurringItems.AsNoTracking().Where(r => r.HouseholdId == householdId).ToDictionaryAsync(r => r.Key, ct);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.HouseholdId == householdId).ToDictionaryAsync(a => a.Id, ct);
        var categories = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == householdId).ToDictionaryAsync(c => c.Id, ct);
        var multi = series.GroupBy(s => s.MerchantKey).Where(g => g.Count() > 1 || g.Any(s => s.Key != s.MerchantKey)).Select(g => g.Key).ToHashSet();

        return series.Select(s =>
        {
            rows.TryGetValue(s.Key, out var r);
            var status = s.Status switch
            {
                RecurringStatus.PossiblyCanceled => "possiblyCanceled",
                RecurringStatus.PriceChanged when r?.AcknowledgedAmountCents != s.AmountCents =>
                    s.AmountCents > s.PreviousAmountCents ? "priceUp" : "priceDown",
                _ => "active",
            };
            if (r?.CanceledOn is { } canceledOn && s.LastDate <= canceledOn)
                status = "canceled";
            var next = r?.DueDay is { } due && s.Frequency == Frequency.Monthly ? DueAfter(s.LastDate, due) : s.NextExpected;
            var cat = s.CategoryId is { } cid ? categories.GetValueOrDefault(cid) : null;
            var account = accounts.GetValueOrDefault(s.AccountId);
            var dto = new RecurringItemDto(
                s.Key, r?.Name ?? s.Name, s.Name, (r?.Kind ?? s.Kind).ToString(), s.Frequency.ToString(), status,
                s.AmountCents, s.PreviousAmountCents, s.PriceChangedOn, s.MonthlyCents, s.LastDate, next, r?.DueDay, s.Occurrences,
                account is null ? "" : account.Mask is null ? account.Name : $"{account.Name} ••{account.Mask}",
                cat?.Id, cat?.Name, cat?.Type == CategoryType.FixedBill, r?.Confirmed ?? false, s.PossibleDuplicate, !multi.Contains(s.MerchantKey));
            return new Item(s, dto, r?.Dismissed ?? false);
        }).ToList();
    }

    private async Task<IReadOnlyList<RecurringSeries>> DetectAsync(Guid householdId, CancellationToken ct)
    {
        var today = await TodayAsync(householdId, ct);
        var since = today.AddMonths(-HistoryMonths);
        var txns = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Date >= since && t.Account.IncludedInBudget && !t.IsTransfer
                        && t.AmountCents > 0 && (t.Kind == TransactionKind.Purchase || t.Kind == TransactionKind.Other))
            .Select(t => new RecurringTxn(t.Id, t.Date, t.AmountCents, t.Merchant, t.AccountId, t.CategoryId,
                t.Category != null ? t.Category.Type : null))
            .ToListAsync(ct);
        return RecurringDetector.Detect(txns, today);
    }

    /// <summary>The first due day after the last charge (a due day past the end of a short month falls on its last day).</summary>
    private static DateOnly DueAfter(DateOnly last, int day)
    {
        DateOnly In(DateOnly m) => new(m.Year, m.Month, Math.Min(day, DateTime.DaysInMonth(m.Year, m.Month)));
        var candidate = In(last);
        return candidate > last ? candidate : In(last.AddMonths(1));
    }

    private async Task<DateOnly> TodayAsync(Guid householdId, CancellationToken ct) =>
        HomeSummaryService.TodayIn(await db.Households.Where(h => h.Id == householdId).Select(h => h.TimeZoneId).SingleAsync(ct), clock);
}
