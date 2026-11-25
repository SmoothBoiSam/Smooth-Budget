using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Plaid;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Debt;
using SmoothBudget.Core.Security;

namespace SmoothBudget.Api.Debt;

/// <param name="NeedsDetails">APR or minimum missing, so it isn't in the payoff plan yet.</param>
/// <param name="PaidOffBy">The month it reaches $0 under your plan.</param>
public record DebtDto(
    Guid Id, string Name, string? Mask, bool IsCard, long BalanceCents, int? AprBps, long? MinimumCents, int? DueDay, DateOnly? NextDueDate,
    long? CreditLimitCents, int? UtilizationPercent, long StartingBalanceCents, DateOnly StartedOn, bool FromBank, bool IsHidden,
    bool NeedsDetails, DateOnly? PaidOffBy, long InterestCents,
    // The balance comes from the bank (a linked card), so it isn't editable here.
    bool BalanceFromBank = false,
    // Interest charged on it this year, and the latest charge (DECISIONS.md 232).
    long InterestChargedThisYearCents = 0, long? LastInterestCents = null, DateOnly? LastInterestDate = null,
    // A card that isn't bank-linked (DECISIONS.md 253): the balance you typed and when; what's owed follows from there.
    long? TypedBalanceCents = null, DateOnly? TypedOn = null);

public record ChartPoint(DateOnly Month, long BalanceCents);

/// <param name="ExtraSource">"budget" (what your budget plans for card payments, minus the minimums: your split's debt share, DECISIONS.md 321) or "you" (a what-if).</param>
public record DebtPlanDto(
    string Strategy, long MinimumsCents, long ExtraCents, string ExtraSource, long MonthlyPaymentCents, int Months, DateOnly? DebtFreeBy,
    bool NeverPaysOff, long TotalInterestCents, string? FirstTarget, IReadOnlyList<ChartPoint> Chart);

/// <param name="InterestDifferenceCents">The other strategy's interest minus this plan's (positive = this plan saves that much).</param>
public record DebtComparisonDto(string Strategy, int Months, DateOnly? DebtFreeBy, bool NeverPaysOff, long TotalInterestCents, long InterestDifferenceCents, int MonthsDifference);

/// <param name="History">The last 12 months, oldest first: what you owed at the end of each and what you paid that month.</param>
public record DebtSummaryDto(
    long OwedCents, long StartingCents, long PaidDownCents, long MinimumsCents, int NeedsDetailsCount,
    IReadOnlyList<DebtDto> Debts, DebtPlanDto? Plan, DebtComparisonDto? Comparison, IReadOnlyList<DebtHistoryPoint>? History = null);

/// <param name="Month">The first day of the month.</param>
/// <param name="OwedCents">Owed at the end of the month: cards worked back from their transactions, loans at today's balance.</param>
/// <param name="PaidCents">Card payments received that month.</param>
public record DebtHistoryPoint(DateOnly Month, long OwedCents, long PaidCents);

public record DebtRequest(string Name, long? BalanceCents, int? AprBps, long? MinimumCents, int? DueDay, long? CreditLimitCents, long? StartingBalanceCents, bool IsHidden = false);
public record DebtSettingsRequest(DebtStrategy Strategy);
public record FromBankResult(int Updated, string? Message);

