using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Home;

public record BudgetMonthDto(DateOnly Start, DateOnly End, string Label);

public record HomeSummaryDto(
    BudgetMonthDto BudgetMonth,
    DateOnly Today,
    long LeftToSpendCents,
    string Status,
    long TotalVariableLimitCents,
    long TotalVariableSpentCents,
    long CarriedInDeficitCents,
    int OverBudgetCategoryCount,
    long OverBudgetCents,
    int DaysRemaining,
    long SafeToSpendPerDayCents,
    int UncategorizedCount,
    DateTime? LastUpdatedUtc,
    DateTime ComputedUtc,
    SyncStatusDto Sync,
    IReadOnlyList<PendingSweepDto> PendingSweeps,
    // Automatic category guesses this month waiting for you to confirm.
    int ReviewCount = 0,
    // The next 3 bills and subscriptions due (SPEC 4.2).
    IReadOnlyList<Bills.UpcomingBillDto>? UpcomingBills = null,
    // The partner's fixed contributions this month, for the late flag on Home (SPEC section 3).
    IReadOnlyList<Budget.ContributionStatusDto>? Contributions = null,
    long PartnerShortfallCents = 0,
    // The next paycheck (SPEC 4.2, 6.3), once a schedule or pay stub is in. Null before that.
    NextPaydayDto? NextPayday = null,
    // What else comes off Left to Spend (DECISIONS.md 177-178): spending not filed yet, and a plan beyond the month's money.
    long UnfiledSpendingCents = 0,
    long OverplannedCents = 0,
    // No income figure for this month while there is a plan: Left to Spend can't check the plan against your money (DECISIONS.md 182).
    bool IncomeUnknown = false,
    // Left to Spend checked against the bank (DECISIONS.md 183): how much it was lowered, and the check itself.
    long CashShortCents = 0,
    CashCheck? Cash = null,
    // When the accounts come up short: the flight hours that would make it up (DECISIONS.md 192).
    Payday.PickUpDto? PickUp = null,
    // Each spending category, in your budget's order, for the small tiles under Left to Spend (DECISIONS.md 254).
    IReadOnlyList<HomeCategoryDto>? Spending = null,
    // Safe to Spend: only when checking can't cover Left to Spend until the next payday (DECISIONS.md 276).
    SafeToSpendDto? SafeToSpend = null);

/// <summary>
/// Safe to Spend (DECISIONS.md 276): Left to Spend is more than checking can cover until the next payday, so Home shows
/// this instead.
/// </summary>
/// <param name="SafeCents">What checking can spare before payday: the most to spend until then (never below zero).</param>
/// <param name="Until">The first money still to land (DECISIONS.md 277): "payday Dec 4", "Bonus lands Nov 24", "Jane sends $800.00".</param>
public record SafeToSpendDto(DateOnly PayDate, int Days, long SafeCents, string Until);

/// <summary>One spending category on Home: its limit, what's spent, what's left (negative when over), and its health,
/// the same numbers and colors as its line on the Budget tab (DECISIONS.md 255).</summary>
public record HomeCategoryDto(Guid Id, string Name, long LimitCents, long SpentCents, long LeftCents, string Health);

/// <param name="HasSchedule">False until that month's schedule is imported (date only).</param>
public record NextPaydayDto(DateOnly PayDate, bool HasSchedule, long NetCents, long LowNetCents, long HighNetCents, bool LowConfidence);

/// <summary>A "send $X to …" item from a closed month that the user hasn't marked as sent yet.</summary>
public record PendingSweepDto(Guid Id, string Kind, long AmountCents, string Destination, string FromMonth);

