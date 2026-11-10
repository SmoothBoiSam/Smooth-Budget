using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Payday;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Tests;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Tests;

/// <summary>The automatic budget (DECISIONS.md 135-137): it follows the money coming in. All data invented.</summary>
public class AutoBudgetApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = PaydayService.Json;

    private record Setup(HttpClient Client, Guid Rent, Guid Groceries, Guid EatingOut);

    /// <summary>Three past budget months of rent $2,300, groceries $400 and eating out $300, then the automatic budget on.</summary>
    private async Task<Setup> NewHouseholdAsync()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2027, 2, 10, 19, 0, 0, TimeSpan.Zero)); // budget month Feb 8 - Mar 7
        var c = app.CreateClient();
        var me = await (await c.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"))).Content.ReadFromJsonAsync<MeResponse>(Json);
        Guid rent = Guid.NewGuid(), groceries = Guid.NewGuid(), eating = Guid.NewGuid();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var acct = new Account { Id = Guid.NewGuid(), HouseholdId = me!.HouseholdId, Name = "Test Checking", Type = AccountType.Checking, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow };
            db.Accounts.Add(acct);
            db.Categories.AddRange(
                new Category { Id = rent, HouseholdId = me.HouseholdId, Name = "Rent", Type = CategoryType.FixedBill, MonthlyLimitCents = 2_300_00 },
                new Category { Id = groceries, HouseholdId = me.HouseholdId, Name = "Groceries", Type = CategoryType.Variable, MonthlyLimitCents = 400_00, SortOrder = 1 },
                new Category { Id = eating, HouseholdId = me.HouseholdId, Name = "Eating out", Type = CategoryType.Variable, Priority = CategoryPriority.Low, MonthlyLimitCents = 300_00, SortOrder = 2 });
            Transaction T(DateOnly d, Guid? cat, long cents, TransactionKind kind = TransactionKind.Purchase) => new()
            {
                Id = Guid.NewGuid(), HouseholdId = me.HouseholdId, AccountId = acct.Id, CategoryId = cat, Date = d, AmountCents = cents, Merchant = "TEST",
                Kind = kind, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow, CategorySource = cat is null ? CategorySource.None : CategorySource.User,
            };
            foreach (var m in new[] { new DateOnly(2026, 11, 15), new DateOnly(2026, 12, 15), new DateOnly(2027, 1, 15) })
                db.Transactions.AddRange(T(m, rent, 2_300_00), T(m, groceries, 400_00), T(m.AddDays(1), eating, 300_00), T(m, null, -3_200_00, TransactionKind.Income));
            await db.SaveChangesAsync();
        }
        await c.PutAsJsonAsync("/api/settings/budget", new BudgetSettingsDto(SweepTarget.Debt, 50, AutoBudget: true), Json);
        return new Setup(c, rent, groceries, eating);
    }

    private static async Task<BudgetSummaryDto> Summary(HttpClient c) => (await c.GetFromJsonAsync<BudgetSummaryDto>("/api/budget/summary", Json))!;
    private static long Limit(BudgetSummaryDto s, Guid id) => s.Groups.SelectMany(g => g.Categories).Single(x => x.Id == id).LimitCents;

    private static async Task Upload(HttpClient c, string url, byte[] bytes, string name, string type, bool? confirm = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", name);
        if (confirm is not null) form.Add(new StringContent(confirm.Value ? "true" : "false"), "confirm");
        (await c.PostAsync(url, form)).EnsureSuccessStatusCode();
    }

    [Fact]
    public void Turning_it_off_in_tests_is_explicit() =>
        Assert.False(app.Services.GetRequiredService<IConfiguration>().GetValue("Budget:AutoForNewHouseholds", true));

    [Fact]
    public async Task Without_pay_data_the_budget_builds_itself_from_history()
    {
        var (c, rent, groceries, eating) = await NewHouseholdAsync();
        var s = await Summary(c);
        Assert.Equal((2_300_00L, 400_00L, 300_00L), (Limit(s, rent), Limit(s, groceries), Limit(s, eating)));
        Assert.Equal(3_200_00, s.IncomeExpectedCents); // the median month
        Assert.True(s.Auto!.On);
        Assert.Equal(0, s.Auto.ShortCents);
    }

    [Fact]
    public async Task Income_follows_the_payday_forecast_and_spending_is_squeezed_to_fit()
    {
        var (c, rent, groceries, eating) = await NewHouseholdAsync();
        foreach (var stub in PayFixtures.Stubs)
            (await c.PostAsJsonAsync("/api/payday/paystubs/confirm", stub, Json)).EnsureSuccessStatusCode();
        await Upload(c, "/api/payday/schedule", Encoding.UTF8.GetBytes(PayFixtures.Text), "schedule.txt", "text/plain", confirm: true);

        var plan = (await c.GetFromJsonAsync<GeneratorDto>("/api/budget/generator", Json))!;
        Assert.Contains("Feb 8", plan.IncomeExplanation); // Jan 16-31: no January schedule, so a typical check
        Assert.Contains("typical check", plan.IncomeExplanation);
        Assert.Contains("Feb 22", plan.IncomeExplanation); // Feb 1-15: forecast from the February schedule
        Assert.Contains("forecast from your schedule", plan.IncomeExplanation);

        var s = await Summary(c);
        Assert.Equal(plan.IncomeCents, s.IncomeExpectedCents);
        Assert.True(s.IncomeExpectedCents < 2_300_00 + 400_00 + 300_00, "the test needs a month that doesn't cover the usual plan");
        Assert.Equal(2_300_00, Limit(s, rent)); // bills aren't squeezed
        Assert.True(Limit(s, eating) < 300_00); // discretionary first
        Assert.Contains("Squeezed", plan.Categories.Single(x => x.CategoryId == eating).Explanation);
        Assert.Contains("squeezed out of spending", s.Auto!.Note);
        var before = s.IncomeExpectedCents;

        // DECISIONS.md 223: a squeezed category's history says where it would be without the squeeze; rent wasn't squeezed.
        async Task<CategoryHistoryDto> History(Guid id) => (await c.GetFromJsonAsync<CategoryHistoryDto>($"/api/budget/categories/{id}/history", Json))!;
        var eatingHistory = await History(eating);
        Assert.NotNull(eatingHistory.UnsqueezedCents);
        Assert.True(eatingHistory.UnsqueezedCents > Limit(s, eating));
        Assert.Contains($"(from {eatingHistory.UnsqueezedCents / 100m:C})", plan.Categories.Single(x => x.CategoryId == eating).Explanation);
        Assert.Null((await History(rent)).UnsqueezedCents);

        // Dropping a trip lowers the Feb 22 check: the budget follows on the next look, with no button.
        await c.PutAsJsonAsync("/api/payday/schedule/2027/2", new ScheduleChoicesRequest(false, [], [], ["T1001 02/04"]), Json);
        var after = await Summary(c);
        Assert.True(after.IncomeExpectedCents < before);
        Assert.True(Limit(after, groceries) <= Limit(s, groceries));
        // DECISIONS.md 226: spending gives way until the plan fits; only rent alone costing more would leave it short.
        Assert.True(2_300_00 + Limit(after, groceries) + Limit(after, eating) <= Math.Max(after.IncomeExpectedCents, 2_300_00));
        Assert.Equal(Math.Max(0, 2_300_00 - after.IncomeExpectedCents), after.Auto!.ShortCents);
    }

    [Fact]
    public async Task A_limit_you_set_stays_until_you_hand_it_back()
    {
        var (c, rent, groceries, eating) = await NewHouseholdAsync();
        await c.PutAsJsonAsync($"/api/budget/categories/{groceries}", new UpdateCategoryRequest("Groceries", CategoryType.Variable, 450_00, null, false, "permanent"), Json);
        var s = await Summary(c);
        Assert.Equal(450_00, Limit(s, groceries));
        Assert.Contains(groceries, s.Auto!.SetByYou);

        await c.PutAsJsonAsync($"/api/budget/categories/{groceries}", new UpdateCategoryRequest("Groceries", CategoryType.Variable, 450_00, null, false, "permanent", Automatic: true), Json);
        s = await Summary(c);
        Assert.Equal(400_00, Limit(s, groceries));
        Assert.DoesNotContain(groceries, s.Auto!.SetByYou);

        // Income you type stays too, until you hand it back.
        await c.PutAsJsonAsync("/api/budget/months/current/income", new IncomeRequest(2_000_00), Json);
        Assert.Equal(2_000_00, (await Summary(c)).IncomeExpectedCents);
        await c.PutAsJsonAsync("/api/budget/months/current/income", new IncomeRequest(0, Automatic: true), Json);
        Assert.Equal(3_200_00, (await Summary(c)).IncomeExpectedCents);

        // Off: nothing moves on its own.
        await c.PutAsJsonAsync("/api/settings/budget", new BudgetSettingsDto(SweepTarget.Debt, 50, AutoBudget: false), Json);
        await c.PutAsJsonAsync($"/api/budget/categories/{eating}", new UpdateCategoryRequest("Eating out", CategoryType.Variable, 300_00, CategoryPriority.Low, false, "permanent"), Json);
        Assert.False((await Summary(c)).Auto!.On);
    }
}
