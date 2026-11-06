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
    bool BalanceFromBank = false);

public record ChartPoint(DateOnly Month, long BalanceCents);

/// <param name="ExtraSource">"budget" (what your budget plans for card payments, minus the minimums) or "you" (set on the Debt tab).</param>
public record DebtPlanDto(
    string Strategy, long MinimumsCents, long ExtraCents, string ExtraSource, long MonthlyPaymentCents, int Months, DateOnly? DebtFreeBy,
    bool NeverPaysOff, long TotalInterestCents, string? FirstTarget, IReadOnlyList<ChartPoint> Chart);

/// <param name="InterestDifferenceCents">The other strategy's interest minus this plan's (positive = this plan saves that much).</param>
public record DebtComparisonDto(string Strategy, int Months, DateOnly? DebtFreeBy, bool NeverPaysOff, long TotalInterestCents, long InterestDifferenceCents, int MonthsDifference);

public record DebtSummaryDto(
    long OwedCents, long StartingCents, long PaidDownCents, long MinimumsCents, int NeedsDetailsCount,
    IReadOnlyList<DebtDto> Debts, DebtPlanDto? Plan, DebtComparisonDto? Comparison);

public record DebtRequest(string Name, long? BalanceCents, int? AprBps, long? MinimumCents, int? DueDay, long? CreditLimitCents, long? StartingBalanceCents, bool IsHidden = false);
public record DebtSettingsRequest(DebtStrategy Strategy, long? ExtraCents);
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
        var cards = await db.Accounts.Where(a => a.HouseholdId == householdId && a.Type == AccountType.CreditCard && a.IncludedInBudget).ToListAsync(ct);
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
        await db.SaveChangesAsync(ct);
        return debts.Where(d => d.AccountId is null || d.Account!.IncludedInBudget).ToList();
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
            : h.ExtraDebtPaymentCents is { } saved ? (saved, "you")
            : (Math.Max(0, await PlannedCardPaymentsAsync(householdId, ct) - minimums), "budget");
        extra = Math.Max(0, extra);

        PayoffResult? plan = inputs.Count > 0 ? DebtPayoff.Simulate(inputs, extra, strategy) : null;
        var other = strategy == DebtStrategy.Avalanche ? DebtStrategy.Snowball : DebtStrategy.Avalanche;
        PayoffResult? alt = inputs.Count > 0 ? DebtPayoff.Simulate(inputs, extra, other) : null;
        var start = new DateOnly(today.Year, today.Month, 1);
        DateOnly? MonthOf(int? m) => m is { } n ? start.AddMonths(n) : null;

        var dtos = visible.Concat(debts.Where(d => d.IsHidden))
            .OrderBy(d => d.IsHidden).ThenByDescending(d => d.AprBps ?? -1).ThenByDescending(d => d.BalanceCents)
            .Select(d =>
            {
                var outcome = plan?.Debts.FirstOrDefault(o => o.Id == d.Id);
                var limit = d.CreditLimitCents;
                return new DebtDto(d.Id, d.Name, d.Account?.Mask, d.AccountId is not null, d.BalanceCents, d.AprBps, d.MinimumPaymentCents, d.DueDay,
                    d.DueDay is { } day ? NextDue(today, day) : null, limit,
                    limit is > 0 ? (int)Math.Round(d.BalanceCents * 100m / limit.Value, MidpointRounding.AwayFromZero) : null,
                    d.StartingBalanceCents, d.StartedOn, d.FromBank, d.IsHidden,
                    !d.IsHidden && d.BalanceCents > 0 && (d.AprBps is null || d.MinimumPaymentCents is null),
                    outcome is null ? null : plan!.NeverPaysOff && outcome.PaidOffInMonth is null ? null : MonthOf(outcome.PaidOffInMonth),
                    outcome?.InterestCents ?? 0, d.Account?.PlaidAccountId is not null);
            }).ToList();

        DebtPlanDto? planDto = plan is null ? null : new DebtPlanDto(
            strategy.ToString(), minimums, extra, source, plan.MonthlyPaymentCents, plan.Months, plan.NeverPaysOff ? null : MonthOf(plan.Months),
            plan.NeverPaysOff, plan.TotalInterestCents,
            DebtPayoff.Order(inputs, inputs.ToDictionary(i => i.Id, i => i.BalanceCents), strategy).First().Name,
            [new ChartPoint(start, inputs.Sum(i => i.BalanceCents)), .. plan.Schedule.Select(m => new ChartPoint(start.AddMonths(m.Month), m.TotalBalanceCents))]);
        DebtComparisonDto? cmp = alt is null || plan is null ? null : new DebtComparisonDto(
            other.ToString(), alt.Months, alt.NeverPaysOff ? null : MonthOf(alt.Months), alt.NeverPaysOff, alt.TotalInterestCents,
            alt.TotalInterestCents - plan.TotalInterestCents, alt.Months - plan.Months);

        return new DebtSummaryDto(
            visible.Sum(d => d.BalanceCents), visible.Sum(d => d.StartingBalanceCents),
            visible.Sum(d => Math.Max(0, d.StartingBalanceCents - d.BalanceCents)), minimums,
            dtos.Count(d => d.NeedsDetails), dtos, planDto, cmp);
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
                    d.DueDay = due.Day;
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

    /// <summary>This month's limit for card payments (the first debt-payment category), the budget's plan for paying cards.</summary>
    private async Task<long> PlannedCardPaymentsAsync(Guid householdId, CancellationToken ct)
    {
        var cat = await db.Categories.AsNoTracking()
            .Where(c => c.HouseholdId == householdId && c.Type == CategoryType.DebtPayment && !c.IsHidden)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).FirstOrDefaultAsync(ct);
        if (cat is null) return 0;
        var current = await months.EnsureCurrentAsync(householdId, ct);
        var over = await db.BudgetMonthCategories.AsNoTracking()
            .Where(x => x.BudgetMonthId == current.Id && x.CategoryId == cat.Id).Select(x => x.LimitOverrideCents).FirstOrDefaultAsync(ct);
        return over ?? cat.MonthlyLimitCents;
    }

    private static DebtInput Input(DebtAccount d) => new(d.Id, Label(d), d.BalanceCents, d.AprBps ?? 0, d.MinimumPaymentCents ?? 0);

    public static string Label(DebtAccount d) => d.Account?.Mask is { } mask ? $"{d.Name} ••{mask}" : d.Name;

    /// <summary>The next due date on or after today; a due day past the end of a short month falls on its last day.</summary>
    public static DateOnly NextDue(DateOnly today, int dueDay)
    {
        DateOnly In(DateOnly month) => new(month.Year, month.Month, Math.Min(dueDay, DateTime.DaysInMonth(month.Year, month.Month)));
        var thisMonth = In(today);
        return thisMonth >= today ? thisMonth : In(today.AddMonths(1));
    }

    private async Task<DateOnly> TodayAsync(Guid householdId, CancellationToken ct) =>
        Home.HomeSummaryService.TodayIn(await db.Households.Where(h => h.Id == householdId).Select(h => h.TimeZoneId).SingleAsync(ct), clock);
}
