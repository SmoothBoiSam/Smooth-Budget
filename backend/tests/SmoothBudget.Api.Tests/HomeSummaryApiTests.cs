using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Home;
using SmoothBudget.Api.Seeding;

namespace SmoothBudget.Api.Tests;

/// <summary>End-to-end: register/login over HTTP, seed fake data, read the Home summary.</summary>
public class HomeSummaryApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string Password = "Test-password-1";

    private async Task<(HttpClient Client, Guid HouseholdId)> RegisterAsync()
    {
        var client = app.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", Password));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var me = await res.Content.ReadFromJsonAsync<MeResponse>(Json);
        return (client, me!.HouseholdId);
    }

    private async Task SeedAsync(Guid householdId)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedHouseholdAsync(householdId);
    }

    private static async Task<HomeSummaryDto> SummaryAsync(HttpClient client)
    {
        var res = await client.GetAsync("/api/home/summary");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<HomeSummaryDto>(Json))!;
    }

    private void SetDenverNoon(int year, int month, int day) =>
        app.Clock.SetUtcNow(new DateTimeOffset(year, month, day, 19, 0, 0, TimeSpan.Zero)); // 12:00 MST

    [Fact]
    public async Task Summary_requires_login()
    {
        var res = await app.CreateClient().GetAsync("/api/home/summary");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Register_logout_and_login_round_trip()
    {
        var client = app.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.test";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "wrong-password-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password))).StatusCode);
        var me = await (await client.GetAsync("/api/auth/me")).Content.ReadFromJsonAsync<MeResponse>(Json);
        Assert.Equal(email, me!.Email);
    }

    [Fact]
    public async Task Weak_password_is_rejected()
    {
        var res = await app.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "short"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Mid_month_summary_matches_the_hand_computed_seed_numbers()
    {
        SetDenverNoon(2026, 11, 20);
        var (client, household) = await RegisterAsync();
        await SeedAsync(household);

        var s = await SummaryAsync(client);

        // Variable limits: 450 + 150 + 160 + 100 + 60 + 50 + 40 = 1,010.00
        // Spent through 11-20 (offset 14): groceries 237.92, eating out 104.50 (incl. a pending 41.20),
        // gas 83.95, shopping 24.99 (after a 12.00 refund), entertainment 14.00, household 23.47 = 488.83
        // Left to Spend = 1,010.00 - 488.83 - 42.50 carried = 478.67
        Assert.Equal(new DateOnly(2026, 11, 6), s.BudgetMonth.Start);
        Assert.Equal(new DateOnly(2026, 12, 6), s.BudgetMonth.End);
        Assert.Equal("November 2026", s.BudgetMonth.Label);
        Assert.Equal(new DateOnly(2026, 11, 20), s.Today);
        Assert.Equal(1_010_00, s.TotalVariableLimitCents);
        Assert.Equal(488_83, s.TotalVariableSpentCents);
        Assert.Equal(42_50, s.CarriedInDeficitCents);
        Assert.Equal(478_67, s.LeftToSpendCents);
        Assert.Equal(17, s.DaysRemaining);
        Assert.Equal(28_15, s.SafeToSpendPerDayCents); // 478.67 / 17, rounded down
        Assert.Equal(0, s.OverBudgetCategoryCount);
        Assert.Equal(0, s.UncategorizedCount);
        Assert.Equal("healthy", s.Status);
        Assert.NotNull(s.LastUpdatedUtc);
    }

    [Fact]
    public async Task End_of_month_summary_shows_overspending_and_turns_yellow()
    {
        SetDenverNoon(2026, 12, 5);
        var (client, household) = await RegisterAsync();
        await SeedAsync(household);

        var s = await SummaryAsync(client);

        // All variable spending: 890.97. Eating out 172.35 vs 150.00 -> over by 22.35.
        // Left to Spend = 1,010.00 - 890.97 - 42.50 = 76.53, under 15% of 1,010 -> low.
        Assert.Equal(890_97, s.TotalVariableSpentCents);
        Assert.Equal(76_53, s.LeftToSpendCents);
        Assert.Equal(1, s.OverBudgetCategoryCount);
        Assert.Equal(22_35, s.OverBudgetCents);
        Assert.Equal(2, s.DaysRemaining);
        Assert.Equal(38_26, s.SafeToSpendPerDayCents);
        Assert.Equal(1, s.UncategorizedCount); // the farmers market purchase
        Assert.Equal("low", s.Status);
    }

    [Fact]
    public async Task Today_is_decided_in_the_households_time_zone()
    {
        // 2026-12-07 05:00 UTC is still 22:00 on 12-06 in Denver: the last day of November's budget month.
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 7, 5, 0, 0, TimeSpan.Zero));
        var (client, _) = await RegisterAsync();

        var s = await SummaryAsync(client);

        Assert.Equal(new DateOnly(2026, 12, 6), s.Today);
        Assert.Equal(new DateOnly(2026, 11, 6), s.BudgetMonth.Start);
        Assert.Equal(1, s.DaysRemaining);
    }

    [Fact]
    public async Task A_household_never_sees_another_households_data()
    {
        SetDenverNoon(2026, 11, 20);
        var (_, seeded) = await RegisterAsync();
        await SeedAsync(seeded);
        var (otherClient, _) = await RegisterAsync();

        var s = await SummaryAsync(otherClient);

        Assert.Equal(0, s.TotalVariableLimitCents);
        Assert.Equal(0, s.TotalVariableSpentCents);
        Assert.Equal(0, s.LeftToSpendCents);
        Assert.Equal(0, s.CarriedInDeficitCents);
    }
}
