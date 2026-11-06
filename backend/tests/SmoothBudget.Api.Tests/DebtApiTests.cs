using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Debt;
using SmoothBudget.Api.Import;
using SmoothBudget.Api.Plaid;
using SmoothBudget.Core.Debt;

namespace SmoothBudget.Api.Tests;

/// <summary>Milestone 5: the Debt tab, card details from the bank, and the sweep going to the highest APR. All data invented.</summary>
public class DebtApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private async Task<(HttpClient Client, Guid Household)> NewUserAsync()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero));
        var client = app.CreateClient();
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        return (client, (await reg.Content.ReadFromJsonAsync<MeResponse>(Json))!.HouseholdId);
    }

    private static async Task<DebtSummaryDto> Summary(HttpClient c, string query = "") =>
        (await c.GetFromJsonAsync<DebtSummaryDto>("/api/debt" + query, Json))!;

    private static Task<HttpResponseMessage> Put(HttpClient c, DebtDto d, long? balance, int? apr, long? min, int? due = null, long? limit = null) =>
        c.PutAsJsonAsync($"/api/debt/{d.Id}", new DebtRequest(d.Name, balance, apr, min, due, limit, null), Json);

    [Fact]
    public async Task A_card_becomes_a_debt_that_needs_its_APR_and_then_gets_a_payoff_plan()
    {
        var (c, _) = await NewUserAsync();
        await c.PostAsJsonAsync("/api/accounts", new { name = "Test Visa", type = "CreditCard", mask = "4321" }, Json);

        var s = await Summary(c);
        var card = Assert.Single(s.Debts);
        Assert.Equal((true, 0L, false), (card.IsCard, card.BalanceCents, card.BalanceFromBank)); // a CSV card: no balance from a bank yet
        Assert.Null(s.Plan); // nothing complete yet

        // A CSV-only card: you enter the balance too. $5,000 at 18% paying $150 → 47 months (the amortization example).
        Assert.Equal(HttpStatusCode.NoContent, (await Put(c, card, 5_000_00, 1800, 150_00, due: 25, limit: 10_000_00)).StatusCode);
        s = await Summary(c, "?extra=0");
        card = s.Debts.Single();
        Assert.Equal((false, 50, 5_000_00L), (card.NeedsDetails, card.UtilizationPercent!.Value, card.StartingBalanceCents));
        Assert.Equal(new DateOnly(2026, 11, 25), card.NextDueDate);
        Assert.Equal((47, 1_983_61L, "Avalanche", "you"), (s.Plan!.Months, s.Plan.TotalInterestCents, s.Plan.Strategy, s.Plan.ExtraSource));
        Assert.Equal(new DateOnly(2030, 10, 1), s.Plan.DebtFreeBy); // 47 months after November 2026
        Assert.Equal(48, s.Plan.Chart.Count); // today plus every month
        Assert.Equal(new ChartPoint(new DateOnly(2026, 11, 1), 5_000_00), s.Plan.Chart[0]);

        // The what-if slider: $100 more a month pays it off sooner.
        var faster = await Summary(c, "?extra=10000");
        Assert.True(faster.Plan!.Months < 47);
        Assert.Equal(250_00, faster.Plan.MonthlyPaymentCents);
    }

    [Fact]
    public async Task Avalanche_is_the_default_and_snowball_is_the_comparison()
    {
        var (c, _) = await NewUserAsync();
        await c.PostAsJsonAsync("/api/debt", new DebtRequest("Store card", 500_00, 1200, 25_00, null, null, null), Json);
        await c.PostAsJsonAsync("/api/debt", new DebtRequest("Big card", 1_000_00, 2400, 50_00, null, null, null), Json);

        var s = await Summary(c, "?extra=10000");
        Assert.Equal(("Avalanche", "Big card"), (s.Plan!.Strategy, s.Plan.FirstTarget));
        Assert.Equal("Snowball", s.Comparison!.Strategy);
        Assert.True(s.Comparison.InterestDifferenceCents > 0); // snowball costs more here
        Assert.Equal(1_500_00, s.OwedCents);

        await c.PutAsJsonAsync("/api/debt/settings", new DebtSettingsRequest(DebtStrategy.Snowball, 100_00), Json);
        s = await Summary(c);
        Assert.Equal(("Snowball", "Store card", "you", 100_00L), (s.Plan!.Strategy, s.Plan.FirstTarget, s.Plan.ExtraSource, s.Plan.ExtraCents));
    }

    [Fact]
    public async Task Card_details_come_from_the_bank_when_it_shares_them_but_never_overwrite_yours()
    {
        var (c, _) = await NewUserAsync();
        var publicToken = app.Plaid.NewLogin(
            new FakePlaid.Account("acc-visa", "Fake Visa", "1111", "credit", "credit card", Balance: 2_400m, Limit: 6_000m),
            new FakePlaid.Account("acc-mc", "Fake Mastercard", "2222", "credit", "credit card", Balance: 300m, Limit: 1_000m));
        var access = app.Plaid.AccessTokenFor(publicToken);
        var link = (await (await c.PostAsJsonAsync("/api/plaid/exchange", new ExchangeRequest(publicToken), Json)).Content.ReadFromJsonAsync<LinkResultDto>(Json))!;
        await c.PostAsJsonAsync($"/api/plaid/items/{link.ItemId}/accounts",
            new ConfirmAccountsRequest(link.Accounts.Select(a => new AccountMappingRequest(a.PlaidAccountId, "new", null)).ToList()), Json);

        var s = await Summary(c);
        var visa = s.Debts.Single(d => d.Name == "Fake Visa");
        Assert.Equal((2_400_00L, 40, true, true), (visa.BalanceCents, visa.UtilizationPercent!.Value, visa.BalanceFromBank, visa.NeedsDetails));

        app.Plaid.Liabilities[access] = [("acc-visa", 24.99m, 72m, "2026-12-03"), ("acc-mc", 19.99m, 35m, "2026-11-28")];
        var r = (await (await c.PostAsync("/api/debt/from-bank", null)).Content.ReadFromJsonAsync<FromBankResult>(Json))!;
        Assert.Equal((2, (string?)null), (r.Updated, r.Message));
        s = await Summary(c);
        visa = s.Debts.Single(d => d.Name == "Fake Visa");
        Assert.Equal((2499, 72_00L, 3, true, false), (visa.AprBps!.Value, visa.MinimumCents!.Value, visa.DueDay!.Value, visa.FromBank, visa.NeedsDetails));
        Assert.Equal(0, s.NeedsDetailsCount);

        // You correct the minimum; the bank's next answer doesn't overwrite it.
        await Put(c, visa, null, 2499, 80_00, 3);
        app.Plaid.Liabilities[access] = [("acc-visa", 26.99m, 75m, "2027-01-03")];
        await c.PostAsync("/api/debt/from-bank", null);
        visa = (await Summary(c)).Debts.Single(d => d.Name == "Fake Visa");
        Assert.Equal((2499, 80_00L, false), (visa.AprBps!.Value, visa.MinimumCents!.Value, visa.FromBank));
    }

    [Fact]
    public async Task A_bank_that_doesnt_share_card_details_says_so()
    {
        var (c, _) = await NewUserAsync();
        var publicToken = app.Plaid.NewLogin(new FakePlaid.Account("acc-visa", "Fake Visa", "1111", "credit", "credit card", Balance: 500m));
        var link = (await (await c.PostAsJsonAsync("/api/plaid/exchange", new ExchangeRequest(publicToken), Json)).Content.ReadFromJsonAsync<LinkResultDto>(Json))!;
        await c.PostAsJsonAsync($"/api/plaid/items/{link.ItemId}/accounts",
            new ConfirmAccountsRequest([new(link.Accounts.Single().PlaidAccountId, "new", null)]), Json);

        var r = (await (await c.PostAsync("/api/debt/from-bank", null)).Content.ReadFromJsonAsync<FromBankResult>(Json))!;
        Assert.Equal(0, r.Updated);
        Assert.Contains("doesn't share card details", r.Message);
    }

    [Fact]
    public async Task Progress_counts_what_you_paid_down_since_you_started()
    {
        var (c, _) = await NewUserAsync();
        await c.PostAsJsonAsync("/api/debt", new DebtRequest("Car loan", 8_000_00, 649, 250_00, 15, null, 10_000_00), Json);
        var s = await Summary(c);
        Assert.Equal((10_000_00L, 8_000_00L, 2_000_00L), (s.StartingCents, s.OwedCents, s.PaidDownCents));
        Assert.False(s.Debts.Single().IsCard);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/debt/{s.Debts.Single().Id}")).StatusCode);
        Assert.Empty((await Summary(c)).Debts);
    }

    [Fact]
    public async Task Bad_input_is_rejected()
    {
        var (c, _) = await NewUserAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/debt", new DebtRequest("X", 100_00, 20_000, 10_00, null, null, null), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/debt", new DebtRequest("X", null, 1000, 10_00, null, null, null), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/debt", new DebtRequest("X", 100_00, 1000, 10_00, 32, null, null), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/debt?extra=-1")).StatusCode);
    }

    [Fact]
    public async Task The_sweep_goes_to_the_highest_APR()
    {
        var (c, household) = await NewUserAsync();
        await c.PostAsJsonAsync("/api/debt", new DebtRequest("Low card", 3_000_00, 1299, 60_00, null, null, null), Json);
        await c.PostAsJsonAsync("/api/debt", new DebtRequest("High card", 500_00, 2899, 25_00, null, null, null), Json);
        using var scope = app.Services.CreateScope();
        var (debt, _) = await scope.ServiceProvider.GetRequiredService<BudgetMonthService>().SweepDestinationsAsync(household, CancellationToken.None);
        Assert.Equal("High card", debt);
    }
}