/// <summary>Everything the money calculators need for one household's current budget month.</summary>
public sealed record CurrentMonthData(
    DateOnly Today,
    BudgetMonthRange Month,
    BudgetMonth Row,
    IReadOnlyList<MonthCategory> Categories,
    IReadOnlyList<BudgetTransaction> Transactions,
    long CarriedInDeficitCents,
    DateTime? LastUpdatedUtc,
    IReadOnlyList<ItemSyncInfo> Connections,
    IReadOnlyList<PendingSweepDto> PendingSweeps,
    int ReviewCount,
    IReadOnlyList<ContributionStatus>? Contributions = null,
    long PartnerShortfallCents = 0,
    CashInput? Cash = null)
{
    public IReadOnlyList<BudgetCategory> CalculatorCategories => Categories.Select(c => c.ForCalculator).ToList();

    public LeftToSpendResult LeftToSpend() =>
        LeftToSpendCalculator.Calculate(Month, Today, CalculatorCategories, Transactions, CarriedInDeficitCents, PartnerShortfallCents,
            MonthData.MoneyIn(Row, Contributions), Cash);

    public BudgetMonthDto MonthDto => new(Month.Start, Month.End, Month.Label.ToString("MMMM yyyy", CultureInfo.InvariantCulture));

    public static string StatusName(LeftToSpendStatus s) => s switch
    {
        LeftToSpendStatus.Healthy => "healthy",
        LeftToSpendStatus.Low => "low",
        _ => "overspent",
    };
}

/// <summary>
/// Loads one household's current budget month. Home and Budget both use this, so they always agree.
/// Data access only; the math lives in Core.
/// </summary>
public class CurrentMonthLoader(AppDbContext db, BudgetMonthService months, TimeProvider clock)
{
    public async Task<CurrentMonthData> LoadAsync(Guid householdId, CancellationToken ct = default)
    {
        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct);
        var today = HomeSummaryService.TodayIn(household.TimeZoneId, clock);

        // Creates this month's row if needed, and closes months past their grace period.
        var row = await months.EnsureCurrentAsync(householdId, ct);
        var month = await months.LoadAsync(householdId, row, ct);

        var pendingSweeps = await db.SweepTasks.AsNoTracking()
            .Where(s => s.HouseholdId == householdId && s.DoneUtc == null)
            .OrderBy(s => s.BudgetMonth.StartDate)
            .Select(s => new { s.Id, s.Kind, s.AmountCents, s.Destination, s.BudgetMonth.StartDate })
            .ToListAsync(ct);
        var calendar = new BudgetMonthCalendar(household.BudgetMonthStartDay);
        var reviewCount = await db.Transactions.CountAsync(t => t.HouseholdId == householdId && t.NeedsReview
            && t.Date >= month.Range.Start && t.Date <= month.Range.End, ct);