/// <summary>
/// The Debt tab (SPEC 4.4): your cards (followed from their accounts) and loans, the payoff plan under your
/// strategy with its debt-free date, the other strategy for comparison, and progress since tracking started.
/// The math is <see cref="DebtPayoff"/>; this only gathers the inputs.
/// </summary>
public class DebtService(AppDbContext db, BudgetMonthService months, PlaidClient plaid, SecretBox secrets, TimeProvider clock, ILogger<DebtService> log)
{
    /// <summary>Every credit card gets a debt row the first time it's seen, and card balances follow their accounts.</summary>
    public async Task<List<DebtAccount>> SyncCardsAsync(Guid householdId, CancellationToken ct)
    {
        var today = await TodayAsync(householdId, ct);
        var cards = await db.Accounts.Where(a => a.HouseholdId == householdId && a.Type == AccountType.CreditCard && a.IncludedInBudget && a.ClosedOn == null).ToListAsync(ct);
        var debts = await db.DebtAccounts.Include(d => d.Account).Where(d => d.HouseholdId == householdId).ToListAsync(ct);
        foreach (var card in cards)
        {
            var owed = Math.Max(0, -card.CurrentBalanceCents);
            var d = debts.FirstOrDefault(x => x.AccountId == card.Id);
            if (d is null)
            {
                d = new DebtAccount
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, AccountId = card.Id, Account = card, Name = card.Name,
                    BalanceCents = owed, StartingBalanceCents = owed, StartedOn = today, CreatedUtc = clock.GetUtcNow().UtcDateTime,
                };
                db.DebtAccounts.Add(d);
                debts.Add(d);
            }
            if (card.PlaidAccountId is not null)
                d.BalanceCents = owed; // a bank-linked card follows its account; a CSV-only card's balance is yours to enter
            if (card.CreditLimitCents is { } limit)
                d.CreditLimitCents = limit;
        }
        await FollowTypedBalancesAsync(householdId, debts, ct);
        await db.SaveChangesAsync(ct);
        // A closed card leaves Debt (DECISIONS.md 318); its payments stay in the months they were made.
        return debts.Where(d => d.AccountId is null || d.Account!.IncludedInBudget && d.Account.ClosedOn == null).ToList();
    }

    /// <summary>
    /// A card that isn't bank-linked follows what the app can see (DECISIONS.md 253): the balance you typed, plus what was
    /// charged to it on later days (purchases, fees and interest, less refunds, from its uploads), less what it was paid
    /// on later days. A payment counts as it leaves checking, before the card's next upload shows it
    /// (<see cref="CardPayments"/>, which never counts both sides). Typing the balance again starts over from that day.
    /// </summary>
    private async Task FollowTypedBalancesAsync(Guid householdId, List<DebtAccount> debts, CancellationToken ct)
    {
        var typed = debts.Where(d => d.Account is { PlaidAccountId: null } && d.TypedOn is not null && d.TypedBalanceCents is not null).ToList();
        if (typed.Count == 0) return;
        var from = typed.Min(d => d.TypedOn!.Value).AddDays(1);
        var cardIds = typed.Select(d => d.AccountId!.Value).ToList();
        var charges = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && cardIds.Contains(t.AccountId) && t.Date >= from && !t.IsTransfer
                        && t.Kind != Core.Transactions.TransactionKind.CardPayment)
            .Select(t => new { t.AccountId, t.Date, t.AmountCents }).ToListAsync(ct);
        var payments = await CardPayments.LoadAsync(db, householdId, from, null, ct);
        foreach (var d in typed)
        {
            var on = d.TypedOn!.Value;
            d.BalanceCents = Math.Max(0, d.TypedBalanceCents!.Value
                + charges.Where(t => t.AccountId == d.AccountId && t.Date > on).Sum(t => t.AmountCents)
                - payments.Where(p => p.CardAccountId == d.AccountId && p.Date > on).Sum(p => p.Cents));
        }
    }

    public async Task<DebtSummaryDto> SummaryAsync(Guid householdId, long? extraOverride, DebtStrategy? strategyOverride, CancellationToken ct)
    {
        var debts = await SyncCardsAsync(householdId, ct);
        var h = await db.Households.AsNoTracking().SingleAsync(x => x.Id == householdId, ct);
        var strategy = strategyOverride ?? h.DebtStrategy;
        var today = Home.HomeSummaryService.TodayIn(h.TimeZoneId, clock);

        var visible = debts.Where(d => !d.IsHidden).ToList();
        var ready = visible.Where(d => d.BalanceCents > 0 && d.AprBps is not null && d.MinimumPaymentCents is not null).ToList();
        var inputs = ready.Select(Input).ToList();
        var minimums = inputs.Sum(i => Math.Min(i.MinimumCents, i.BalanceCents));

        var (extra, source) = extraOverride is { } e ? (e, "you")
            : (Math.Max(0, await PlannedCardPaymentsAsync(householdId, ct) - minimums - await ShortAsync(h, ct)), "budget");
        extra = Math.Max(0, extra);

        PayoffResult? plan = inputs.Count > 0 ? DebtPayoff.Simulate(inputs, extra, strategy) : null;
        var other = strategy == DebtStrategy.Avalanche ? DebtStrategy.Snowball : DebtStrategy.Avalanche;
        PayoffResult? alt = inputs.Count > 0 ? DebtPayoff.Simulate(inputs, extra, other) : null;
        var start = new DateOnly(today.Year, today.Month, 1);
        DateOnly? MonthOf(int? m) => m is { } n ? start.AddMonths(n) : null;

        // Interest charged, from the Card interest line's charges tied to each debt (DECISIONS.md 232).
        var interestLine = await db.Categories.AsNoTracking()
            .Where(c => c.HouseholdId == householdId && c.Type == CategoryType.FixedBill && c.Name == Core.Transactions.CardInterest.CategoryName)
            .Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        var yearStart = new DateOnly(today.Year, 1, 1);
        var charged = interestLine is not { } lineId ? [] : await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.CategoryId == lineId && t.DebtAccountId != null && t.Date >= today.AddYears(-1))
            .Select(t => new { Debt = t.DebtAccountId!.Value, t.Date, t.AmountCents }).ToListAsync(ct);
        var interestByDebt = charged.GroupBy(x => x.Debt).ToDictionary(g => g.Key, g => (
            Year: g.Where(x => x.Date >= yearStart).Sum(x => x.AmountCents),
            Last: g.OrderByDescending(x => x.Date).First()));

        var dtos = visible.Concat(debts.Where(d => d.IsHidden))
            .OrderBy(d => d.IsHidden).ThenByDescending(d => d.AprBps ?? -1).ThenByDescending(d => d.BalanceCents)
            .Select(d =>
            {
                var outcome = plan?.Debts.FirstOrDefault(o => o.Id == d.Id);
                var limit = d.CreditLimitCents;
                return new DebtDto(d.Id, d.Account?.Nickname ?? d.Name, d.Account?.Nickname is null ? d.Account?.Mask : null, d.AccountId is not null, d.BalanceCents, d.AprBps, d.MinimumPaymentCents, d.DueDay,
                    d.DueDay is { } day ? NextDue(today, day) : null, limit,
                    limit is > 0 ? (int)Math.Round(d.BalanceCents * 100m / limit.Value, MidpointRounding.AwayFromZero) : null,
                    d.StartingBalanceCents, d.StartedOn, d.FromBank, d.IsHidden,
                    !d.IsHidden && d.BalanceCents > 0 && (d.AprBps is null || d.MinimumPaymentCents is null),
                    outcome is null ? null : plan!.NeverPaysOff && outcome.PaidOffInMonth is null ? null : MonthOf(outcome.PaidOffInMonth),
                    outcome?.InterestCents ?? 0, d.Account?.PlaidAccountId is not null,
                    interestByDebt.TryGetValue(d.Id, out var i) ? Math.Max(0, i.Year) : 0, i.Last?.AmountCents, i.Last?.Date,
                    d.Account is { PlaidAccountId: null } ? d.TypedBalanceCents : null, d.Account is { PlaidAccountId: null } ? d.TypedOn : null);
            }).ToList();

        DebtPlanDto? planDto = plan is null ? null : new DebtPlanDto(
            strategy.ToString(), minimums, extra, source, plan.MonthlyPaymentCents, plan.Months, plan.NeverPaysOff ? null : MonthOf(plan.Months),
            plan.NeverPaysOff, plan.TotalInterestCents,
            DebtPayoff.Order(inputs, inputs.ToDictionary(i => i.Id, i => i.BalanceCents), strategy).First().Name,
            [new ChartPoint(start, inputs.Sum(i => i.BalanceCents)), .. plan.Schedule.Select(m => new ChartPoint(start.AddMonths(m.Month), m.TotalBalanceCents))]);
        DebtComparisonDto? cmp = alt is null || plan is null ? null : new DebtComparisonDto(
            other.ToString(), alt.Months, alt.NeverPaysOff ? null : MonthOf(alt.Months), alt.NeverPaysOff, alt.TotalInterestCents,
            alt.TotalInterestCents - plan.TotalInterestCents, alt.Months - plan.Months);

        // The last 12 months (DECISIONS.md 141): each card's balance worked back from its transactions (a purchase after a
        // month's end wasn't owed yet; a payment after it still was), plus loans at today's balance, and payments made.
        var cardIds = visible.Where(d => d.AccountId is not null).Select(d => d.AccountId!.Value).ToList();
        var cardOwed = visible.Where(d => d.AccountId is not null).Sum(d => d.BalanceCents);
        var loans = visible.Where(d => d.AccountId is null).Sum(d => d.BalanceCents);
        var since = start.AddMonths(-12);
        var cardTxns = await db.Transactions.AsNoTracking().Where(t => cardIds.Contains(t.AccountId) && t.Date >= since)
            .Select(t => new { t.Date, t.AmountCents, t.Kind }).ToListAsync(ct);
        var first = await db.Transactions.Where(t => cardIds.Contains(t.AccountId)).MinAsync(t => (DateOnly?)t.Date, ct);
        var history = new List<DebtHistoryPoint>();
        for (var i = 12; i >= 1; i--)
        {
            var m = start.AddMonths(-i);
            var end = m.AddMonths(1).AddDays(-1);
            if (first is null || end < first) continue;
            var owed = cardOwed - cardTxns.Where(t => t.Date > end).Sum(t => t.AmountCents) + loans;
            var paid = -cardTxns.Where(t => t.Kind == Core.Transactions.TransactionKind.CardPayment && t.AmountCents < 0 && t.Date >= m && t.Date <= end).Sum(t => t.AmountCents);
            history.Add(new DebtHistoryPoint(m, Math.Max(0, owed), paid));
        }

        return new DebtSummaryDto(
            visible.Sum(d => d.BalanceCents), visible.Sum(d => d.StartingBalanceCents),
            visible.Sum(d => Math.Max(0, d.StartingBalanceCents - d.BalanceCents)), minimums,
            dtos.Count(d => d.NeedsDetails), dtos, planDto, cmp, history);
    }

    /// <summary>The debts ready for the plan, for the budget generator and the sweep (cards only).</summary>
    public async Task<List<DebtInput>> CardInputsAsync(Guid householdId, CancellationToken ct)
    {
        var debts = await SyncCardsAsync(householdId, ct);
        return debts.Where(d => !d.IsHidden && d.AccountId is not null && d.BalanceCents > 0 && d.AprBps is not null && d.MinimumPaymentCents is not null)
            .Select(Input).ToList();
    }

    /// <summary>
    /// Fills APR, minimum and due day from the bank (Plaid Liabilities) where it shares them, without overwriting
    /// what you typed in. Many connections don't include card details; that's fine, you enter them by hand.
    /// </summary>
    public async Task<FromBankResult> FromBankAsync(Guid householdId, CancellationToken ct)
    {
        await SyncCardsAsync(householdId, ct);
        var items = await db.PlaidItems.Where(i => i.HouseholdId == householdId).ToListAsync(ct);
        if (items.Count == 0)
            return new FromBankResult(0, "No bank connections. Enter your APRs and minimums by hand.");
        var debts = await db.DebtAccounts.Include(d => d.Account)
            .Where(d => d.HouseholdId == householdId && d.Account != null && d.Account.PlaidAccountId != null).ToListAsync(ct);
        int updated = 0, unavailable = 0;
        foreach (var item in items)
        {
            LiabilitiesGetResponse r;
            try
            {
                r = await plaid.GetLiabilitiesAsync(secrets.Decrypt(item.AccessTokenEncrypted), ct);
            }
            catch (PlaidException ex)
            {
                log.LogInformation("Liabilities unavailable for item {ItemId}: {ErrorCode}", item.Id, ex.ErrorCode);
                unavailable++;
                continue;
            }
            foreach (var c in r.Liabilities.Credit ?? [])
            {
                var d = debts.FirstOrDefault(x => x.Account!.PlaidAccountId == c.AccountId && x.Account.PlaidItemId == item.Id);
                if (d is null) continue;
                var changed = false;
                var apr = c.Aprs?.FirstOrDefault(a => a.AprType == "purchase_apr") ?? c.Aprs?.FirstOrDefault();
                if (apr is not null && (d.AprBps is null || d.FromBank))
                {
                    d.AprBps = (int)Math.Round(apr.AprPercentage * 100m, MidpointRounding.AwayFromZero);
                    changed = true;
                }
                if (c.MinimumPaymentAmount is { } min && (d.MinimumPaymentCents is null || d.FromBank))
                {
                    d.MinimumPaymentCents = (long)Math.Round(min * 100m, MidpointRounding.AwayFromZero);
                    changed = true;
                }
                if (c.NextPaymentDueDate is { } due && (d.DueDay is null || d.FromBank))
                {
                    // A due date on the last day of its month means "the last day" (stored as 31), so it moves with the month.
                    d.DueDay = due.Day == DateTime.DaysInMonth(due.Year, due.Month) ? 31 : due.Day;
                    changed = true;
                }
                if (changed)
                {
                    d.FromBank = true;
                    updated++;
                }
            }
        }
        await db.SaveChangesAsync(ct);
        return new FromBankResult(updated, unavailable == items.Count
            ? "Your bank connection doesn't share card details (APR, minimum). Enter them by hand; they're on your statement."
            : updated == 0 ? "Nothing new from your bank." : null);
    }

    /// <summary>
    /// This month's plan for paying cards: the limit of the first debt-payment category, plus the Card interest bill, since
    /// the interest is charged on the cards and the minimums include it (DECISIONS.md 172).
    /// </summary>
    private async Task<long> PlannedCardPaymentsAsync(Guid householdId, CancellationToken ct)
    {
        var all = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == householdId && !c.IsHidden)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var cat = all.FirstOrDefault(c => c.Type == CategoryType.DebtPayment);
        if (cat is null) return 0;
        var interest = all.FirstOrDefault(c => Core.Transactions.CardInterest.IsInterestCategory(c.Name, c.Type));
        var current = await months.EnsureCurrentAsync(householdId, ct);
        var overrides = await BudgetMonthService.MonthOnlyLimitsAsync(db, householdId, current.StartDate, ct);
        long Limit(Category c) => overrides.TryGetValue(c.Id, out var o) ? o : c.MonthlyLimitCents;
        return Limit(cat) + (interest is null ? 0 : Limit(interest));
    }

    /// <summary>
    /// What the automatic budget says this month still lacks (DECISIONS.md 226): money that isn't there can't be an
    /// extra card payment, so it comes off the extra first.
    /// </summary>
    private async Task<long> ShortAsync(Household h, CancellationToken ct) =>
        h.AutoBudget ? Math.Max(0, (await months.EnsureCurrentAsync(h.Id, ct)).AutoShortCents) : 0;

    private static DebtInput Input(DebtAccount d) => new(d.Id, Label(d), d.BalanceCents, d.AprBps ?? 0, d.MinimumPaymentCents ?? 0);

    public static string Label(DebtAccount d) => d.Account is { } a ? AccountNames.Label(d.Name, a.Mask, a.Nickname) : d.Name;

    /// <summary>The next due date on or after today. Day 31 means "the last day of the month"; any day past the end of a short month falls on its last day.</summary>
    public static DateOnly NextDue(DateOnly today, int dueDay)
    {
        DateOnly In(DateOnly month) => new(month.Year, month.Month, Math.Min(dueDay, DateTime.DaysInMonth(month.Year, month.Month)));
        var thisMonth = In(today);
        return thisMonth >= today ? thisMonth : In(today.AddMonths(1));
    }

    private async Task<DateOnly> TodayAsync(Guid householdId, CancellationToken ct) =>
        Home.HomeSummaryService.TodayIn(await db.Households.Where(h => h.Id == householdId).Select(h => h.TimeZoneId).SingleAsync(ct), clock);
}
