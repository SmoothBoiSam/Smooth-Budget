using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Home;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Api.Budget;

public record BudgetCategoryLineDto(
    Guid Id,
    string Name,
    string Type,
    bool IsDiscretionary,
    long LimitCents,
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
    IReadOnlyList<BudgetGroupDto> Groups);

/// <summary>The Budget screen (SPEC section 4.3): every category with its limit, spent and remaining, grouped by type.</summary>
public class BudgetSummaryService(CurrentMonthLoader loader)
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
                    return new BudgetCategoryLineDto(c.Id, c.Name, c.Type.ToString(), c.IsDiscretionary,
                        l.LimitCents, l.SpentCents, l.RemainingCents, HealthName(l.Health), l.TransactionCount);
                }).ToList();
                return new BudgetGroupDto(g.Type.ToString(), g.Label, g.Type == CategoryType.Variable,
                    items.Sum(i => i.LimitCents), items.Sum(i => i.SpentCents), items);
            })
            .Where(g => g.Categories.Count > 0)
            .ToList();

        return new BudgetSummaryDto(
            data.MonthDto, data.Today, lts.LeftToSpendCents, CurrentMonthData.StatusName(lts.Status),
            lts.TotalVariableLimitCents, lts.TotalVariableSpentCents, lts.CarriedInDeficitCents,
            lts.DaysRemaining, lts.SafeToSpendPerDayCents, lts.UncategorizedSpendingCount, groups);
    }

    private static string HealthName(CategoryHealth h) => h switch
    {
        CategoryHealth.Over => "over",
        CategoryHealth.Low => "low",
        _ => "onTrack",
    };

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/budget/summary", (HttpContext ctx, BudgetSummaryService svc, CancellationToken ct) => svc.GetAsync(ctx.User.HouseholdId(), ct))
            .RequireAuthorization();
}