        var lastUpdated = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId)
            .MaxAsync(t => (DateTime?)t.CreatedUtc, ct);

        var connections = await db.PlaidItems.AsNoTracking()
            .Where(i => i.HouseholdId == householdId)
            .Select(i => new ItemSyncInfo(i.InstitutionName, i.Status, i.StatusMessage, i.LastSyncedUtc))
            .ToListAsync(ct);

        return new CurrentMonthData(today, month.Range, row, month.Categories, month.Transactions, month.CarriedInDeficitCents,
            lastUpdated, connections,
            pendingSweeps.Select(s => new PendingSweepDto(s.Id, s.Kind.ToString(), s.AmountCents, s.Destination,
                calendar.MonthContaining(s.StartDate).Label.ToString("MMMM yyyy", CultureInfo.InvariantCulture))).ToList(),
            reviewCount, month.Contributions, month.PartnerShortfallCents,
            await CashAsync(householdId, today, month, pendingSweeps.Sum(s => s.AmountCents), ct));
    }

    /// <summary>
    /// The bank side of the month for Left to Spend's cash check (DECISIONS.md 183). Null unless every checking account
    /// in the budget gets its balance from the bank: without that there's nothing true to check against.
    /// </summary>
    private async Task<CashInput?> CashAsync(Guid householdId, DateOnly today, MonthData month, long leftoverToMove, CancellationToken ct)
    {
        var range = month.Range;
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.HouseholdId == householdId && a.IncludedInBudget)
            .Select(a => new { a.Id, a.Type, a.PlaidAccountId, a.CurrentBalanceCents }).ToListAsync(ct);
        var checking = accounts.Where(a => a.Type == AccountType.Checking).ToList();
        if (checking.Count == 0 || checking.Any(a => a.PlaidAccountId is null))
            return null;
        var checkingIds = checking.Select(a => a.Id).ToList();
        var cards = accounts.Where(a => a.Type == AccountType.CreditCard).ToList();
        var cardIds = cards.Select(a => a.Id).ToList();

        // Paychecks that landed count against the month's expected income, including one that came a few days early.
        var deposits = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && checkingIds.Contains(t.AccountId) && t.Kind == TransactionKind.Income && t.AmountCents < 0
                        && !t.IsTransfer && !t.IsPartnerTransfer && t.Date >= range.Start.AddDays(-5) && t.Date <= range.End)
            .Select(t => new { t.Id, t.Date, t.AmountCents }).ToListAsync(ct);
        var paidEarly = await db.PayPeriodForecasts.AsNoTracking()
            .Where(f => f.HouseholdId == householdId && f.DepositTransactionId != null && f.PayDate >= range.Start && f.PayDate <= range.End)
            .Select(f => f.DepositTransactionId!.Value).ToListAsync(ct);
        var incomeLanded = deposits.Where(d => d.Date >= range.Start || paidEarly.Contains(d.Id)).Sum(d => -d.AmountCents);

        long partnerComing;
        if (month.Contributions is { Count: > 0 } contributions)
            partnerComing = contributions.Sum(c => Math.Max(0, c.ExpectedCents - c.ReceivedCents));
        else
        {
            var partnerLanded = await db.Transactions.AsNoTracking()
                .Where(t => t.HouseholdId == householdId && t.IsPartnerTransfer && t.AmountCents < 0 && !t.IsTransfer && t.Date >= range.Start && t.Date <= range.End)
                .SumAsync(t => -t.AmountCents, ct);
            partnerComing = Math.Max(0, month.Row.PartnerExpectedCents - partnerLanded);
        }

        // The cards' window starts at the payday before this month's first one, like the payday plan's "Pay off what you
        // charged" (DECISIONS.md 179); without pay dates, at the month's start.
        var cardsFrom = range.Start;
        SmoothBudget.Core.Pay.PayRules? rules = null;
        if (await db.ScheduleMonths.AnyAsync(m => m.HouseholdId == householdId, ct) || await db.PayStubs.AnyAsync(s => s.HouseholdId == householdId, ct))
        {
            var profile = await db.PayProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.HouseholdId == householdId, ct);
            rules = profile is null ? SmoothBudget.Core.Pay.PayRules.Default : Payday.PaydayService.RulesOf(profile);
            var before = Payday.PaydayService.PaydayBefore(SmoothBudget.Core.Pay.PayCalendar.From(range.Start, rules).First().PayDate, rules);
            if (before < cardsFrom) cardsFrom = before;
        }
        var cardCharges = cardIds.Count == 0 ? [] : await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && cardIds.Contains(t.AccountId) && !t.IsTransfer && t.Kind != TransactionKind.CardPayment
                        && t.Date >= cardsFrom && t.Date <= today)
            .Select(t => new { t.AccountId, t.Date, t.AmountCents }).ToListAsync(ct);
        var charged = cardCharges.Sum(t => t.AmountCents);
        // A skipped "Pay off what you charged" (DECISIONS.md 195): those charges stay on the card as debt, so checking
        // doesn't hold money back for them. They're still spending in their categories.
        if (rules is not null && cardCharges.Count > 0)
        {
            var skips = await db.PaydayChecklistItems.AsNoTracking()
                .Where(x => x.HouseholdId == householdId && x.Key.StartsWith(Payday.PaydayService.SkipPrefix + "cardspend:") && x.PayDate > cardsFrom && x.PayDate <= today.AddDays(3))
                .Select(x => new { x.PayDate, x.Key }).ToListAsync(ct);
            foreach (var skip in skips)
            {
                if (!Guid.TryParse(skip.Key[(Payday.PaydayService.SkipPrefix + "cardspend:").Length..], out var cardId)) continue;
                var from = Payday.PaydayService.PaydayBefore(skip.PayDate, rules);
                charged -= cardCharges.Where(t => t.AccountId == cardId && t.Date >= from && t.Date < skip.PayDate).Sum(t => t.AmountCents);
            }
        }
        var paid = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && checkingIds.Contains(t.AccountId) && t.Kind == TransactionKind.CardPayment && t.AmountCents > 0
                        && t.Date >= range.Start && t.Date <= today)
            .SumAsync(t => t.AmountCents, ct);
        // Pending activity (DECISIONS.md 255): the bank's current balance leaves out charges and deposits still pending, so
        // they're added here. Otherwise a charge waits to come off the money you have until it posts, a day or more.
        var linkedIds = accounts.Where(a => a.PlaidAccountId is not null).Select(a => a.Id).ToList();
        var pending = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Pending && linkedIds.Contains(t.AccountId))
            .GroupBy(t => t.AccountId).Select(g => new { g.Key, Cents = g.Sum(t => t.AmountCents) }).ToDictionaryAsync(x => x.Key, x => x.Cents, ct);
        long Pending(Guid id) => pending.GetValueOrDefault(id);

        // A card's balance is negative when money is owed on it; a pending charge will be owed too.
        long? owed = cards.Count > 0 && cards.All(c => c.PlaidAccountId is not null) ? cards.Sum(c => Math.Max(0, -c.CurrentBalanceCents + Pending(c.Id))) : null;

        return new CashInput(checking.Sum(a => a.CurrentBalanceCents - Pending(a.Id)), Math.Max(0, month.Row.IncomeExpectedCents - incomeLanded), partnerComing,
            cards.Count > 0, charged, paid, owed, leftoverToMove);
    }
}

