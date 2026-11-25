using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Import;
using SmoothBudget.Api.Plaid;
using SmoothBudget.Api.Savings;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Api.Tests;

/// <summary>Milestone 8: progress and projected completion date update from account balances. All data invented.</summary>
public class SavingsApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private async Task<HttpClient> NewUserAsync()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero)); // budget month Nov 6 - Dec 6
        var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        return client;
    }

    private static async Task<SavingsSummaryDto> Summary(HttpClient c) => (await c.GetFromJsonAsync<SavingsSummaryDto>("/api/savings", Json))!;

    [Fact]
    public async Task The_rainy_day_fund_follows_the_bank_balance_and_its_date_moves_with_it()
    {
        var c = await NewUserAsync();
        var publicToken = app.Plaid.NewLogin(new FakePlaid.Account("acc-sav", "Fake Savings", "4444", "depository", "savings", Balance: 250m));
        var access = app.Plaid.AccessTokenFor(publicToken);
        var link = (await (await c.PostAsJsonAsync("/api/plaid/exchange", new ExchangeRequest(publicToken), Json)).Content.ReadFromJsonAsync<LinkResultDto>(Json))!;
        // $100 in each of the last three budget months: a pace of $100 a month.
        app.Plaid.QueueSync(access, added:
        [
            FakePlaid.Txn("d1", "acc-sav", -100m, "2026-08-10", "Online Transfer from CHK"),
            FakePlaid.Txn("d2", "acc-sav", -100m, "2026-09-10", "Online Transfer from CHK"),
            FakePlaid.Txn("d3", "acc-sav", -100m, "2026-10-10", "Online Transfer from CHK"),
        ]);
        await c.PostAsJsonAsync($"/api/plaid/items/{link.ItemId}/accounts",
            new ConfirmAccountsRequest([new(link.Accounts.Single().PlaidAccountId, "new", null)]), Json);

        var s = await Summary(c);
        var rainy = Assert.Single(s.Goals);
        Assert.Equal(("Rainy-day fund", true, 1_000_00L, "Fake Savings"), (rainy.Name, rainy.IsStarter, rainy.TargetCents, rainy.AccountName));
        Assert.Equal((250_00L, 25, 100_00L, "pace", 8), (rainy.SavedCents, rainy.Percent, rainy.MonthlyCents, rainy.MonthlySource, rainy.MonthsToGo!.Value));
        Assert.Equal(new DateOnly(2027, 7, 20), rainy.ProjectedDate); // 750 left at $100 a month: 8 months
        Assert.Equal(250_00, s.TotalSavedCents);
        Assert.Equal(13, s.History.Count); // 12 month-ends and today
        Assert.Equal(250_00, s.History[^1].BalanceCents);
        Assert.Equal([150_00L, 250_00L], s.History.Skip(10).Take(2).Select(p => p.BalanceCents)); // end of Sept, end of Oct

        // The bank reports more saved: progress and the date update on the next sync.
        app.Plaid.SetBalance(access, "acc-sav", 600m);
        await c.PostAsync($"/api/plaid/items/{link.ItemId}/resync", null);
        rainy = (await Summary(c)).Goals.Single();
        Assert.Equal((600_00L, 60, 4, new DateOnly(2027, 3, 20)), (rainy.SavedCents, rainy.Percent, rainy.MonthsToGo!.Value, rainy.ProjectedDate!.Value));

        app.Plaid.SetBalance(access, "acc-sav", 1_250m);
        await c.PostAsync($"/api/plaid/items/{link.ItemId}/resync", null);
        rainy = (await Summary(c)).Goals.Single();
        Assert.Equal((1_000_00L, true, (DateOnly?)null), (rainy.SavedCents, rainy.Reached, rainy.ProjectedDate));
    }

    [Fact]
    public async Task Goals_by_hand_a_csv_savings_balance_and_the_plans_share()
    {
        var c = await NewUserAsync();
        await c.PostAsJsonAsync("/api/budget/categories", new SmoothBudget.Api.Budget.CategoryRequest("Savings", CategoryType.SavingsTransfer, 200_00), Json);
        var acct = (await (await c.PostAsJsonAsync("/api/accounts", new { name = "Test Savings", type = "Savings", mask = "5555" }, Json))
            .Content.ReadFromJsonAsync<AccountDto>(Json))!.Id;
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api/savings/accounts/{acct}/balance", new BalanceRequest(1_300_00), Json)).StatusCode);

        var s = await Summary(c);
        var rainy = s.Goals.Single();
        Assert.Equal((acct, 1_000_00L, true), (rainy.AccountId!.Value, rainy.SavedCents, rainy.Reached)); // linked to the savings account

        // A second goal on the same account gets what's beyond the rainy-day fund; one by hand uses what you enter.
        await c.PostAsJsonAsync("/api/savings/goals", new GoalRequest("Trip", 2_000_00, acct, null), Json);
        await c.PostAsJsonAsync("/api/savings/goals", new GoalRequest("Car", 5_000_00, null, 1_200_00), Json);
        s = await Summary(c);
        // The month plans $200 for savings, all of it to the one account: the trip, its first unfinished goal, gets it.
        var trip = s.Goals.Single(g => g.Name == "Trip");
        Assert.Equal((300_00L, 200_00L, "plan", 9), (trip.SavedCents, trip.MonthlyCents, trip.MonthlySource, trip.MonthsToGo!.Value)); // 1,700 / 200 = 8.5 → 9
        var car = s.Goals.Single(g => g.Name == "Car");
        Assert.Equal((1_200_00L, "none"), (car.SavedCents, car.MonthlySource));
        Assert.Equal(1_300_00 + 1_200_00, s.TotalSavedCents);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.DeleteAsync($"/api/savings/goals/{rainy.Id}")).StatusCode); // the starter goal stays
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/savings/goals/{car.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/savings/goals", new GoalRequest("X", 0, null, null), Json)).StatusCode);
    }

    [Fact]
    public async Task Planned_this_month_follows_a_this_month_only_change_to_the_savings_line()
    {
        var c = await NewUserAsync();
        var id = (await (await c.PostAsJsonAsync("/api/budget/categories", new SmoothBudget.Api.Budget.CategoryRequest("Savings", CategoryType.SavingsTransfer, 100_00), Json))
            .Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(Json))!["id"]!.GetValue<Guid>();
        Assert.Equal(100_00, (await Summary(c)).PlannedCents);

        // "This month only": the Budget tab shows 75, so Savings does too (DECISIONS.md 251).
        await c.PutAsJsonAsync($"/api/budget/categories/{id}",
            new SmoothBudget.Api.Budget.UpdateCategoryRequest("Savings", CategoryType.SavingsTransfer, 75_00, null, false, "month"), Json);
        Assert.Equal(75_00, (await Summary(c)).PlannedCents);
    }
}
