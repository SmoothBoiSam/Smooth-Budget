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
    Guid? CategoryId, string? CategoryName, bool InBillCategory, bool Confirmed, bool PossibleDuplicate, bool MerchantWide,
    IReadOnlyList<BillChargeDto> Charges,
    SameBillDto? SameAs = null, string? ReplacedBy = null,
    // Bills you joined (DECISIONS.md 197): how many were joined into this one (Split apart undoes it), and whether the
    // price varies from charge to charge, so it's planned at its usual amount instead of flagged as a price change.
    int MergedCount = 0, bool Varies = false,
    // "Still going: keep looking" (DECISIONS.md 200): on since this day; Found = charges it has picked up since its old last one.
    DateOnly? LookingSince = null, int Found = 0);

/// <summary>
/// On the newer of two bills that may be one bill whose price changed (DECISIONS.md 181): the older one. That one carries
/// <see cref="RecurringItemDto.ReplacedBy"/> and isn't asked about on its own until you answer.
/// </summary>
public record SameBillDto(string Key, string Name, long AmountCents, DateOnly FirstDate, DateOnly LastDate, int Occurrences);

/// <summary>One charge of a bill, newest first in <see cref="RecurringItemDto.Charges"/> (DECISIONS.md 167).</summary>
public record BillChargeDto(DateOnly Date, long AmountCents);

public record RecurringSummaryDto(
    long MonthlyCents, long AnnualCents, int AlertCount,
    IReadOnlyList<RecurringItemDto> Items, IReadOnlyList<RecurringItemDto> Dismissed);

public record UpcomingBillDto(string Name, long AmountCents, DateOnly Date);

/// <param name="AcknowledgePrice">"Got it" on a price change: the flag clears until the price changes again.</param>
/// <param name="MergeInto">This bill is the same as that one (its price changed): its charges join it (DECISIONS.md 181).</param>
/// <param name="NotSameAs">This bill isn't the same as that one: stop asking.</param>
/// <param name="Split">Undo joining: every bill joined into this one is its own again, and isn't suggested again.</param>
/// <param name="KeepLooking">It's still going: find its later charges, even at a new price (DECISIONS.md 200). False stops looking.</param>
public record RecurringUpdateRequest(
    string Key, string? Name = null, RecurringKind? Kind = null, int? DueDay = null, bool ClearDueDay = false,
    bool? Confirmed = null, bool? Dismissed = null, bool? Canceled = null, bool AcknowledgePrice = false,
    string? MergeInto = null, string? NotSameAs = null, bool Split = false, bool? KeepLooking = null);

/// <summary>"Not a bill" (DECISIONS.md 197): its charges go to this spending category and it's no longer expected.</summary>
public record NotABillRequest(string Key, Guid CategoryId);

public record TrackAsBillRequest(string Key);

