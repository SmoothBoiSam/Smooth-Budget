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
/// <summary>Category as loaded for the current month: what the calculators see, plus display fields.</summary>
public sealed record LoadedCategory(Guid Id, string Name, CategoryType Type, long MonthlyLimitCents, bool IsDiscretionary, int SortOrder)
{
    public BudgetCategory ForCalculator => new(Id, Type, MonthlyLimitCents);
}

/// <summary>Everything the money calculators need for one household's current budget month.</summary>
public sealed record CurrentMonthData(
    DateOnly Today,
    BudgetMonthRange Month,
    IReadOnlyList<LoadedCategory> Categories,
    IReadOnlyList<BudgetTransaction> Transactions,
    long CarriedInDeficitCents,
    DateTime? LastUpdatedUtc)
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
public class CurrentMonthLoader(AppDbContext db, TimeProvider clock)
{
    public async Task<CurrentMonthData> LoadAsync(Guid householdId, CancellationToken ct = default)
    {
        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct);

        var today = HomeSummaryService.TodayIn(household.TimeZoneId, clock);
        var month = new BudgetMonthCalendar(household.BudgetMonthStartDay).MonthContaining(today);

        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.HouseholdId == householdId && !c.IsHidden)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new LoadedCategory(c.Id, c.Name, c.Type, c.MonthlyLimitCents, c.IsDiscretionary, c.SortOrder))
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

        return new CurrentMonthData(today, month, categories, transactions, carried, lastUpdated);
    }
}

public class HomeSummaryService(CurrentMonthLoader loader, TimeProvider clock)
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
            ComputedUtc: clock.GetUtcNow().UtcDateTime);
    }

    public static DateOnly TodayIn(string timeZoneId, TimeProvider clock)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
    }
}
