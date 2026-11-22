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
using SmoothBudget.Api.Transactions;

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

    /// <summary>A purchase this month (Feb 10) in a category, put straight in the database.</summary>
    private async Task SpendAsync(Guid category, long cents)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cat = await db.Categories.FindAsync(category);
        var acct = db.Accounts.First(a => a.HouseholdId == cat!.HouseholdId);
        db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(), HouseholdId = cat!.HouseholdId, AccountId = acct.Id, CategoryId = category, Date = new DateOnly(2027, 2, 10),
            AmountCents = cents, Merchant = "TEST", Kind = TransactionKind.Purchase, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
            CategorySource = CategorySource.User,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_overspent_category_is_raised_to_whats_spent_and_the_others_make_room()
    {
        // DECISIONS.md 268: groceries spent $700 of $400. Its limit becomes $700; the $300 more takes the $200 the plan
        // had spare, then $100 off eating out. Left to Spend is what eating out has left, with nothing over.
        var (c, rent, groceries, eating) = await NewHouseholdAsync();
        await SpendAsync(groceries, 700_00);
        var s = await Summary(c);
        Assert.Equal((2_300_00L, 700_00L, 200_00L), (Limit(s, rent), Limit(s, groceries), Limit(s, eating)));
        Assert.Equal(200_00, s.LeftToSpendCents);
        Assert.Equal(0, s.OverplannedCents);

        // DECISIONS.md 271: a limit you set that's overspent becomes this month's override of what's spent, and the others
        // still make room; your usual limit stays as you set it, for next month.
        await c.PutAsJsonAsync($"/api/budget/categories/{groceries}", new UpdateCategoryRequest("Groceries", CategoryType.Variable, 450_00, null, false, "permanent"), Json);
        s = await Summary(c);
        Assert.Contains(groceries, s.Auto!.SetByYou);
        Assert.Equal((700_00L, 200_00L), (Limit(s, groceries), Limit(s, eating)));
        Assert.True(s.Groups.SelectMany(g => g.Categories).Single(x => x.Id == groceries).HasMonthOverride);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(450_00, (await db.Categories.FindAsync(groceries))!.MonthlyLimitCents);
            Assert.Equal(700_00, db.BudgetMonthCategories.Single(x => x.CategoryId == groceries).LimitOverrideCents);
        }

        // Spending more moves the override with it.
        await SpendAsync(groceries, 50_00);
        await c.PutAsJsonAsync("/api/settings/budget", new BudgetSettingsDto(SweepTarget.Debt, 50, AutoBudget: true), Json); // any save: the budget refreshes
        s = await Summary(c);
        Assert.Equal((750_00L, 150_00L), (Limit(s, groceries), Limit(s, eating)));
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

    [Fact]
    public async Task Other_income_that_lands_adds_to_the_months_money_and_a_paycheck_does_not()
    {
        var (c, rent, _, _) = await NewHouseholdAsync();
        foreach (var stub in PayFixtures.Stubs)
            (await c.PostAsJsonAsync("/api/payday/paystubs/confirm", stub, Json)).EnsureSuccessStatusCode();
        await Upload(c, "/api/payday/schedule", Encoding.UTF8.GetBytes(PayFixtures.Text), "schedule.txt", "text/plain", confirm: true);
        var before = await Summary(c);
        var plan = (await c.GetFromJsonAsync<GeneratorDto>("/api/budget/generator", Json))!;
        var firstCheck = (long)(decimal.Parse(System.Text.RegularExpressions.Regex.Match(plan.IncomeExplanation, @"Feb 8 \$([\d,]+\.\d\d)").Groups[1].Value,
            System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture) * 100);

        async Task<Guid> DepositAsync(DateOnly date, long cents, string merchant)
        {
            var id = Guid.NewGuid();
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var household = db.Categories.Single(x => x.Id == rent).HouseholdId;
                db.Transactions.Add(new Transaction
                {
                    Id = id, HouseholdId = household, AccountId = db.Accounts.Single(a => a.HouseholdId == household).Id, Date = date, AmountCents = -cents,
                    Merchant = merchant, Description = merchant, Kind = TransactionKind.Income, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            await Refresh();
            return id;
        }
        async Task Refresh() =>
            await c.PutAsJsonAsync("/api/settings/budget", new BudgetSettingsDto(SweepTarget.Debt, 50, AutoBudget: true), Json); // any save: the budget refreshes

        // The Feb 8 paycheck lands, a few dollars off its estimate: it's that check, not extra money.
        await DepositAsync(new DateOnly(2027, 2, 8), firstCheck - 12_00, "EMPLOYER PAYROLL");
        Assert.Equal(before.IncomeExpectedCents, (await Summary(c)).IncomeExpectedCents);

        // A cash-out from a payment app the next day isn't a paycheck (DECISIONS.md 252): the month has that much more.
        await DepositAsync(new DateOnly(2027, 2, 9), 37_50, "PAYMENT APP CASHOUT");
        var after = await Summary(c);
        Assert.Equal(before.IncomeExpectedCents + 37_50, after.IncomeExpectedCents);
        Assert.Contains("$37.50 of other income", (await c.GetFromJsonAsync<GeneratorDto>("/api/budget/generator", Json))!.IncomeExplanation);

        // A bonus from work around payday, about a check's size (DECISIONS.md 254): it looks like pay, so the app leaves
        // it out, until you say in Review it's other income. Called a paycheck, it's the check again, not extra.
        var bonus = await DepositAsync(new DateOnly(2027, 2, 10), firstCheck - 50_00, "EMPLOYER PAYROLL");
        Assert.Equal(after.IncomeExpectedCents, (await Summary(c)).IncomeExpectedCents);
        (await c.PostAsJsonAsync($"/api/transactions/{bonus}/income", new MarkIncomeRequest("other"), Json)).EnsureSuccessStatusCode();
        await Refresh();
        Assert.Equal(after.IncomeExpectedCents + firstCheck - 50_00, (await Summary(c)).IncomeExpectedCents);
        (await c.PostAsJsonAsync($"/api/transactions/{bonus}/income", new MarkIncomeRequest("paycheck"), Json)).EnsureSuccessStatusCode();
        await Refresh();
        Assert.Equal(after.IncomeExpectedCents, (await Summary(c)).IncomeExpectedCents);

        // The Income page counts it where you said: a paycheck.
        var items = (await c.GetFromJsonAsync<List<IncomeItemDto>>("/api/payday/income/transactions", Json))!;
        Assert.Equal(("paycheck", true), items.Where(i => i.Id == bonus).Select(i => (i.Kind, i.Said)).Single());
        Assert.Equal("other", items.Single(i => i.Description == "PAYMENT APP CASHOUT").Kind);
    }

    [Fact]
    public async Task A_bonus_you_expect_counts_until_it_lands_then_the_deposit_does()
    {
        // DECISIONS.md 256. Made-up numbers throughout.
        var (c, rent, _, _) = await NewHouseholdAsync();
        foreach (var stub in PayFixtures.Stubs)
            (await c.PostAsJsonAsync("/api/payday/paystubs/confirm", stub, Json)).EnsureSuccessStatusCode();
        await Upload(c, "/api/payday/schedule", Encoding.UTF8.GetBytes(PayFixtures.Text), "schedule.txt", "text/plain", confirm: true);
        var before = await Summary(c);

        // Nothing to take home, or a day long gone: turned away.
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/payday/expected", new ExpectedIncomeRequest("Bonus", 0, new DateOnly(2027, 2, 20)), Json)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/payday/expected", new ExpectedIncomeRequest("Bonus", 500_00, new DateOnly(2026, 12, 1)), Json)).StatusCode);

        // Expected Feb 20, inside this budget month: the month's money counts it.
        var list = (await (await c.PostAsJsonAsync("/api/payday/expected", new ExpectedIncomeRequest(null, 1_000_00, new DateOnly(2027, 2, 20)), Json))
            .Content.ReadFromJsonAsync<List<ExpectedIncomeDto>>(Json))!;
        Assert.Equal(("Bonus", (DateOnly?)null), (list.Single().Name, list.Single().LandedDate));
        Assert.Equal(before.IncomeExpectedCents + 1_000_00, (await Summary(c)).IncomeExpectedCents);
        Assert.Contains("Bonus $1,000.00 expected Feb 20", (await c.GetFromJsonAsync<GeneratorDto>("/api/budget/generator", Json))!.IncomeExplanation);

        // It lands a day late and a little smaller: matched, other income, counted once at what came in, and waiting in
        // Review to confirm (DECISIONS.md 257).
        var deposit = Guid.NewGuid();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var household = db.Categories.Single(x => x.Id == rent).HouseholdId;
            db.Transactions.Add(new Transaction
            {
                Id = deposit, HouseholdId = household, AccountId = db.Accounts.Single(a => a.HouseholdId == household).Id, Date = new DateOnly(2027, 2, 21),
                AmountCents = -960_00, Merchant = "EMPLOYER BONUS", Description = "EMPLOYER BONUS", Kind = TransactionKind.Income, NeedsReview = true,
                Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        app.Clock.SetUtcNow(new DateTimeOffset(2027, 2, 21, 19, 0, 0, TimeSpan.Zero));
        await c.GetAsync("/api/payday"); // the payday page matches deposits
        await c.PutAsJsonAsync("/api/settings/budget", new BudgetSettingsDto(SweepTarget.Debt, 50, AutoBudget: true), Json);
        var landed = (await c.GetFromJsonAsync<List<ExpectedIncomeDto>>("/api/payday/expected", Json))!.Single();
        Assert.Equal((new DateOnly(2027, 2, 21), 960_00L), (landed.LandedDate!.Value, landed.LandedCents!.Value));
        Assert.Equal(before.IncomeExpectedCents + 960_00, (await Summary(c)).IncomeExpectedCents);
        var row = (await c.GetFromJsonAsync<List<TransactionDto>>("/api/accounts/transactions?type=checking", Json))!.Single(t => t.Id == deposit);
        Assert.Equal(("other", true), (row.IncomeType, row.NeedsReview));
        Assert.Contains("Looks like your Bonus", row.CategoryReason);
        // Confirmed in Review as that bonus: settled, still the bonus (DECISIONS.md 259).
        (await c.PostAsJsonAsync($"/api/transactions/{deposit}/income", new MarkIncomeRequest("bonus", ExpectedIncomeId: landed.Id), Json)).EnsureSuccessStatusCode();
        Assert.NotNull((await c.GetFromJsonAsync<List<ExpectedIncomeDto>>("/api/payday/expected", Json))!.Single().LandedDate);
        // Called other income instead, it lets go of the bonus; called the bonus again, it's back.
        (await c.PostAsJsonAsync($"/api/transactions/{deposit}/income", new MarkIncomeRequest("other"), Json)).EnsureSuccessStatusCode();
        Assert.Null((await c.GetFromJsonAsync<List<ExpectedIncomeDto>>("/api/payday/expected", Json))!.Single().LandedDate);
        (await c.PostAsJsonAsync($"/api/transactions/{deposit}/income", new MarkIncomeRequest("bonus", ExpectedIncomeId: landed.Id), Json)).EnsureSuccessStatusCode();

        // Removing it once it's landed leaves the deposit as the other income it is.
        (await c.DeleteAsync($"/api/payday/expected/{landed.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(before.IncomeExpectedCents + 960_00, (await Summary(c)).IncomeExpectedCents);
    }

    [Fact]
    public async Task Account_balances_take_pending_charges_off_the_banks_number()
    {
        // DECISIONS.md 256. A bank-synced account: its balance less what's pending. One you track: the balance you gave on a
        // day, moved by everything since, pending included.
        var (c, rent, _, _) = await NewHouseholdAsync();
        Guid household, checking, savings = Guid.NewGuid();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            household = db.Categories.Single(x => x.Id == rent).HouseholdId;
            var acct = db.Accounts.Single(a => a.HouseholdId == household);
            checking = acct.Id;
            (acct.PlaidAccountId, acct.CurrentBalanceCents) = ("test-chk-bal", 1_000_00);
            db.Accounts.Add(new Account { Id = savings, HouseholdId = household, Name = "Test Savings", Type = AccountType.Savings, Source = DataSource.Csv,
                CurrentBalanceCents = 500_00, BalanceAsOf = new DateOnly(2027, 2, 1), CreatedUtc = DateTime.UtcNow });
            Transaction T(Guid account, DateOnly d, long cents, bool pending) => new()
            {
                Id = Guid.NewGuid(), HouseholdId = household, AccountId = account, Date = d, AmountCents = cents, Merchant = "TEST", Description = "TEST",
                Kind = TransactionKind.Purchase, Pending = pending, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
            };
            db.Transactions.AddRange(T(checking, new DateOnly(2027, 2, 10), 30_00, pending: true), T(savings, new DateOnly(2027, 1, 30), 99_00, pending: false),
                T(savings, new DateOnly(2027, 2, 5), -100_00, pending: false), T(savings, new DateOnly(2027, 2, 10), 20_00, pending: true));
            await db.SaveChangesAsync();
        }
        var chk = (await c.GetFromJsonAsync<List<Accounts.AccountBalanceDto>>("/api/accounts/balances?type=checking", Json))!.Single();
        Assert.Equal((970_00L, 30_00L, true), (chk.BalanceCents, chk.PendingCents, chk.BankSynced));
        var sav = (await c.GetFromJsonAsync<List<Accounts.AccountBalanceDto>>("/api/accounts/balances?type=savings", Json))!.Single();
        Assert.Equal((580_00L, 20_00L, false), (sav.BalanceCents, sav.PendingCents, sav.BankSynced));
        Assert.Empty((await c.GetFromJsonAsync<List<Accounts.AccountBalanceDto>>("/api/accounts/balances?type=card", Json))!);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.GetAsync("/api/accounts/balances?type=loans")).StatusCode);

        // Debit's Cash card (DECISIONS.md 257): the checking total now, and each day's close worked back: before today's
        // pending charge, it was 30 more.
        var cash = (await c.GetFromJsonAsync<Accounts.CashDto>("/api/accounts/cash", Json))!;
        Assert.Equal(970_00, cash.TotalCents);
        Assert.Equal((new DateOnly(2027, 2, 10), 970_00L), (cash.History[^1].Date, cash.History[^1].BalanceCents));
        Assert.Equal((new DateOnly(2027, 2, 9), 1_000_00L), (cash.History[^2].Date, cash.History[^2].BalanceCents));
        Assert.Equal(Accounts.AccountBalances.CashDays + 1, cash.History.Count);

        // Transactions: one account's, or all of that kind.
        var savTx = (await c.GetFromJsonAsync<List<TransactionDto>>($"/api/accounts/transactions?type=savings&accountId={savings}", Json))!;
        Assert.Equal(3, savTx.Count);
        Assert.All(savTx, t => Assert.Equal(savings, t.AccountId));
    }
}
