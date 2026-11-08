using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Home;
using SmoothBudget.Api.Import;
using SmoothBudget.Api.Seeding;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Api.Tests;

/// <summary>Milestone 4: months close, surplus becomes a sweep, a deficit carries over. All data invented.</summary>
public class BudgetMonthApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Password = "Test-password-1";
    private readonly Dictionary<HttpClient, string> _emails = new();

    private void At(int y, int m, int d) => app.Clock.SetUtcNow(new DateTimeOffset(y, m, d, 19, 0, 0, TimeSpan.Zero)); // noon in Denver

    /// <summary>Moves the clock and signs in again: a jump of a month or more expires the 30-day login cookie.</summary>
    private async Task At(HttpClient client, int y, int m, int d)
    {
        At(y, m, d);
        var res = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(_emails[client], Password));
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    private async Task<(HttpClient Client, Guid Household, Guid Card)> NewHouseholdAsync()
    {
        At(2026, 10, 20); // October budget month: 10-07..11-05
        var client = app.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.test";
        _emails[client] = email;
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password));
        var household = (await reg.Content.ReadFromJsonAsync<MeResponse>(Json))!.HouseholdId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Categories.AddRange(DemoSeeder.DefaultCategories(household)); // spending limits total 1,010.00
            await db.SaveChangesAsync();
        }
        var card = (await (await client.PostAsJsonAsync("/api/accounts", new { name = "Test Card", type = "CreditCard", mask = "4321" }, Json))
            .Content.ReadFromJsonAsync<AccountDto>(Json))!.Id;
        return (client, household, card);
    }

    private static async Task ImportAsync(HttpClient client, Guid card, params string[] rows)
    {
        var csv = "Transaction Date,Post Date,Description,Category,Type,Amount,Memo\n" + string.Join("\n", rows);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(csv, Encoding.UTF8, "text/csv"), "file", "card.csv");
        form.Add(new StringContent(card.ToString()), "accountId");
        var res = await client.PostAsync("/api/import/csv", form);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    private static async Task<BudgetSummaryDto> Budget(HttpClient c) => (await c.GetFromJsonAsync<BudgetSummaryDto>("/api/budget/summary", Json))!;
    private static async Task<HomeSummaryDto> Home(HttpClient c) => (await c.GetFromJsonAsync<HomeSummaryDto>("/api/home/summary", Json))!;
    private static BudgetCategoryLineDto Cat(BudgetSummaryDto b, string name) => b.Groups.SelectMany(g => g.Categories).Single(c => c.Name == name);

    // ---- The milestone 4 "done when" -------------------------------------------------------------

    [Fact]
    public async Task A_month_closes_its_surplus_becomes_a_sweep_and_a_later_deficit_carries_over()
    {
        var (client, _, card) = await NewHouseholdAsync();
        await ImportAsync(client, card,
            "10/10/2026,10/11/2026,FRESH MARKET,Groceries,Sale,-400.00,",
            "10/12/2026,10/13/2026,TAQUERIA UNO,Food & Drink,Sale,-100.00,");
        var october = await Budget(client);
        Assert.Equal(1_010_00 - 500_00, october.LeftToSpendCents);
        Assert.Null(october.LastMonth); // first month on record

        // November 8: October has ended but is still in its grace period. Its result is provisional.
        await At(client, 2026, 11, 8);
        var b = await Budget(client);
        Assert.False(b.LastMonth!.Closed);
        Assert.Equal(new DateOnly(2026, 11, 11), b.LastMonth.ClosesOn);
        Assert.Equal(510_00, b.LastMonth.SurplusCents);
        Assert.Equal(("Debt", 510_00L, "Test Card ••4321"), (b.LastMonth.Sweeps.Single().Kind, b.LastMonth.Sweeps.Single().AmountCents, b.LastMonth.Sweeps.Single().Destination));

        // A late October purchase arrives during the grace period and is reflected.
        await ImportAsync(client, card, "10/30/2026,11/02/2026,PIZZA PLACE,Food & Drink,Sale,-60.00,");
        Assert.Equal(450_00, (await Budget(client)).LastMonth!.SurplusCents);

        // Changing a limit "from now on" in November must not rewrite October, which already ended.
        var groceriesId = Cat(b, "Groceries").Id;
        await client.PutAsJsonAsync($"/api/budget/categories/{groceriesId}",
            new CategoryUpdate("Groceries", CategoryType.Variable, 300_00, false, false, "permanent"), Json);
        await client.PostAsJsonAsync("/api/budget/categories", new CategoryRequest("Pets", CategoryType.Variable, 75_00, false), Json);
        Assert.Equal(450_00, (await Budget(client)).LastMonth!.SurplusCents);
        await client.PutAsJsonAsync($"/api/budget/categories/{groceriesId}",
            new CategoryUpdate("Groceries", CategoryType.Variable, 450_00, false, false, "permanent"), Json);
        var pets = Cat(await Budget(client), "Pets").Id;
        await client.PutAsJsonAsync($"/api/budget/categories/{pets}", new CategoryUpdate("Pets", CategoryType.Variable, 75_00, false, true, "permanent"), Json);

        // Close now: the surplus becomes a sweep item to act on; nothing carries into November.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/budget/months/2026-10-07/close", null)).StatusCode);
        b = await Budget(client);
        Assert.True(b.LastMonth!.Closed);
        Assert.Equal(0, b.CarriedInDeficitCents);
        Assert.Equal(1_010_00, b.LeftToSpendCents); // a surplus is never spendable next month
        var sweep = Assert.Single(b.PendingSweeps);
        Assert.Equal((450_00L, "Test Card ••4321", "October 2026"), (sweep.AmountCents, sweep.Destination, sweep.FromMonth));
        Assert.Single((await Home(client)).PendingSweeps);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/budget/sweeps/{sweep.Id}/done", null)).StatusCode);
        Assert.Empty((await Home(client)).PendingSweeps);
        Assert.True((await Budget(client)).LastMonth!.Sweeps.Single().Done);

        // November overspends by 90.00 (600 + 500 against 1,010).
        await ImportAsync(client, card,
            "11/10/2026,11/11/2026,FRESH MARKET,Groceries,Sale,-600.00,",
            "11/12/2026,11/13/2026,STEAKHOUSE,Food & Drink,Sale,-500.00,");

        // December 15: November (11-06..12-06) is past its grace period and closes on its own.
        await At(client, 2026, 12, 15);
        var dec = await Budget(client);
        Assert.True(dec.LastMonth!.Closed);
        Assert.Equal((90_00L, 0L), (dec.LastMonth.DeficitCents, dec.LastMonth.SurplusCents));
        Assert.Empty(dec.LastMonth.Sweeps);
        var home = await Home(client);
        Assert.Equal(90_00, home.CarriedInDeficitCents); // "Carried over: -$90.00"
        Assert.Equal(1_010_00 - 90_00, home.LeftToSpendCents);
    }

    [Fact]
    public async Task A_carried_deficit_is_paid_back_before_anything_is_swept()
    {
        var (client, _, card) = await NewHouseholdAsync();
        await ImportAsync(client, card, "10/10/2026,10/11/2026,FRESH MARKET,Groceries,Sale,-1060.00,"); // 50 over
        await Budget(client); // October is on record once the app is used that month
        await At(client, 2026, 11, 20); // October closes: 50 carried into November
        Assert.Equal(50_00, (await Home(client)).CarriedInDeficitCents);

        await ImportAsync(client, card, "11/10/2026,11/11/2026,FRESH MARKET,Groceries,Sale,-980.00,"); // 30 under
        await At(client, 2026, 12, 20);
        var b = await Budget(client);
        Assert.Equal((0L, 20_00L), (b.LastMonth!.SurplusCents, b.LastMonth.DeficitCents)); // 30 under can't cover 50: 20 still carries
        Assert.Equal(20_00, b.CarriedInDeficitCents);
    }

    [Fact]
    public async Task A_month_cannot_be_closed_before_it_ends()
    {
        var (client, _, _) = await NewHouseholdAsync();
        await Budget(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/budget/months/2026-10-07/close", null)).StatusCode);
    }

    [Fact]
    public async Task Split_preference_makes_two_sweep_items()
    {
        var (client, _, card) = await NewHouseholdAsync();
        var savings = (await (await client.PostAsJsonAsync("/api/accounts", new { name = "Test Savings", type = "Savings", mask = "9999" }, Json))
            .Content.ReadFromJsonAsync<AccountDto>(Json))!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/settings/budget", new BudgetSettingsDto(SweepTarget.Split, 25), Json)).StatusCode);
        await ImportAsync(client, card, "10/10/2026,10/11/2026,FRESH MARKET,Groceries,Sale,-10.00,"); // 1,000.00 under
        await Budget(client);

        await At(client, 2026, 11, 20);
        var sweeps = (await Budget(client)).LastMonth!.Sweeps;
        Assert.Equal(750_00, sweeps.Single(s => s.Kind == "Debt").AmountCents);
        Assert.Equal((250_00L, "Test Savings ••9999"), (sweeps.Single(s => s.Kind == "Savings").AmountCents, sweeps.Single(s => s.Kind == "Savings").Destination));
    }

    // ---- Categories and income -------------------------------------------------------------------

    private record CategoryUpdate(string Name, CategoryType Type, long LimitCents, bool IsDiscretionary, bool IsHidden, string ApplyTo);

    [Fact]
    public async Task A_this_month_only_limit_applies_now_and_reverts_next_month()
    {
        var (client, _, _) = await NewHouseholdAsync();
        var groceries = Cat(await Budget(client), "Groceries");

        var res = await client.PutAsJsonAsync($"/api/budget/categories/{groceries.Id}", new CategoryUpdate("Groceries", CategoryType.Variable, 600_00, false, false, "month"), Json);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var b = await Budget(client);
        Assert.Equal((600_00L, true), (Cat(b, "Groceries").LimitCents, Cat(b, "Groceries").HasMonthOverride));
        Assert.Equal(1_160_00, b.TotalVariableLimitCents);

        await At(client, 2026, 11, 20);
        Assert.Equal((450_00L, false), (Cat(await Budget(client), "Groceries").LimitCents, Cat(await Budget(client), "Groceries").HasMonthOverride));
    }

    [Fact]
    public async Task A_permanent_limit_change_applies_now_and_later()
    {
        var (client, _, _) = await NewHouseholdAsync();
        var gas = Cat(await Budget(client), "Gas");
        await client.PutAsJsonAsync($"/api/budget/categories/{gas.Id}", new CategoryUpdate("Fuel", CategoryType.Variable, 200_00, false, false, "permanent"), Json);

        Assert.Equal(200_00, Cat(await Budget(client), "Fuel").LimitCents);
        await At(client, 2026, 11, 20);
        Assert.Equal(200_00, Cat(await Budget(client), "Fuel").LimitCents);
    }

    [Fact]
    public async Task Categories_can_be_added_hidden_and_reordered()
    {
        var (client, _, _) = await NewHouseholdAsync();
        var add = await client.PostAsJsonAsync("/api/budget/categories", new CategoryRequest("Pets", CategoryType.Variable, 75_00, false), Json);
        Assert.True(add.IsSuccessStatusCode);
        var b = await Budget(client);
        Assert.Equal(1_085_00, b.TotalVariableLimitCents);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/budget/categories", new CategoryRequest("Pets", CategoryType.Variable, 1, false), Json)).StatusCode);

        var pets = Cat(b, "Pets");
        await client.PutAsJsonAsync($"/api/budget/categories/{pets.Id}", new CategoryUpdate("Pets", CategoryType.Variable, 75_00, false, true, "permanent"), Json);
        b = await Budget(client);
        Assert.DoesNotContain(b.Groups.SelectMany(g => g.Categories), c => c.Name == "Pets");
        Assert.Equal(1_010_00, b.TotalVariableLimitCents);

        var spending = b.Groups.Single(g => g.Type == "Variable").Categories.Select(c => c.Id).Reverse().ToList();
        await client.PostAsJsonAsync("/api/budget/categories/order", new ReorderRequest(spending), Json);
        Assert.Equal(spending, (await Budget(client)).Groups.Single(g => g.Type == "Variable").Categories.Select(c => c.Id));
    }

    [Fact]
    public async Task Hiding_a_category_never_hides_this_months_spending_in_it()
    {
        var (client, household, card) = await NewHouseholdAsync();
        await client.PostAsJsonAsync("/api/budget/categories", new CategoryRequest("Pets", CategoryType.Variable, 75_00, false), Json);
        var pets = Cat(await Budget(client), "Pets");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(), HouseholdId = household, AccountId = card, Date = new DateOnly(2026, 10, 15), AmountCents = 30_00,
                Merchant = "TEST ODD SHOP", Description = "TEST ODD SHOP", Kind = SmoothBudget.Core.Transactions.TransactionKind.Purchase,
                CategoryId = pets.Id, CategorySource = SmoothBudget.Core.Transactions.CategorySource.User,
                Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(1_085_00 - 30_00, (await Home(client)).LeftToSpendCents);

        // Hidden: Pets' limit leaves the budget, and the $30 waits in Review instead of disappearing (DECISIONS.md 177).
        await client.PutAsJsonAsync($"/api/budget/categories/{pets.Id}", new CategoryUpdate("Pets", CategoryType.Variable, 75_00, false, true, "permanent"), Json);
        var home = await Home(client);
        Assert.Equal((30_00L, 1, 1_010_00L - 30_00), (home.UnfiledSpendingCents, home.UncategorizedCount, home.LeftToSpendCents));
        Assert.Equal(home.LeftToSpendCents, (await Budget(client)).LeftToSpendCents);
    }

    [Fact]
    public async Task Income_versus_planned_shows_an_explicit_remainder()
    {
        var (client, _, _) = await NewHouseholdAsync();
        await client.PutAsJsonAsync("/api/budget/months/current/income", new IncomeRequest(3_332_86), Json);
        var b = await Budget(client);
        var planned = 1_650_00 + 65_00 + 120_00 + 140_00 + 35_00 + 140_86 + 100_00 + 1_010_00;
        Assert.Equal((3_332_86L, (long)planned, 3_332_86L - planned), (b.IncomeExpectedCents, b.PlannedCents, b.UnallocatedCents));

        await At(client, 2026, 11, 20); // next month starts with the same expected income
        Assert.Equal(3_332_86, (await Budget(client)).IncomeExpectedCents);
    }

    // ---- Budget generator --------------------------------------------------------------------------

    [Fact]
    public async Task The_generator_proposes_from_history_and_applying_sets_limits_and_income()
    {
        At(2026, 11, 20);
        var client = app.CreateClient();
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        var household = (await reg.Content.ReadFromJsonAsync<MeResponse>(Json))!.HouseholdId;
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedHouseholdAsync(household); // three past months of fake history

        var g = (await client.GetFromJsonAsync<GeneratorDto>("/api/budget/generator", Json))!;

        Assert.Equal(3, g.MonthsUsed);
        Assert.Equal(1_700_86 + 760_00, g.IncomeCents); // every past month: two paychecks
        Assert.Equal((800_00L, "Partner"), (g.PartnerCents, g.PartnerName)); // her Zelle, kept separate
        Assert.True(g.HasCardDebt);
        Assert.Equal(1_650_00, g.Categories.Single(c => c.Name == "Rent").ProposedCents);
        var eating = g.Categories.Single(c => c.Name == "Eating out");
        Assert.Contains("trimmed 10%", eating.Explanation);
        Assert.Equal(3, eating.MonthlyCents.Count);
        Assert.Equal(g.IncomeCents + g.PartnerCents - g.AllocatedCents, g.UnallocatedCents); // money in = your income + partner
        Assert.Equal(3, g.UncategorizedCount); // the uncategorized farmers market purchase, once in each of the 3 months

        var res = await client.PostAsJsonAsync("/api/budget/generator/apply",
            new ApplyBudgetRequest(g.IncomeCents, g.Categories.Select(c => new GeneratorLimit(c.CategoryId, c.ProposedCents)).ToList(), g.PartnerCents), Json);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var b = await Budget(client);
        Assert.Equal((g.IncomeCents, g.PartnerCents), (b.IncomeExpectedCents, b.PartnerExpectedCents));
        Assert.Equal(eating.ProposedCents, Cat(b, "Eating out").LimitCents);
        Assert.Equal(g.AllocatedCents, b.PlannedCents);
    }

    [Fact]
    public async Task A_new_household_has_no_premade_categories()
    {
        At(2026, 11, 20);
        var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));

        var g = (await client.GetFromJsonAsync<GeneratorDto>("/api/budget/generator", Json))!;

        Assert.True(g.NeedsCategorySetup); // categories come from the user's own transactions
        Assert.Empty(g.Categories);
        Assert.Empty((await client.GetFromJsonAsync<List<CategoryDto>>("/api/budget/categories", Json))!);
    }

    [Fact]
    public async Task The_seeded_demo_closes_last_month_over_budget_and_carries_it()
    {
        At(2026, 11, 20);
        var client = app.CreateClient();
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        var household = (await reg.Content.ReadFromJsonAsync<MeResponse>(Json))!.HouseholdId;
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedHouseholdAsync(household);

        var b = await Budget(client);

        Assert.True(b.LastMonth!.Closed); // October ended on 11-05, more than five days ago
        Assert.Equal(DemoSeeder.DemoCarriedInDeficitCents, b.LastMonth.DeficitCents);
        Assert.Equal(DemoSeeder.DemoCarriedInDeficitCents, b.CarriedInDeficitCents);
        var pending = Assert.Single(b.PendingSweeps); // September's surplus, not sent yet (August's was)
        Assert.Equal(("September 2026", "Demo Visa ••0002"), (pending.FromMonth, pending.Destination)); // the card with the most owed
    }
}