/// <summary>A bill a charge in review can join (DECISIONS.md 225).</summary>
public record BillChoiceDto(string Key, string Name, string? CategoryName, long AmountCents);
/// <summary>A bill line in your budget a charge can go in as a bill of its own.</summary>
public record BillLineDto(Guid Id, string Name);
/// <param name="Current">The bill this charge already belongs to, when it's filed as a bill.</param>
/// <param name="Detected">A repeating charge the app found this in that isn't a bill yet: a new bill, most likely.</param>
/// <param name="Suggested">A name for a new bill.</param>
public record ChargeBillChoicesDto(IReadOnlyList<BillChoiceDto> Bills, IReadOnlyList<BillLineDto> Lines, string? Current, string? Detected, string? Suggested);
/// <summary>A charge in review is a bill (DECISIONS.md 225, 230): <paramref name="BillKey"/> (it's that bill), or a new bill
/// named <paramref name="Name"/>, in the bill category <paramref name="CategoryId"/> or, without one, a new bill category of its own.</summary>
public record FileChargeAsBillRequest(Guid TransactionId, string? BillKey = null, Guid? CategoryId = null, string? Name = null);
/// <param name="Moved">Other charges that moved to that line with it.</param>
public record FiledAsBillDto(Guid CategoryId, string CategoryName, int Moved);
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
            live.Count(i => i.Status is "priceUp" or "priceDown" || (i.Status is "possiblyCanceled" && i.ReplacedBy is null)
                            || i.PossibleDuplicate || i.SameAs is not null),
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

    /// <summary>
    /// Current prices of the bills filed in bill categories, for the budget generator (milestone 6). Stopped ones come too,
    /// marked, so their category stops planning for them (DECISIONS.md 174).
    /// </summary>
    public async Task<List<CurrentBill>> CurrentBillsAsync(Guid householdId, CancellationToken ct) =>
        (await ItemsAsync(householdId, ct))
            .Where(i => !i.Dismissed && i.Dto.InBillCategory && i.Dto.CategoryId is not null)
            .Select(i => new CurrentBill(i.Dto.CategoryId!.Value, i.Dto.Name, i.Dto.MonthlyCents, i.Dto.Status is "possiblyCanceled" or "canceled")).ToList();

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
        if (req.NotSameAs is { } other) row.NotSameAsKey = other;
        if (req.KeepLooking is { } look)
        {
            row.KeepLookingOn = look ? await TodayAsync(householdId, ct) : null;
            if (look) row.CanceledOn = null; // you said it's still going
        }
        row.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
        if (req.MergeInto is { } into && into != req.Key)
        {
            // The bill it joins keeps what you said about this one, where it has nothing of its own.
            var target = await db.RecurringItems.SingleOrDefaultAsync(r => r.HouseholdId == householdId && r.Key == into, ct);
            if (target is null)
                db.RecurringItems.Add(target = new RecurringItem { Id = Guid.NewGuid(), HouseholdId = householdId, Key = into });
            target.Name ??= row.Name;
            target.Kind ??= row.Kind;
            target.DueDay ??= row.DueDay;
            target.Confirmed |= row.Confirmed;
            target.UpdatedUtc = row.UpdatedUtc;
            row.MergedIntoKey = into;
            row.NotSameAsKey = null;
        }
        if (req.Split)
        {
            var joined = await db.RecurringItems.Where(r => r.HouseholdId == householdId && r.MergedIntoKey == req.Key).ToListAsync(ct);
            foreach (var j in joined)
            {
                j.MergedIntoKey = null;
                j.NotSameAsKey = req.Key; // you said they're different: don't suggest joining them again
                j.UpdatedUtc = row.UpdatedUtc;
            }
        }
        await db.SaveChangesAsync(ct);
        if (req.MergeInto is not null)
            await KeepBillsTogetherAsync(householdId, ct); // the old charges follow the bill line you filed it under
        return true;
    }

    /// <summary>
    /// "Not a bill" (DECISIONS.md 197): it never was one. Its charges move to the spending category you pick (with a
    /// merchant rule for future ones, as when filing a bill), and it leaves the bills: nothing expects it any more.
    /// It's listed under "Not bills", where Restore brings it back.
    /// </summary>
    public async Task<string?> NotABillAsync(Guid householdId, string key, Guid categoryId, CancellationToken ct)
    {
        var cat = await db.Categories.SingleOrDefaultAsync(c => c.Id == categoryId && c.HouseholdId == householdId, ct);
        if (cat is null || cat.Type != CategoryType.Variable) return null;
        var item = (await ItemsAsync(householdId, ct)).FirstOrDefault(i => i.Dto.Key == key);
        if (item is null) return null;
        cat.IsHidden = false;
        await db.SaveChangesAsync(ct);
        await FileUnderAsync(householdId, item, cat.Id, $"Not a bill: you filed it under {cat.Name}.", ct);
        var row = await db.RecurringItems.SingleAsync(r => r.HouseholdId == householdId && r.Key == key, ct);
        row.Dismissed = true;
        row.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return cat.Name;
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
        var cat = await BillLineAsync(householdId, item.Dto.Name, item.Dto.MonthlyCents, ct);
        await FileUnderAsync(householdId, item, cat.Id, "You made this its own bill.", ct);
        return (cat.Id, cat.Name);
    }

    /// <summary>The bill category with this name (shown again if hidden), or a new one at the end of your budget.</summary>
    private async Task<Category> BillLineAsync(Guid householdId, string name, long monthlyCents, CancellationToken ct)
    {
        name = name.Length > 100 ? name[..100] : name;
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
                MonthlyLimitCents = monthlyCents, SortOrder = categories.Count == 0 ? 0 : categories.Max(c => c.SortOrder) + 1,
            };
            db.Categories.Add(cat);
        }
        cat.IsHidden = false;
        await db.SaveChangesAsync(ct);
        return cat;
    }

    /// <summary>
    /// What a charge in review can be filed as, when you say it's a bill (DECISIONS.md 225): your bills (the ones filed
    /// in a bill line), your bill lines that aren't just one bill of the same name, and the bill it already belongs to.
    /// </summary>
    public async Task<ChargeBillChoicesDto?> BillChoicesAsync(Guid householdId, Guid transactionId, CancellationToken ct)
    {
        var merchant = await db.Transactions.AsNoTracking().Where(x => x.Id == transactionId && x.HouseholdId == householdId)
            .Select(x => x.Merchant).SingleOrDefaultAsync(ct);
        if (merchant is null) return null;
        var items = (await ItemsAsync(householdId, ct)).Where(i => !i.Dismissed).ToList();
        var own = items.FirstOrDefault(i => i.Series.TransactionIds.Contains(transactionId));
        var bills = items.Where(i => i.Dto.InBillCategory && (i == own || (i.Dto.Status != "canceled" && i.Dto.ReplacedBy is null)))
            .OrderBy(i => i.Dto.Name, StringComparer.OrdinalIgnoreCase)
            .Select(i => new BillChoiceDto(i.Dto.Key, i.Dto.Name, i.Dto.CategoryName, i.Dto.AmountCents)).ToList();
        var lines = await db.Categories.AsNoTracking()
            .Where(c => c.HouseholdId == householdId && c.Type == CategoryType.FixedBill && !c.IsHidden && c.Name != CardInterest.CategoryName)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(c => new BillLineDto(c.Id, c.Name)).ToListAsync(ct);
        lines = lines.Where(l => !bills.Any(b => b.CategoryName == l.Name && string.Equals(b.Name, l.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        return new ChargeBillChoicesDto(bills, lines,
            own is { Dto.InBillCategory: true } ? own.Dto.Key : null,
            own is { Dto.InBillCategory: false } ? own.Dto.Key : null,
            own?.Dto.Name);
    }

    /// <summary>
    /// Files a charge from review as a bill (DECISIONS.md 225). Joining a bill you have: it goes in that bill's line, and
    /// if the app saw it repeating on its own (a new price, a new card) that series joins the bill. A bill of its own in
    /// one of your bill lines, or a new bill with its own line: it goes there, and so do its earlier charges. Either way
    /// the bill's charges stay together (DECISIONS.md 158), with a merchant rule for future ones where that's safe.
    /// </summary>
    public async Task<FiledAsBillDto?> FileChargeAsBillAsync(Guid householdId, FileChargeAsBillRequest req, CancellationToken ct)
    {
        var t = await db.Transactions.SingleOrDefaultAsync(x => x.Id == req.TransactionId && x.HouseholdId == householdId, ct);
        if (t is null || t.IsTransfer) return null;
        var items = (await ItemsAsync(householdId, ct)).Where(i => !i.Dismissed).ToList();
        var own = items.FirstOrDefault(i => i.Series.TransactionIds.Contains(t.Id));

        Category? cat;
        string reason;
        Item? bill = null;
        if (req.BillKey is { } key)
        {
            bill = items.FirstOrDefault(i => i.Dto.Key == key && i.Dto.InBillCategory);
            if (bill?.Dto.CategoryId is not { } billCat) return null;
            cat = await db.Categories.SingleAsync(c => c.Id == billCat, ct);
            reason = $"You said it's {bill.Dto.Name}.";
        }
        else
        {
            var name = req.Name?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            if (req.CategoryId is { } lineId)
            {
                cat = await db.Categories.SingleOrDefaultAsync(c => c.Id == lineId && c.HouseholdId == householdId && c.Type == CategoryType.FixedBill, ct);
                if (cat is null) return null;
                reason = $"A new bill, {name}, in {cat.Name}.";
            }
            else
            {
                cat = await BillLineAsync(householdId, name, own?.Dto.MonthlyCents ?? Math.Max(0, t.AmountCents), ct);
                reason = "You made this its own bill.";
            }
            if (own is not null && name != own.Dto.Name)
                await UpdateAsync(householdId, new RecurringUpdateRequest(own.Dto.Key, Name: name), ct);
        }
        cat.IsHidden = false;
        var before = await db.Transactions.CountAsync(x => x.HouseholdId == householdId && x.CategoryId == cat.Id && x.Id != t.Id, ct);

        (t.CategoryId, t.SuggestedCategoryId, t.CategorySource, t.NeedsReview, t.CategoryUnsure, t.CategoryReason) =
            (cat.Id, null, CategorySource.User, false, false, reason);
        if (t.Kind == TransactionKind.PartnerTransfer)
            t.Kind = t.AmountCents < 0 ? TransactionKind.Refund : TransactionKind.Purchase;
        await db.SaveChangesAsync(ct);

        if (bill is not null)
        {
            // The app saw this charge repeat as a bill of its own: that's this bill (a new price, a new card), so they join.
            if (own is not null && own.Dto.Key != bill.Dto.Key)
                await UpdateAsync(householdId, new RecurringUpdateRequest(own.Dto.Key, MergeInto: bill.Dto.Key, Confirmed: true), ct);
        }
        else if (own is not null)
            await FileUnderAsync(householdId, own, cat.Id, reason, ct);
        await KeepBillsTogetherAsync(householdId, ct, t.Id);

        var after = await db.Transactions.CountAsync(x => x.HouseholdId == householdId && x.CategoryId == cat.Id && x.Id != t.Id, ct);
        return new FiledAsBillDto(cat.Id, cat.Name, Math.Max(0, after - before));
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

    /// <summary>
    /// A bill's charges stay together (DECISIONS.md 158): once one of a bill's charges is filed under a bill category by
    /// you, its other charges follow (and a merchant rule sends future ones), so that bill line's budget sees the whole
    /// bill. With <paramref name="filedId"/> (the charge you just filed) every other charge of that bill follows, and if
    /// it isn't part of a detected bill yet, earlier charges from the same merchant for the same amount do. Without it
    /// (the regular check), only charges you haven't filed yourself move.
    /// </summary>
    /// <returns>How many charges moved.</returns>
    public async Task<int> KeepBillsTogetherAsync(Guid householdId, CancellationToken ct, Guid? filedId = null)
    {
        var items = (await ItemsAsync(householdId, ct)).Where(i => !i.Dismissed).ToList();
        if (filedId is { } only)
            items = items.Where(i => i.Series.TransactionIds.Contains(only)).ToList();
        var ids = items.SelectMany(i => i.Series.TransactionIds).Append(filedId ?? Guid.Empty).Distinct().ToList();
        var rows = await db.Transactions.AsNoTracking().Where(t => t.HouseholdId == householdId && ids.Contains(t.Id))
            .Select(t => new BillCharge(t.Id, t.Date, t.AmountCents, t.Merchant, t.CategoryId, t.CategorySource,
                t.Category != null && t.Category.Type == CategoryType.FixedBill, t.Category != null ? t.Category.Name : null))
            .ToDictionaryAsync(t => t.Id, ct);

        var moved = 0;
        foreach (var item in items)
        {
            var charges = item.Series.TransactionIds.Select(rows.GetValueOrDefault).OfType<BillCharge>().ToList();
            var anchor = filedId is { } f
                ? charges.FirstOrDefault(c => c.Id == f)
                : charges.Where(c => c.Source == CategorySource.User && c.InBill).MaxBy(c => c.Date);
            if (anchor is not { InBill: true, CategoryId: { } categoryId }) continue;
            var move = charges.Where(c => c.CategoryId != categoryId && (filedId is not null || c.Source != CategorySource.User)).Select(c => c.Id).ToList();
            if (move.Count == 0) continue;
            await FileUnderAsync(householdId, item, categoryId, $"Same bill as the one you filed under {anchor.CategoryName}.", ct, move);
            moved += move.Count;
        }

        // Not a detected bill yet (too few charges, or irregular dates): earlier charges from that merchant for the same amount.
        if (filedId is { } id && items.Count == 0 && rows.GetValueOrDefault(id) is { InBill: true, CategoryId: { } cat, AmountCents: > 0 } filed)
        {
            moved += await db.Transactions
                .Where(t => t.HouseholdId == householdId && t.Id != id && t.Merchant == filed.Merchant && t.AmountCents == filed.AmountCents
                            && !t.IsTransfer && t.CategorySource != CategorySource.User && t.CategoryId != cat)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(t => t.CategoryId, cat).SetProperty(t => t.CategorySource, CategorySource.User)
                    .SetProperty(t => t.NeedsReview, false).SetProperty(t => t.CategoryUnsure, false)
                    .SetProperty(t => t.CategoryReason, $"Same bill as the one you filed under {filed.CategoryName}."), ct);
        }
        return moved;
    }

    private sealed record BillCharge(Guid Id, DateOnly Date, long AmountCents, string Merchant, Guid? CategoryId, CategorySource Source, bool InBill, string? CategoryName);

    private async Task FileUnderAsync(Guid householdId, Item item, Guid categoryId, string reason, CancellationToken ct, IReadOnlyList<Guid>? only = null)
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
        var ids = (only ?? s.TransactionIds).ToList();
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
        var txns = await RecentAsync(householdId, ct);
        var rows = await db.RecurringItems.AsNoTracking().Where(r => r.HouseholdId == householdId).ToDictionaryAsync(r => r.Key, ct);
        var byId = txns.ToDictionary(t => t.Id);
        var today = await TodayAsync(householdId, ct);
        var (series, joined) = ApplyMerges(RecurringDetector.Detect(txns, today), rows, byId);

        // Bills you said are still going (DECISIONS.md 200): find their later charges, even at a new price.
        var found = new Dictionary<string, int>();
        foreach (var key in series.Where(s => rows.GetValueOrDefault(s.Key)?.KeepLookingOn is not null).Select(s => s.Key).ToList())
        {
            var at = series.FindIndex(s => s.Key == key);
            if (at < 0) continue; // taken in by another bill you're looking for
            var look = RecurringDetector.KeepLooking(series[at], txns, series, today);
            series[at] = look.Series;
            series.RemoveAll(s => look.Absorbed.Contains(s.Key));
            found[key] = look.Found;
            if (look.Varies)
                joined[key] = (joined.GetValueOrDefault(key).Count, true);
        }

        // Two bills that may be one whose price changed: ask on the newer one, and hold the older one's own question.
        var sameAs = new Dictionary<string, RecurringSeries>();
        var replacedBy = new Dictionary<string, string>();
        foreach (var (older, newer) in RecurringDetector.SameBillCandidates(series))
        {
            var o = rows.GetValueOrDefault(older.Key);
            if (o?.Dismissed == true || rows.GetValueOrDefault(newer.Key)?.Dismissed == true || o?.NotSameAsKey == newer.Key) continue;
            if (sameAs.TryGetValue(newer.Key, out var had) && had.LastDate >= older.LastDate) continue;
            sameAs[newer.Key] = older;
            replacedBy[older.Key] = newer.Key;
        }
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
                cat?.Id, cat?.Name, cat?.Type == CategoryType.FixedBill, r?.Confirmed ?? false, s.PossibleDuplicate, !multi.Contains(s.MerchantKey),
                s.TransactionIds.Select(byId.GetValueOrDefault).OfType<RecurringTxn>().OrderByDescending(t => t.Date)
                    .Select(t => new BillChargeDto(t.Date, t.AmountCents)).ToList(),
                sameAs.GetValueOrDefault(s.Key) is { } old
                    ? new SameBillDto(old.Key, rows.GetValueOrDefault(old.Key)?.Name ?? old.Name, old.AmountCents, old.FirstDate, old.LastDate, old.Occurrences)
                    : null,
                replacedBy.GetValueOrDefault(s.Key),
                joined.GetValueOrDefault(s.Key).Count, joined.GetValueOrDefault(s.Key).Varies,
                r?.KeepLookingOn, found.GetValueOrDefault(s.Key));
            return new Item(s, dto, r?.Dismissed ?? false);
        }).ToList();
    }

    /// <summary>
    /// Bills you said are one bill (DECISIONS.md 181) become one: the newest keeps its key, takes every charge, starts
    /// at the first, and shows the old price as the one before. A chain (a second price change) joins up the same way.
    /// When the latest charges don't share a price (a bill whose amount varies), it's planned at the median of its last 6
    /// charges and never flagged as a price change (DECISIONS.md 197).
    /// </summary>
    private static (List<RecurringSeries> Series, Dictionary<string, (int Count, bool Varies)> Joined) ApplyMerges(
        IReadOnlyList<RecurringSeries> detected, IReadOnlyDictionary<string, RecurringItem> rows, IReadOnlyDictionary<Guid, RecurringTxn> byId)
    {
        var joined = new Dictionary<string, (int Count, bool Varies)>();
        var byKey = detected.ToDictionary(s => s.Key);
        string Root(string key)
        {
            var k = key;
            for (var n = 0; n < 20 && rows.TryGetValue(k, out var r) && r.MergedIntoKey is { } next && next != k && byKey.ContainsKey(next); n++)
                k = next;
            return k;
        }

        var result = new List<RecurringSeries>();
        foreach (var group in detected.GroupBy(s => Root(s.Key)))
        {
            var target = byKey[group.Key];
            var others = group.Where(s => s.Key != target.Key).ToList();
            if (others.Count == 0)
            {
                result.Add(target);
                continue;
            }
            var before = others.MaxBy(s => s.LastDate)!;
            var previous = target.PreviousAmountCents ?? (before.AmountCents != target.AmountCents ? before.AmountCents : null);
            var ids = group.SelectMany(s => s.TransactionIds).Distinct().ToList();
            var amounts = ids.Select(byId.GetValueOrDefault).OfType<RecurringTxn>().OrderByDescending(t => t.Date).Select(t => t.AmountCents).ToList();
            var latest = amounts.Count > 0 ? amounts[0] : target.AmountCents;
            var varies = amounts.Count >= 3 && amounts.Take(3).Any(a => Math.Abs(a - latest) > Math.Max(50, latest / 50));
            var usual = varies ? Median(amounts.Take(6).ToList()) : target.AmountCents;
            joined[target.Key] = (others.Count, varies);
            result.Add(target with
            {
                FirstDate = group.Min(s => s.FirstDate),
                Occurrences = group.Sum(s => s.Occurrences),
                TransactionIds = ids,
                AmountCents = usual,
                MonthlyCents = varies ? RecurringDetector.MonthlyCents(usual, target.Frequency) : target.MonthlyCents,
                Status = varies && target.Status == RecurringStatus.PriceChanged ? RecurringStatus.Active : target.Status,
                PreviousAmountCents = varies ? null : previous,
                PriceChangedOn = varies ? null : target.PriceChangedOn ?? (previous is null ? null : target.FirstDate),
                CategoryId = target.CategoryId ?? before.CategoryId,
            });
        }
        return (result.OrderBy(s => s.NextExpected).ThenBy(s => s.Name, StringComparer.Ordinal).ToList(), joined);
    }

    private static long Median(List<long> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private async Task<IReadOnlyList<RecurringSeries>> DetectAsync(Guid householdId, CancellationToken ct) =>
        RecurringDetector.Detect(await RecentAsync(householdId, ct), await TodayAsync(householdId, ct));

    private async Task<List<RecurringTxn>> RecentAsync(Guid householdId, CancellationToken ct)
    {
        var since = (await TodayAsync(householdId, ct)).AddMonths(-HistoryMonths);
        return await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Date >= since && t.Account.IncludedInBudget && !t.IsTransfer
                        && t.AmountCents > 0 && (t.Kind == TransactionKind.Purchase || t.Kind == TransactionKind.Other))
            .Select(t => new RecurringTxn(t.Id, t.Date, t.AmountCents, t.Merchant, t.AccountId, t.CategoryId,
                t.Category != null ? t.Category.Type : null))
            .ToListAsync(ct);
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
