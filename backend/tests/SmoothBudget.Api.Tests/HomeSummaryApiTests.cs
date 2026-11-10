using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
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
        Assert.False(s.IncomeUnknown);
    }

    [Fact]
    public async Task Left_to_spend_is_checked_against_the_bank_once_checking_comes_from_the_bank()
    {
        // DECISIONS.md 183. The seed's checking is a CSV account: no check until its balance comes from the bank.
        SetDenverNoon(2026, 11, 20);
        var (client, household) = await RegisterAsync();
        await SeedAsync(household);
        Assert.Null((await SummaryAsync(client)).Cash);

        async Task SetChecking(long cents)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmoothBudget.Api.Data.AppDbContext>();
            await db.Accounts.Where(a => a.HouseholdId == household && a.Type == SmoothBudget.Api.Data.AccountType.Checking)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.PlaidAccountId, "test-checking").SetProperty(a => a.CurrentBalanceCents, cents));
        }

        // Plenty in checking: the budget's own figure stands.
        await SetChecking(100_000_00);
        var rich = await SummaryAsync(client);
        Assert.NotNull(rich.Cash);
        Assert.Equal((0L, 478_67L), (rich.CashShortCents, rich.LeftToSpendCents));

        // $100 in checking: Left to Spend is only what the accounts cover, and every part adds up to it.
        await SetChecking(100_00);
        var poor = await SummaryAsync(client);
        var c = poor.Cash!;
        Assert.Equal(c.AvailableCents, poor.LeftToSpendCents);
        Assert.Equal(478_67 - c.AvailableCents, poor.CashShortCents);
        Assert.Equal(c.CheckingCents + c.IncomeStillComingCents + c.PartnerStillComingCents
                     - c.BillsStillDueCents - c.CardsToPayCents - c.SavingsStillToMoveCents - c.LeftoverToMoveCents, c.AvailableCents);
        Assert.Equal(100_00, c.CheckingCents);

        // DECISIONS.md 195: a card purchase before the last payday is held back from checking to pay the card off, unless
        // you skipped paying it off on the payday plan: then it stays on the card as debt and checking doesn't hold it.
        var cardId = Guid.NewGuid();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmoothBudget.Api.Data.AppDbContext>();
            db.Accounts.Add(new SmoothBudget.Api.Data.Account
            {
                Id = cardId, HouseholdId = household, Name = "Test Card", Mask = "4444", Type = SmoothBudget.Api.Data.AccountType.CreditCard,
                Source = SmoothBudget.Api.Data.DataSource.Csv, CreatedUtc = DateTime.UtcNow,
            });
            db.Transactions.Add(new SmoothBudget.Api.Data.Transaction
            {
                Id = Guid.NewGuid(), HouseholdId = household, AccountId = cardId, Date = new DateOnly(2026, 11, 1), AmountCents = 50_00, Merchant = "TEST SHOP",
                Description = "TEST", Kind = SmoothBudget.Core.Transactions.TransactionKind.Purchase, Source = SmoothBudget.Api.Data.DataSource.Csv, CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var held = (await SummaryAsync(client)).Cash!.CardsToPayCents;
        var plan = await client.GetFromJsonAsync<SmoothBudget.Api.Payday.PaydayDto>("/api/payday", Json);
        var current = plan!.Checks.Single(x => x.IsCurrent);
        var spend = current.Plan!.AllParts().Single(i => i.Kind == "cardspend" && i.Key == $"cardspend:{cardId}");
        Assert.Equal(50_00, spend.Cents);
        (await client.PutAsJsonAsync($"/api/payday/plan/{current.PayDate:yyyy-MM-dd}/{Uri.EscapeDataString(spend.Key)}", new { done = false, skipped = true }, Json)).EnsureSuccessStatusCode();
        var afterSkip = await SummaryAsync(client);
        Assert.Equal(held - 50_00, afterSkip.Cash!.CardsToPayCents);
        poor = afterSkip;

        // DECISIONS.md 192: nothing short, no flight hours. With a latest stub that took home 75% of gross, the hours to pick up
        // are the shortfall over the rate's take-home, rounded up.
        Assert.Null(rich.PickUp);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmoothBudget.Api.Data.AppDbContext>();
            var data = new SmoothBudget.Core.Pay.PayStubData(new(2026, 11, 20), new(2026, 11, 1), new(2026, 11, 15), 40_00,
                2_000_00, 1_900_00, 400_00, 100_00, 1_500_00, [], [], [], []);
            db.PayStubs.Add(new SmoothBudget.Api.Data.PayStub
            {
                Id = Guid.NewGuid(), HouseholdId = household, PayDate = data.PayDate, PeriodStart = data.PeriodStart, PeriodEnd = data.PeriodEnd,
                GrossCents = data.GrossCents, NetCents = data.NetCents, Json = JsonSerializer.Serialize(data, SmoothBudget.Api.Payday.PaydayService.Json),
            });
            await db.SaveChangesAsync();
        }
        var withPay = await SummaryAsync(client);
        var pick = withPay.PickUp!;
        Assert.True(pick.RateCents > 0);
        Assert.Equal((long)Math.Round(pick.RateCents * 0.75m, MidpointRounding.AwayFromZero), pick.TakeHomePerHourCents);
        Assert.Equal((int)Math.Ceiling((decimal)withPay.CashShortCents / pick.TakeHomePerHourCents), pick.Hours);
        Assert.True(pick.Hours * pick.TakeHomePerHourCents >= withPay.CashShortCents);
    }

    [Fact]
    public async Task Home_says_when_no_income_is_set_to_check_the_plan_against()
    {
        // DECISIONS.md 182: with limits but no income figure, Left to Spend can't tell whether the plan fits.
        SetDenverNoon(2026, 11, 20);
        var (client, _) = await RegisterAsync();
        Assert.False((await SummaryAsync(client)).IncomeUnknown);
        await client.PostAsJsonAsync("/api/budget/categories", new CategoryRequest("Groceries", SmoothBudget.Core.Budgeting.CategoryType.Variable, 400_00), Json);
        Assert.True((await SummaryAsync(client)).IncomeUnknown);
    }

    [Fact]
    public async Task End_of_month_summary_shows_overspending_and_turns_yellow()
    {
        SetDenverNoon(2026, 12, 5);
        var (client, household) = await RegisterAsync();
        await SeedAsync(household);

        var s = await SummaryAsync(client);

        // All variable spending: 890.97. Eating out 172.35 vs 150.00 -> over by 22.35.
        // Left to Spend = 1,010.00 - 890.97 - 42.50 carried - 15.00 not filed yet (DECISIONS.md 177) = 61.53, under 15% of 1,010 -> low.
        // The plan uses exactly the month's 3,260.86, so nothing is planned beyond it.
        Assert.Equal(890_97, s.TotalVariableSpentCents);
        Assert.Equal((15_00L, 0L), (s.UnfiledSpendingCents, s.OverplannedCents));
        Assert.Equal(61_53, s.LeftToSpendCents);
        Assert.Equal(1, s.OverBudgetCategoryCount);
        Assert.Equal(22_35, s.OverBudgetCents);
        Assert.Equal(2, s.DaysRemaining);
        Assert.Equal(30_76, s.SafeToSpendPerDayCents);
        Assert.Equal(1, s.UncategorizedCount); // the farmers market purchase
        Assert.Equal("low", s.Status);
    }

    private static async Task<BudgetSummaryDto> BudgetAsync(HttpClient client)
    {
        var res = await client.GetAsync("/api/budget/summary");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<BudgetSummaryDto>(Json))!;
    }

    private static BudgetCategoryLineDto Cat(BudgetSummaryDto b, string name) =>
        b.Groups.SelectMany(g => g.Categories).Single(c => c.Name == name);

    [Fact]
    public async Task Budget_summary_shows_every_category_grouped_with_spent_and_remaining()
    {
        SetDenverNoon(2026, 11, 20);
        var (client, household) = await RegisterAsync();
        await SeedAsync(household);

        var b = await BudgetAsync(client);

        Assert.Equal(["Variable", "FixedBill", "DebtPayment", "SavingsTransfer"], b.Groups.Select(g => g.Type));
        Assert.Equal([true, false, false, false], b.Groups.Select(g => g.CountsTowardLeftToSpend));

        // Spending: the same numbers Home uses.
        var spending = b.Groups[0];
        Assert.Equal((1_010_00L, 488_83L), (spending.LimitCents, spending.SpentCents));
        Assert.Equal((237_92L, 212_08L, "onTrack"), (Cat(b, "Groceries").SpentCents, Cat(b, "Groceries").RemainingCents, Cat(b, "Groceries").Health));
        Assert.Equal((104_50L, 45_50L, 4), (Cat(b, "Eating out").SpentCents, Cat(b, "Eating out").RemainingCents, Cat(b, "Eating out").TransactionCount));
        Assert.Equal(24_99, Cat(b, "Shopping").SpentCents); // 36.99 purchase - 12.00 return
        Assert.Equal(0, Cat(b, "Personal care").SpentCents);
        Assert.Equal(478_67, b.LeftToSpendCents);
        Assert.Equal(b.LeftToSpendCents, spending.Categories.Sum(c => c.RemainingCents) - b.CarriedInDeficitCents);

        // Bills, debt and savings show what has been paid, and never touch Left to Spend.
        Assert.Equal((1_650_00L, 0L), (Cat(b, "Rent").SpentCents, Cat(b, "Rent").RemainingCents));
        Assert.Equal(70_00 + 68_40, Cat(b, "Utilities & internet").SpentCents);
        Assert.Equal(15_49, Cat(b, "Subscriptions").SpentCents);
        Assert.Equal(0, Cat(b, "Credit card payment").SpentCents); // not paid yet on 11-20
        Assert.Equal(100_00, Cat(b, "Rainy-day fund").SpentCents);  // the transfer to savings counts here
    }

    [Fact]
    public async Task Budget_summary_flags_low_and_over_categories_at_month_end()
    {
        SetDenverNoon(2026, 12, 5);
        var (client, household) = await RegisterAsync();
        await SeedAsync(household);

        var b = await BudgetAsync(client);

        Assert.Equal((-22_35L, "over"), (Cat(b, "Eating out").RemainingCents, Cat(b, "Eating out").Health));
        Assert.Equal((30_48L, "low"), (Cat(b, "Groceries").RemainingCents, Cat(b, "Groceries").Health)); // 6.8% left
        // The $300 card payment only covered this month's card purchases, which already count in their categories.
        Assert.Equal(0, Cat(b, "Credit card payment").SpentCents);
        Assert.Equal(61_53, b.LeftToSpendCents); // the same as Home, with the $15 not filed yet
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
