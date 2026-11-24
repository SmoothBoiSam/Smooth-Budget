using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Payday;

/// <summary>One month of income on the Income page's chart: paychecks, friends' contributions and other income.</summary>
public record IncomeMonthDto(DateOnly Month, long PaycheckCents, long FriendCents, long OtherCents);

/// <param name="Kind">"paycheck", "friend" or "other".</param>
/// <param name="Said">You said what it is in Review; otherwise the app decided (a paycheck matched to its pay date, say).</param>
public record IncomeItemDto(Guid Id, DateOnly Date, long Cents, string Description, string Account, string Kind, string? FriendName, bool Said, bool NeedsReview);

public record IncomeSummaryDto(IReadOnlyList<IncomeMonthDto> Months, long ThisMonthCents, int ToReview);

/// <summary>
/// The Income page (DECISIONS.md 254): every deposit that's your income, by what it is. A paycheck is one you said was, or
/// one matched to a pay date, or from whoever paid an earlier matched check; a friend's contribution is theirs; the rest
/// is other income. The same split the budget uses, so the page and the month's money agree.
/// </summary>
public class IncomeHistory(AppDbContext db, TimeProvider clock)
{
    public const int MonthsShown = 12;

    public async Task<List<IncomeItemDto>> ItemsAsync(Guid householdId, DateOnly from, CancellationToken ct)
    {
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.AmountCents < 0 && !t.IsTransfer && t.Date >= from && t.Account.IncludedInBudget
                        && (t.Kind == TransactionKind.Income || t.Kind == TransactionKind.PartnerContribution))
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedUtc)
            .Select(t => new
            {
                t.Id, t.Date, t.AmountCents, t.Merchant, Account = t.Account.Nickname ?? t.Account.Name, t.Kind, t.IncomeType, t.CategorySource, t.NeedsReview,
                Friend = t.Friend != null ? t.Friend.Name : null,
            })
            .ToListAsync(ct);
        var linked = (await db.PayPeriodForecasts.AsNoTracking().Where(f => f.HouseholdId == householdId && f.DepositTransactionId != null)
            .Select(f => f.DepositTransactionId!.Value).ToListAsync(ct)).ToHashSet();
        // Who pays you: a deposit linked to a pay date, or one that matches a pay stub to the cent around its pay date (the
        // rule the month's other income uses, so the page and the budget agree).
        var stubs = await db.PayStubs.AsNoTracking().Where(s => s.HouseholdId == householdId && s.PayDate >= from.AddDays(-7))
            .Select(s => new { s.PayDate, s.NetCents }).ToListAsync(ct);
        var matched = rows.Where(r => r.Kind == TransactionKind.Income && stubs.Any(s => s.NetCents == -r.AmountCents
                && r.Date >= s.PayDate.AddDays(-2) && r.Date <= s.PayDate.AddDays(3))).Select(r => r.Id).ToHashSet();
        var payers = rows.Where(r => (linked.Contains(r.Id) || matched.Contains(r.Id)) && r.IncomeType != IncomeType.Other).Select(r => r.Merchant)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (payers.Count == 0)
        {
            // Nothing to go on yet (no stubs that match, nothing linked): whoever's deposits keep landing on your pay dates.
            var profile = await db.PayProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.HouseholdId == householdId, ct);
            var rules = profile is null ? Core.Pay.PayRules.Default : PaydayService.RulesOf(profile);
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var payDates = Core.Pay.PayCalendar.From(from.AddDays(-7), rules).TakeWhile(p => p.PayDate <= today.AddDays(3)).Select(p => p.PayDate).ToList();
            bool OnPayday(DateOnly d) => payDates.Any(p => d >= p.AddDays(-2) && d <= p.AddDays(3));
            payers = rows.Where(r => r.Kind == TransactionKind.Income && r.IncomeType != IncomeType.Other).GroupBy(r => r.Merchant, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count(r => OnPayday(r.Date)) is var on && on >= 2 && on * 3 >= g.Count() * 2)
                .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return rows.Select(r =>
        {
            var kind = r.Kind == TransactionKind.PartnerContribution ? "friend"
                : r.IncomeType == IncomeType.Paycheck ? "paycheck"
                : r.IncomeType == IncomeType.Other ? "other"
                : linked.Contains(r.Id) || matched.Contains(r.Id) || payers.Contains(r.Merchant) ? "paycheck" : "other";
            return new IncomeItemDto(r.Id, r.Date, -r.AmountCents, r.Merchant, r.Account, kind, r.Friend,
                r.CategorySource == CategorySource.User, r.NeedsReview);
        }).ToList();
    }

    public async Task<IncomeSummaryDto> SummaryAsync(Guid householdId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-(MonthsShown - 1));
        var items = await ItemsAsync(householdId, first, ct);
        var months = Enumerable.Range(0, MonthsShown).Select(i => first.AddMonths(i)).Select(m =>
        {
            var inMonth = items.Where(x => x.Date.Year == m.Year && x.Date.Month == m.Month).ToList();
            long Sum(string k) => inMonth.Where(x => x.Kind == k).Sum(x => x.Cents);
            return new IncomeMonthDto(m, Sum("paycheck"), Sum("friend"), Sum("other"));
        }).ToList();
        var now = months[^1];
        return new IncomeSummaryDto(months, now.PaycheckCents + now.FriendCents + now.OtherCents, items.Count(x => x.NeedsReview));
    }
}
