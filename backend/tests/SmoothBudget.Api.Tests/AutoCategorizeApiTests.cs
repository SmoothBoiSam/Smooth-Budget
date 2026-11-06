using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Import;
using SmoothBudget.Api.Plaid;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Api.Tests;

/// <summary>Automatic categorization with review, suggested categories, and partner contributions. All data invented.</summary>
public class AutoCategorizeApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private async Task<HttpClient> NewUserAsync()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero)); // November budget month 11-06..12-06
        var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        return client;
    }

    private static async Task<Guid> AccountAsync(HttpClient c, string type, string mask) =>
        (await (await c.PostAsJsonAsync("/api/accounts", new { name = $"Test {type}", type, mask }, Json)).Content.ReadFromJsonAsync<AccountDto>(Json))!.Id;

    private static async Task<ImportResultDto> ImportAsync(HttpClient c, Guid account, string header, params string[] rows)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(header + "\n" + string.Join("\n", rows), Encoding.UTF8, "text/csv"), "file", "x.csv");
        form.Add(new StringContent(account.ToString()), "accountId");
        var res = await c.PostAsync("/api/import/csv", form);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<ImportResultDto>(Json))!;
    }

    private const string ChaseCard = "Transaction Date,Post Date,Description,Category,Type,Amount,Memo";
    private const string Apple = "Transaction Date,Clearing Date,Description,Merchant,Category,Type,Amount (USD),Purchased By";
    private const string ChaseChecking = "Details,Posting Date,Description,Amount,Type,Balance,Check or Slip #";

    private static async Task<List<TransactionDto>> All(HttpClient c) =>
        (await c.GetFromJsonAsync<TransactionListDto>("/api/transactions?from=2026-08-01&to=2026-12-31", Json))!.Items.ToList();

    [Fact]
    public async Task Transactions_that_arrived_before_any_categories_are_filed_when_the_budget_is_generated()
    {
        // Exactly what happened with the real data: a brand-new account syncs months of history with no
        // categories yet, then generates a budget.
        var c = await NewUserAsync();
        var card = await AccountAsync(c, "CreditCard", "4321");
        foreach (var month in new[] { 8, 9, 10 })
        {
            await ImportAsync(c, card, ChaseCard,
                $"{month:00}/10/2026,{month:00}/11/2026,FRESH MARKET,Groceries,Sale,-400.00,",
                $"{month:00}/12/2026,{month:00}/13/2026,TAQUERIA UNO,Food & Drink,Sale,-120.00,",
                $"{month:00}/14/2026,{month:00}/15/2026,FUEL STOP,Gas,Sale,-150.00,");
        }
        Assert.All(await All(c), t => Assert.Null(t.CategoryName)); // nothing to file into yet

        var g = (await c.GetFromJsonAsync<GeneratorDto>("/api/budget/generator", Json))!;

        Assert.True(g.CreatedStarterCategories);
        Assert.Equal(9, g.Categorization.Filed);
        Assert.Equal(400_00, g.Categories.Single(x => x.Name == "Groceries").ProposedCents);
        Assert.Equal(120_00, g.Categories.Single(x => x.Name == "Eating out").ProposedCents);
        Assert.Equal(150_00, g.Categories.Single(x => x.Name == "Gas").ProposedCents);
        Assert.All(await All(c), t => Assert.NotNull(t.CategoryName));
    }

    [Fact]
    public async Task Guesses_are_filed_counted_and_listed_for_review_and_confirming_teaches_the_merchant()
    {
        var c = await NewUserAsync();
        await c.GetAsync("/api/budget/generator"); // starter categories
        var apple = await AccountAsync(c, "CreditCard", "7777");
        await ImportAsync(c, apple, Apple,
            "11/10/2026,11/11/2026,STREAMCO,StreamCo,Entertainment,Purchase,15.49,Demo Person",   // "Entertainment": often a subscription -> guess
            "11/11/2026,11/12/2026,FRESH MARKET,Fresh Market,Grocery,Purchase,91.45,Demo Person"); // clear -> filed

        var review = (await c.GetFromJsonAsync<List<TransactionDto>>("/api/transactions/review", Json))!;
        var guess = Assert.Single(review);
        Assert.Equal(("STREAMCO", "Entertainment", true), (guess.Merchant, guess.CategoryName, guess.NeedsReview));
        Assert.Contains("could mean more than one thing", guess.CategoryReason);
        var summary = (await c.GetFromJsonAsync<BudgetSummaryDto>("/api/budget/summary", Json))!;
        Assert.Equal(1, summary.ReviewCount);
        Assert.Equal(15_49 + 91_45, summary.TotalVariableSpentCents); // the guess counts while it waits

        var count = await c.GetFromJsonAsync<JsonElement>("/api/transactions/inbox/count");
        Assert.Equal(1, count.GetProperty("review").GetInt32());

        // The user says it's a subscription instead; the next StreamCo charge follows that choice.
        var subs = (await c.GetFromJsonAsync<List<CategoryDto>>("/api/budget/categories", Json))!.Single(x => x.Name == "Subscriptions").Id;
        await c.PatchAsJsonAsync($"/api/transactions/{guess.Id}", new UpdateTransactionRequest(subs, false, null), Json);
        await ImportAsync(c, apple, Apple, "11/18/2026,11/19/2026,STREAMCO,StreamCo,Entertainment,Purchase,15.49,Demo Person");

        var next = (await All(c)).Single(t => t.Merchant == "STREAMCO" && t.Date == new DateOnly(2026, 11, 18));
        Assert.Equal(("Subscriptions", "Learned", false), (next.CategoryName, next.CategorySource, next.NeedsReview));
        Assert.Empty((await c.GetFromJsonAsync<List<TransactionDto>>("/api/transactions/review", Json))!);
    }

    [Fact]
    public async Task Confirming_guesses_in_bulk_makes_them_your_own()
    {
        var c = await NewUserAsync();
        await c.GetAsync("/api/budget/generator");
        var card = await AccountAsync(c, "CreditCard", "4321");
        await ImportAsync(c, card, ChaseCard,
            "11/10/2026,11/11/2026,STREAMCO,Entertainment,Sale,-15.49,",
            "11/11/2026,11/12/2026,BOWLING LANES,Entertainment,Sale,-22.50,");
        var ids = (await c.GetFromJsonAsync<List<TransactionDto>>("/api/transactions/review", Json))!.Select(t => t.Id).ToList();
        Assert.Equal(2, ids.Count);

        var res = await c.PostAsJsonAsync("/api/transactions/review/confirm", new ConfirmRequest(ids), Json);

        Assert.Equal(2, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("confirmed").GetInt32());
        Assert.Empty((await c.GetFromJsonAsync<List<TransactionDto>>("/api/transactions/review", Json))!);
        Assert.All((await All(c)).Where(t => ids.Contains(t.Id)), t => Assert.Equal(("User", "Entertainment"), (t.CategorySource, t.CategoryName)));
    }

    [Fact]
    public async Task Groups_with_no_fitting_category_suggest_one_and_adding_it_files_them()
    {
        var c = await NewUserAsync();
        await c.GetAsync("/api/budget/generator");
        var card = await AccountAsync(c, "CreditCard", "4321");
        await ImportAsync(c, card, ChaseCard,
            "11/10/2026,11/11/2026,QUICK LUBE,Automotive,Sale,-49.99,",
            "11/12/2026,11/13/2026,TIRE SHOP,Automotive,Sale,-120.00,",
            "11/13/2026,11/14/2026,CITY CLINIC,Health & Wellness,Sale,-30.00,");

        var suggestions = (await c.GetFromJsonAsync<List<SuggestedCategoryDto>>("/api/transactions/suggested-categories", Json))!;
        var carCare = Assert.Single(suggestions); // the clinic maps to Personal care (a guess), so only one group is unfiled
        Assert.Equal(("Car care", "Variable", 2), (carCare.Name, carCare.Type, carCare.TransactionCount));

        var add = await c.PostAsJsonAsync("/api/budget/categories", new CategoryRequest("Car care", CategoryType.Variable, 100_00, false), Json);
        Assert.True(add.IsSuccessStatusCode);

        var filed = (await All(c)).Where(t => t.Merchant is "QUICK LUBE" or "TIRE SHOP").ToList();
        Assert.All(filed, t => Assert.Equal(("Car care", true), (t.CategoryName, t.NeedsReview))); // filed, but confirm once
        Assert.Empty((await c.GetFromJsonAsync<List<SuggestedCategoryDto>>("/api/transactions/suggested-categories", Json))!);
    }

    [Fact]
    public async Task Partner_transfers_are_kept_apart_from_your_income()
    {
        var c = await NewUserAsync();
        await c.GetAsync("/api/budget/generator");
        var checking = await AccountAsync(c, "Checking", "1111");
        await ImportAsync(c, checking, ChaseChecking,
            "CREDIT,11/07/2026,\"PAYROLL DEMO AIR PPD ID: 1234567890\",1700.86,ACH_CREDIT,2900.86,,",
            "CREDIT,11/20/2026,\"Zelle payment from JANE PARTNER 24681357\",800.00,PARTNERFI_TO_CHASE,3737.86,,",
            "CREDIT,11/12/2026,\"Zelle payment from OLD FRIEND 1357\",25.00,PARTNERFI_TO_CHASE,3762.86,,");

        var names = (await c.GetFromJsonAsync<List<PartnerNameSuggestion>>("/api/settings/partner/suggestions", Json))!;
        Assert.Contains(names, n => n.Name == "JANE PARTNER" && n.TotalCents == 800_00);

        var res = await c.PutAsJsonAsync("/api/settings/partner", new PartnerSettingsDto("Jane Partner"), Json);
        Assert.Equal(1, (await res.Content.ReadFromJsonAsync<AutoCategorizeResult>(Json))!.PartnerContributions);

        var txns = await All(c);
        Assert.Equal("PartnerContribution", txns.Single(t => t.Description.Contains("JANE PARTNER")).Kind);
        Assert.Equal("Income", txns.Single(t => t.Description.Contains("OLD FRIEND")).Kind);
        Assert.Equal("Income", txns.Single(t => t.Description.StartsWith("PAYROLL")).Kind);

        await c.PutAsJsonAsync("/api/budget/months/current/income", new IncomeRequest(2_532_86, 800_00), Json);
        var b = (await c.GetFromJsonAsync<BudgetSummaryDto>("/api/budget/summary", Json))!;
        Assert.Equal((2_532_86L, 800_00L), (b.IncomeExpectedCents, b.PartnerExpectedCents));
        Assert.Equal((1_700_86L + 25_00L, 800_00L), (b.IncomeReceivedCents, b.PartnerReceivedCents));
        Assert.Equal("Jane Partner", b.PartnerName);
        Assert.Equal(2_532_86 + 800_00 - b.PlannedCents, b.UnallocatedCents);

        // Clearing the name turns her transfers back into income.
        await c.PutAsJsonAsync("/api/settings/partner", new PartnerSettingsDto(null), Json);
        Assert.Equal("Income", (await All(c)).Single(t => t.Description.Contains("JANE PARTNER")).Kind);
    }

    [Fact]
    public async Task Plaid_confidence_drives_review_and_a_resync_backfills_details_without_duplicates()
    {
        var c = await NewUserAsync();
        await c.GetAsync("/api/budget/generator");
        var publicToken = app.Plaid.NewLogin(new FakePlaid.Account("acc-card", "Fake Card", "4321", "credit", "credit card"));
        var access = app.Plaid.AccessTokenFor(publicToken);
        var link = (await (await c.PostAsJsonAsync("/api/plaid/exchange", new ExchangeRequest(publicToken), Json)).Content.ReadFromJsonAsync<LinkResultDto>(Json))!;

        JsonObject Txn(string id, string name, string detailed, string? confidence)
        {
            var t = FakePlaid.Txn(id, "acc-card", 30m, "2026-11-12", name, primary: "FOOD_AND_DRINK", detailed: detailed);
            t["personal_finance_category"]!["confidence_level"] = confidence;
            return t;
        }
        app.Plaid.QueueSync(access, added: [Txn("a", "CORNER BISTRO", "FOOD_AND_DRINK_RESTAURANT", "MEDIUM"), Txn("b", "KING SOOPERS", "FOOD_AND_DRINK_GROCERIES", "VERY_HIGH")]);
        await c.PostAsJsonAsync($"/api/plaid/items/{link.ItemId}/accounts",
            new ConfirmAccountsRequest([new(link.Accounts.Single().PlaidAccountId, "new", null)]), Json);

        var txns = await All(c);
        Assert.Equal(("Eating out", true), (txns.Single(t => t.Merchant == "CORNER BISTRO").CategoryName, txns.Single(t => t.Merchant == "CORNER BISTRO").NeedsReview));
        Assert.Equal(("Groceries", false), (txns.Single(t => t.Merchant == "KING SOOPERS").CategoryName, txns.Single(t => t.Merchant == "KING SOOPERS").NeedsReview));

        // Re-download: Plaid sends everything again (now with HIGH confidence for the bistro). Same rows, updated.
        app.Plaid.QueueSync(access, added: [Txn("a", "CORNER BISTRO", "FOOD_AND_DRINK_RESTAURANT", "HIGH"), Txn("b", "KING SOOPERS", "FOOD_AND_DRINK_GROCERIES", "VERY_HIGH")]);
        var res = await c.PostAsync($"/api/plaid/items/{link.ItemId}/resync", null);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());

        var after = await All(c);
        Assert.Equal(2, after.Count);
        Assert.False(after.Single(t => t.Merchant == "CORNER BISTRO").NeedsReview);
        Assert.Equal("FOOD_AND_DRINK_RESTAURANT", after.Single(t => t.Merchant == "CORNER BISTRO").BankCategory);
    }
}
