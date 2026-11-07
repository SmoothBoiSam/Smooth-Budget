using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Budgeting;

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
    IReadOnlyList<Bills.UpcomingBillDto>? UpcomingBills = null);

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
    int ReviewCount)
{
    public IReadOnlyList<BudgetCategory> CalculatorCategories => Categories.Select(c => c.ForCalculator).ToList();

    public LeftToSpendResult LeftToSpend() =>
        LeftToSpendCalculator.Calculate(Month, Today, CalculatorCategories, Transactions, CarriedInDeficitCents);

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
        var reviewCount = await db.Transactions.CountAsync(t => t.HouseholdId == householdId && t.NeedsReview && t.CategoryId != null
            && !t.IsTransfer && t.Date >= month.Range.Start && t.Date <= month.Range.End, ct);

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
            reviewCount);
    }
}

public class HomeSummaryService(CurrentMonthLoader loader, TimeProvider clock, Bills.RecurringService recurring)
{
    public async Task<HomeSummaryDto> GetAsync(Guid householdId, CancellationToken ct = default)
    {
        var data = await loader.LoadAsync(householdId, ct);
        var (today, lastUpdated) = (data.Today, data.LastUpdatedUtc);
        var r = data.LeftToSpend();

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
            UpcomingBills: await recurring.UpcomingAsync(householdId, 3, ct));
    }

    public static DateOnly TodayIn(string timeZoneId, TimeProvider clock)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
    }
}