public class HomeSummaryService(CurrentMonthLoader loader, TimeProvider clock, Bills.RecurringService recurring, Payday.PaydayService payday, Budget.AutoBudgetService autoBudget, Data.AppDbContext db)
{
    public async Task<HomeSummaryDto> GetAsync(Guid householdId, CancellationToken ct = default)
    {
        await autoBudget.EnsureFreshAsync(householdId, ct); // Left to Spend uses this month's limits
        var data = await loader.LoadAsync(householdId, ct);
        var (today, lastUpdated) = (data.Today, data.LastUpdatedUtc);
        var r = data.LeftToSpend();
        var checks = (await payday.GetAsync(householdId, ct)).Checks;

        return new HomeSummaryDto(
            BudgetMonth: data.MonthDto,
            Today: today,
            LeftToSpendCents: r.LeftToSpendCents,
            Status: CurrentMonthData.StatusName(r.Status),
            TotalVariableLimitCents: r.TotalVariableLimitCents,
            TotalVariableSpentCents: r.TotalVariableSpentCents,
            CarriedInDeficitCents: r.CarriedInDeficitCents,
            OverBudgetCategoryCount: r.OverBudgetCategoryCount,
            OverBudgetCents: r.OverBudgetCents,
            DaysRemaining: r.DaysRemaining,
            SafeToSpendPerDayCents: r.SafeToSpendPerDayCents,
            UncategorizedCount: r.UncategorizedSpendingCount,
            LastUpdatedUtc: lastUpdated,
            ComputedUtc: clock.GetUtcNow().UtcDateTime,
            Sync: SyncStatus.For(data.Connections, clock.GetUtcNow().UtcDateTime),
            PendingSweeps: data.PendingSweeps,
            ReviewCount: data.ReviewCount,
            UpcomingBills: await recurring.UpcomingAsync(householdId, 3, ct),
            Contributions: Budget.ContributionStatusDto.From(data.Contributions, data.Categories, await Shared.SharedService.SendersAsync(db, householdId, ct)),
            PartnerShortfallCents: data.PartnerShortfallCents,
            NextPayday: await payday.NextAsync(householdId, ct) is { } n
                ? new NextPaydayDto(n.Period.PayDate, n.HasSchedule, n.NetCents, n.LowNetCents, n.HighNetCents, n.LowConfidence)
                : null,
            UnfiledSpendingCents: r.UnfiledSpendingCents,
            OverplannedCents: r.OverplannedCents,
            IncomeUnknown: MonthData.MoneyIn(data.Row, data.Contributions) <= 0 && data.Categories.Any(c => c.LimitCents > 0),
            CashShortCents: r.CashShortCents,
            Cash: r.Cash,
            PickUp: await payday.PickUpAsync(householdId, r.CashShortCents, ct),
            Spending: SpendingRows(data),
            SafeToSpend: SafeToSpend(checks, today, r));
    }

