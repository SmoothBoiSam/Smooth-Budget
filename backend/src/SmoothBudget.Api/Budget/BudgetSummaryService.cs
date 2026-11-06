using System.Globalization;
using SmoothBudget.Api.Home;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Api.Budget;

public record BudgetCategoryLineDto(
    Guid Id,
    string Name,
    string Type,
    bool IsDiscretionary,
    long LimitCents,
    // True when this month's limit differs from the usual one ("this month only").
    bool HasMonthOverride,
    // Spent for spending categories; paid for bills, debt and savings.
    long SpentCents,
    // Limit minus spent. Negative = over.
    long RemainingCents,
    // "onTrack", "low" or "over".
    string Health,
    int TransactionCount);

public record BudgetGroupDto(
    string Type,
    string Label,
    // True only for spending categories: the ones that make up Left to Spend.
    bool CountsTowardLeftToSpend,
    long LimitCents,
    long SpentCents,
    IReadOnlyList<BudgetCategoryLineDto> Categories);

public record SweepItemDto(Guid Id, string Kind, long AmountCents, string Destination, bool Done);

/// <param name="Closed">False while the month is still in its grace period: the result may still change as late transactions arrive.</param>
/// <param name="ClosesOn">When it closes automatically, if not already closed.</param>
/// <param name="Sweeps">Once closed: the sweep items. While provisional: what they would be.</param>
public record LastMonthDto(
    BudgetMonthDto Month, bool Closed, DateOnly ClosesOn,
    long TotalUnspentCents, long TotalOverspentCents, long CarriedInDeficitCents,
    long ClosingNetCents, long SurplusCents, long DeficitCents,
    IReadOnlyList<SweepItemDto> Sweeps);

public record BudgetSummaryDto(
    BudgetMonthDto BudgetMonth,
    DateOnly Today,
    long LeftToSpendCents,
    string Status,
    long TotalVariableLimitCents,
    long TotalVariableSpentCents,
    long CarriedInDeficitCents,
    int DaysRemaining,
    long SafeToSpendPerDayCents,
    int UncategorizedCount,
    // SPEC section 3: income = all category limits + an explicit unallocated remainder.
    long IncomeExpectedCents,
    long PlannedCents,
    long UnallocatedCents,
    IReadOnlyList<BudgetGroupDto> Groups,
    LastMonthDto? LastMonth,
    IReadOnlyList<PendingSweepDto> PendingSweeps);

/// <summary>The Budget screen (SPEC section 4.3): every category with its limit, spent and remaining, grouped by type.</summary>
public class BudgetSummaryService(CurrentMonthLoader loader, BudgetMonthService months, Data.AppDbContext db)
{
    private static readonly (CategoryType Type, string Label)[] GroupOrder =
    [
        (CategoryType.Variable, "Spending"),
        (CategoryType.FixedBill, "Bills & subscriptions"),
        (CategoryType.DebtPayment, "Debt payments"),
        (CategoryType.SavingsTransfer, "Savings"),
    ];

    public async Task<BudgetSummaryDto> GetAsync(Guid householdId, CancellationToken ct = default)
    {
        var data = await loader.LoadAsync(householdId, ct);
        var lts = data.LeftToSpend();
        var lines = CategoryBreakdownCalculator.Calculate(data.Month, data.CalculatorCategories, data.Transactions)
            .ToDictionary(l => l.CategoryId);

        var groups = GroupOrder
            .Select(g =>
            {
                var items = data.Categories.Where(c => c.Type == g.Type).Select(c =>
                {
                    var l = lines[c.Id];
                    return new BudgetCategoryLineDto(c.Id, c.Name, c.Type.ToString(), c.IsDiscretionary, l.LimitCents, c.HasMonthOverride,
                        l.SpentCents, l.RemainingCents, HealthName(l.Health), l.TransactionCount);
                }).ToList();
                return new BudgetGroupDto(g.Type.ToString(), g.Label, g.Type == CategoryType.Variable,
                    items.Sum(i => i.LimitCents), items.Sum(i => i.SpentCents), items);
            })
            .Where(g => g.Categories.Count > 0)
            .ToList();

        var planned = data.Categories.Sum(c => c.LimitCents);
        var last = await LastMonthAsync(householdId, data.Row, ct);

        return new BudgetSummaryDto(
            data.MonthDto, data.Today, lts.LeftToSpendCents, CurrentMonthData.StatusName(lts.Status),
            lts.TotalVariableLimitCents, lts.TotalVariableSpentCents, lts.CarriedInDeficitCents,
            lts.DaysRemaining, lts.SafeToSpendPerDayCents, lts.UncategorizedSpendingCount,
            data.Row.IncomeExpectedCents, planned, data.Row.IncomeExpectedCents - planned,
            groups, last, data.PendingSweeps);
    }

    private async Task<LastMonthDto?> LastMonthAsync(Guid householdId, Data.BudgetMonth current, CancellationToken ct)
    {
        var ended = await months.LastEndedAsync(householdId, current, ct);
        if (ended is null)
            return null;
        var r = ended.Result;

        IReadOnlyList<SweepItemDto> sweeps;
        if (ended.Closed)
        {
            sweeps = db.SweepTasks.Where(s => s.BudgetMonthId == ended.Row.Id).OrderBy(s => s.Kind).AsEnumerable()
                .Select(s => new SweepItemDto(s.Id, s.Kind.ToString(), s.AmountCents, s.Destination, s.DoneUtc is not null)).ToList();
        }
        else
        {
            var household = db.Households.Single(h => h.Id == householdId);
            var (debt, savings) = await months.SweepDestinationsAsync(householdId, ct);
            sweeps = SweepPlanner.Plan(r.SurplusCents, new SweepPreference(household.SweepTarget, household.SweepSavingsPercent), debt, savings)
                .Select(i => new SweepItemDto(Guid.Empty, i.Kind.ToString(), i.AmountCents, i.Destination, false)).ToList();
        }

        var label = ended.Range.Label.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        return new LastMonthDto(new BudgetMonthDto(ended.Range.Start, ended.Range.End, label), ended.Closed, ended.ClosesOn,
            r.TotalUnspentCents, r.TotalOverspentCents, r.CarriedInDeficitCents, r.ClosingNetCents, r.SurplusCents, r.DeficitCents, sweeps);
    }

    public static string HealthName(CategoryHealth h) => h switch
    {
        CategoryHealth.Over => "over",
        CategoryHealth.Low => "low",
        _ => "onTrack",
    };
}
