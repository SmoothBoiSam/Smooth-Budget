using System.Globalization;
using Microsoft.EntityFrameworkCore;
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
    DateTime ComputedUtc);

/// <summary>
/// Loads one household's data for the current budget month and hands it to
/// <see cref="LeftToSpendCalculator"/>. This class only does data access; the math lives in Core.
/// </summary>
public class HomeSummaryService(AppDbContext db, TimeProvider clock)
{
    public async Task<HomeSummaryDto> GetAsync(Guid householdId, CancellationToken ct = default)
    {
        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct);

        var today = TodayIn(household.TimeZoneId, clock);
        var calendar = new BudgetMonthCalendar(household.BudgetMonthStartDay);
        var month = calendar.MonthContaining(today);

        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.HouseholdId == householdId && !c.IsHidden)
            .Select(c => new BudgetCategory(c.Id, c.Type, c.MonthlyLimitCents))
            .ToListAsync(ct);

        var transactions = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Date >= month.Start && t.Date <= month.End && t.Account.IncludedInBudget)
            .Select(t => new BudgetTransaction(t.CategoryId, t.Date, t.AmountCents, t.IsTransfer, t.Pending, t.SplitFactor, t.Kind))
            .ToListAsync(ct);

        var carried = await db.BudgetMonths.AsNoTracking()
            .Where(b => b.HouseholdId == householdId && b.StartDate == month.Start)
            .Select(b => (long?)b.CarriedInDeficitCents)
            .SingleOrDefaultAsync(ct) ?? 0;

        var lastUpdated = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId)
            .MaxAsync(t => (DateTime?)t.CreatedUtc, ct);

        var r = LeftToSpendCalculator.Calculate(month, today, categories, transactions, carried);

        return new HomeSummaryDto(
            BudgetMonth: new BudgetMonthDto(month.Start, month.End, month.Label.ToString("MMMM yyyy", CultureInfo.InvariantCulture)),
            Today: today,
            LeftToSpendCents: r.LeftToSpendCents,
            Status: r.Status switch
            {
                LeftToSpendStatus.Healthy => "healthy",
                LeftToSpendStatus.Low => "low",
                _ => "overspent",
            },
            TotalVariableLimitCents: r.TotalVariableLimitCents,
            TotalVariableSpentCents: r.TotalVariableSpentCents,
            CarriedInDeficitCents: r.CarriedInDeficitCents,
            OverBudgetCategoryCount: r.OverBudgetCategoryCount,
            OverBudgetCents: r.OverBudgetCents,
            DaysRemaining: r.DaysRemaining,
            SafeToSpendPerDayCents: r.SafeToSpendPerDayCents,
            UncategorizedCount: r.UncategorizedSpendingCount,
            LastUpdatedUtc: lastUpdated,
            ComputedUtc: clock.GetUtcNow().UtcDateTime);
    }

    public static DateOnly TodayIn(string timeZoneId, TimeProvider clock)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
    }
}