    /// <summary>Under this, a gap between checking and Left to Spend isn't worth a warning.</summary>
    public const long SafeToSpendMinGapCents = 10_00;

    /// <summary>
    /// Safe to Spend (DECISIONS.md 276): Left to Spend is never more than checking can cover right now. It counts the whole
    /// month's money, including paychecks and bonuses that haven't landed; checking can only give what it holds (counting
    /// pending both ways) after the bills, card payments and savings due from it before the next payday. When that's less
    /// than Left to Spend by $10 or more, or below zero, Home shows it instead. It's Home's one cash warning (275).
    /// Nothing when checking isn't bank-synced.
    /// </summary>
    public static SafeToSpendDto? SafeToSpend(IReadOnlyList<Payday.CheckDto> checks, DateOnly today, LeftToSpendResult r)
    {
        var i = checks.ToList().FindIndex(c => c.IsCurrent);
        if (i < 0 || i + 1 >= checks.Count || checks[i].Plan?.SpareCents is not { } spare) return null;
        var pay = checks[i + 1].PayDate;
        var days = pay.DayNumber - today.DayNumber;
        if (days <= 0) return null;
        if (spare >= 0 && (r.LeftToSpendCents <= 0 || spare > r.LeftToSpendCents - SafeToSpendMinGapCents)) return null;
        // Until the first money still to land: a bonus or a friend's part before payday, or else payday (277).
        var until = checks[i].Plan?.Coming?.FirstOrDefault(x => x.Date < pay)?.Label ?? $"payday {pay:MMM d}";
        return new SafeToSpendDto(pay, days, Math.Max(0, spare), until);
    }

    /// <summary>The spending categories as the Budget tab shows them (its calculator), in your budget's order.</summary>
    private static List<HomeCategoryDto> SpendingRows(CurrentMonthData data)
    {
        var lines = Core.Budgeting.CategoryBreakdownCalculator.Calculate(data.Month, data.CalculatorCategories, data.Transactions).ToDictionary(l => l.CategoryId);
        return data.Categories.Where(c => c.Type == Core.Budgeting.CategoryType.Variable).OrderBy(c => c.SortOrder)
            .Where(c => lines.TryGetValue(c.Id, out var l) && (l.LimitCents > 0 || l.SpentCents > 0))
            .Select(c =>
            {
                var l = lines[c.Id];
                return new HomeCategoryDto(c.Id, c.Name, l.LimitCents, l.SpentCents, l.RemainingCents, Budget.BudgetSummaryService.HealthName(l.Health));
            }).ToList();
    }

    public static DateOnly TodayIn(string timeZoneId, TimeProvider clock)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
    }
}
