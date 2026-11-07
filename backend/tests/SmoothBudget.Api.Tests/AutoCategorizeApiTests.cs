using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Import;
using SmoothBudget.Api.Plaid;
using SmoothBudget.Api.Seeding;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Api.Tests;

/// <summary>
/// Categories built from your own transactions; every automatic category waits for you to confirm it;
/// suggested categories; partner contributions. All data invented.
/// </summary>
public class AutoCategorizeApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private async Task<HttpClient> NewUserAsync(bool withStandardCategories = true)
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero)); // November budget month 11-06..12-06
        var client = app.CreateClient();
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        if (withStandardCategories)
        {
            var household = (await reg.Content.ReadFromJsonAsync<MeResponse>(Json))!.HouseholdId;
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Categories.AddRange(DemoSeeder.DefaultCategories(household));
            await db.SaveChangesAsync();
        }
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
        (await c.GetFromJsonAsync<TransactionListDto>("/api/transactions?from=2026-01-01&to=2026-12-31", Json))!.Items.ToList();

    private static async Task<List<TransactionDto>> Review(HttpClient c, string scope = "month") =>
        (await c.GetFromJsonAsync<List<TransactionDto>>($"/api/transactions/review?scope={scope}", Json))!;

    // ---- Categories from your own transactions -------------------------------------------------------

    [Fact]
    public async Task Categories_are_discovered_from_your_transactions_not_premade()
    {
        var c = await NewUserAsync(withStandardCategories: false);
        var card = await AccountAsync(c, "CreditCard", "4321");
        foreach (var month in new[] { 7, 8, 9, 10 })
        {
            await ImportAsync(c, card, ChaseCard,
                $"{month:00}/03/2026,{month:00}/04/2026,FRESH MARKET,Groceries,Sale,-400.00,",
                $"{month:00}/08/2026,{month:00}/09/2026,TAQUERIA UNO,Food & Drink,Sale,-120.00,",
                $"{month:00}/10/2026,{month:00}/11/2026,VOLT CHARGERS CHARGING CO,Gas,Sale,-38.00,", // EV charging, labeled "Gas" by the bank
                $"{month:00}/11/2026,{month:00}/12/2026,MAPLE COURT APARTMENTS,Bills & Utilities,Sale,-1650.00,",
                $"{month:00}/14/2026,{month:00}/15/2026,STEAM PURCHASE,Entertainment,Sale,-19.99,");
        }

        var g = (await c.GetFromJsonAsync<GeneratorDto>("/api/budget/generator", Json))!;
        Assert.True(g.NeedsCategorySetup);

        var d = (await c.GetFromJsonAsync<CategoryDiscoveryDto>("/api/budget/categories/discover", Json))!;
        var names = d.Proposed.Select(p => p.Name).ToList();
        Assert.Contains("Groceries", names);
        Assert.Contains("Eating out", names);
        Assert.Contains("Rent", names);
        Assert.Contains("EV charging", names);
        Assert.Contains("Gaming", names);
        Assert.DoesNotContain("Gas", names);   // only charging here
        Assert.DoesNotContain("Phone", names); // no phone bill
        Assert.All(d.Proposed, p => Assert.Equal("new", p.Status));
        var ev = d.Proposed.Single(p => p.Name == "EV charging");
        Assert.Equal((true, 4), (ev.Custom, ev.TransactionCount));
        Assert.Equal(["VOLT CHARGERS CHARGING CO"], ev.Examples);

        var res = await c.PostAsJsonAsync("/api/budget/categories/discover/apply",
            new ApplyDiscoveryRequest(d.Proposed.Select(p => new NewCategoryRequest(p.Name, Enum.Parse<CategoryType>(p.Type), p.IsDiscretionary)).ToList(), []), Json);
        var filed = (await res.Content.ReadFromJsonAsync<AutoCategorizeResult>(Json))!;
        Assert.Equal(20, filed.Filed + filed.Guesses);

        var txns = await All(c);
        Assert.All(txns.Where(t => t.Merchant.StartsWith("VOLT")), t => Assert.Equal("EV charging", t.CategoryName));
        Assert.All(txns.Where(t => t.Merchant.StartsWith("MAPLE")), t => Assert.Equal("Rent", t.CategoryName));
        Assert.All(txns, t => Assert.True(t.NeedsReview)); // everything waits for you to confirm

        g = (await c.GetFromJsonAsync<GeneratorDto>("/api/budget/generator", Json))!;
        Assert.False(g.NeedsCategorySetup);
        Assert.Equal(400_00, g.Categories.Single(x => x.Name == "Groceries").ProposedCents);
        Assert.Equal(38_00, g.Categories.Single(x => x.Name == "EV charging").ProposedCents);
    }

    [Fact]
    public async Task Unused_categories_are_offered_for_hiding_and_their_transactions_move()
    {
        var c = await NewUserAsync(); // has the standard set, including Gas and Phone
        var card = await AccountAsync(c, "CreditCard", "4321");
        foreach (var month in new[] { 8, 9, 10 })
            await ImportAsync(c, card, ChaseCard, $"{month:00}/10/2026,{month:00}/11/2026,VOLT CHARGERS CHARGING CO,Gas,Sale,-38.00,");
        Assert.All(await All(c), t => Assert.Equal("Gas", t.CategoryName)); // the bank said gas

        var d = (await c.GetFromJsonAsync<CategoryDiscoveryDto>("/api/budget/categories/discover", Json))!;
        Assert.Contains(d.Unused, u => u.Name == "Gas");
        Assert.Contains(d.Unused, u => u.Name == "Phone");
        Assert.Contains(d.Proposed, p => p.Name == "EV charging" && p.Status == "new");

        await c.PostAsJsonAsync("/api/budget/categories/discover/apply", new ApplyDiscoveryRequest(
            [new("EV charging", CategoryType.Variable, false)], d.Unused.Select(u => u.Id).ToList()), Json);

        Assert.All(await All(c), t => Assert.Equal("EV charging", t.CategoryName));
        var visible = (await c.GetFromJsonAsync<List<CategoryDto>>("/api/budget/categories", Json))!.Select(x => x.Name).ToList();
        Assert.DoesNotContain("Gas", visible);
        Assert.DoesNotContain("Phone", visible);
    }

    // ---- Every automatic category is reviewed --------------------------------------------------------

    [Fact]
    public async Task Every_automatic_category_waits_for_you_with_likely_and_unsure_told_apart()
    {
        var c = await NewUserAsync();
        var apple = await AccountAsync(c, "CreditCard", "7777");
        await ImportAsync(c, apple, Apple,
            "11/10/2026,11/11/2026,STREAMCO,StreamCo,Entertainment,Purchase,15.49,Demo Person",   // ambiguous label -> unsure
            "11/11/2026,11/12/2026,FRESH MARKET,Fresh Market,Grocery,Purchase,91.45,Demo Person"); // clear -> likely

        var review = await Review(c);
        Assert.Equal(2, review.Count);
        Assert.True(review.Single(t => t.Merchant == "STREAMCO").Unsure);
        Assert.False(review.Single(t => t.Merchant == "FRESH MARKET").Unsure);
        Assert.All(review, t => Assert.True(t.NeedsReview));

        var summary = (await c.GetFromJsonAsync<BudgetSummaryDto>("/api/budget/summary", Json))!;
        Assert.Equal(2, summary.ReviewCount);
        Assert.Equal(15_49 + 91_45, summary.TotalVariableSpentCents); // counted while waiting

        // You file StreamCo as a subscription; the next StreamCo charge follows that, but still waits for you.
        var subs = (await c.GetFromJsonAsync<List<CategoryDto>>("/api/budget/categories", Json))!.Single(x => x.Name == "Subscriptions").Id;
        await c.PatchAsJsonAsync($"/api/transactions/{review.Single(t => t.Merchant == "STREAMCO").Id}", new UpdateTransactionRequest(subs, false, null), Json);
        await ImportAsync(c, apple, Apple, "11/18/2026,11/19/2026,STREAMCO,StreamCo,Entertainment,Purchase,15.49,Demo Person");

        var next = (await All(c)).Single(t => t.Merchant == "STREAMCO" && t.Date == new DateOnly(2026, 11, 18));
        Assert.Equal(("Subscriptions", "Learned", true, false), (next.CategoryName, next.CategorySource, next.NeedsReview, next.Unsure));
    }

    [Fact]
    public async Task A_rule_files_automatically_but_you_still_confirm()
    {
        var c = await NewUserAsync();
        var card = await AccountAsync(c, "CreditCard", "4321");
        await ImportAsync(c, card, ChaseCard, "11/10/2026,11/11/2026,WAL-MART #1234,Shopping,Sale,-60.00,");
        var groceries = (await c.GetFromJsonAsync<List<CategoryDto>>("/api/budget/categories", Json))!.Single(x => x.Name == "Groceries").Id;
        var first = (await Review(c)).Single();
        await c.PatchAsJsonAsync($"/api/transactions/{first.Id}", new UpdateTransactionRequest(groceries, false, "WAL-MART"), Json);

        await ImportAsync(c, card, ChaseCard, "11/15/2026,11/16/2026,WAL-MART #1234,Shopping,Sale,-45.00,");

        var second = (await Review(c)).Single(); // sometimes groceries, sometimes household: you decide
        Assert.Equal(("Groceries", "Rule", true), (second.CategoryName, second.CategorySource, second.NeedsReview));
    }

    [Fact]
    public async Task Likely_ones_can_be_confirmed_in_bulk_but_never_the_unsure_ones()
    {
        var c = await NewUserAsync();
        var card = await AccountAsync(c, "CreditCard", "4321");
        await ImportAsync(c, card, ChaseCard,
            "09/10/2026,09/11/2026,FRESH MARKET,Groceries,Sale,-80.00,",   // earlier, likely
            "09/12/2026,09/13/2026,STREAMCO,Entertainment,Sale,-15.49,",    // earlier, unsure
            "11/10/2026,11/11/2026,CORNER GROCERY,Groceries,Sale,-55.00,"); // this month, likely

        var s = (await c.GetFromJsonAsync<ReviewSummaryDto>("/api/transactions/review/summary", Json))!;
        Assert.Equal((1, 1, 0), (s.Month.Total, s.Month.Likely, s.Month.Unsure));
        Assert.Equal((2, 1, 1), (s.Earlier.Total, s.Earlier.Likely, s.Earlier.Unsure));
        Assert.True((await Review(c, "earlier")).First().Unsure); // unsure first

        var res = await c.PostAsJsonAsync("/api/transactions/review/confirm-likely", new ConfirmLikelyRequest("earlier"), Json);
        Assert.Equal(1, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("confirmed").GetInt32());

        s = (await c.GetFromJsonAsync<ReviewSummaryDto>("/api/transactions/review/summary", Json))!;
        Assert.Equal((1, 0, 1), (s.Earlier.Total, s.Earlier.Likely, s.Earlier.Unsure)); // the unsure one still waits
        Assert.Equal(1, s.Month.Total); // this month untouched
        var count = await c.GetFromJsonAsync<JsonElement>("/api/transactions/inbox/count");
        Assert.Equal(1, count.GetProperty("review").GetInt32()); // the badge counts this month only

        var ids = (await Review(c)).Select(t => t.Id).ToList();
        await c.PostAsJsonAsync("/api/transactions/review/confirm", new ConfirmRequest(ids), Json);
        Assert.Empty(await Review(c));
        Assert.Equal("User", (await All(c)).Single(t => t.Merchant == "CORNER GROCERY").CategorySource);
    }

    [Fact]
    public async Task Groups_with_no_fitting_category_suggest_one_and_adding_it_files_them()
    {
        var c = await NewUserAsync();
        var card = await AccountAsync(c, "CreditCard", "4321");
        await ImportAsync(c, card, ChaseCard,
            "11/10/2026,11/11/2026,QUICK LUBE,Automotive,Sale,-49.99,",
            "11/12/2026,11/13/2026,TIRE SHOP,Automotive,Sale,-120.00,",
            "11/13/2026,11/14/2026,CITY CLINIC,Health & Wellness,Sale,-30.00,");

        var suggestions = (await c.GetFromJsonAsync<List<SuggestedCategoryDto>>("/api/transactions/suggested-categories", Json))!;
        var carCare = Assert.Single(suggestions);
        Assert.Equal(("Car care", "Variable", 2), (carCare.Name, carCare.Type, carCare.TransactionCount));

        Assert.True((await c.PostAsJsonAsync("/api/budget/categories", new CategoryRequest("Car care", CategoryType.Variable, 100_00, false), Json)).IsSuccessStatusCode);

        Assert.All((await All(c)).Where(t => t.Merchant is "QUICK LUBE" or "TIRE SHOP"), t => Assert.Equal(("Car care", true), (t.CategoryName, t.NeedsReview)));
        Assert.Empty((await c.GetFromJsonAsync<List<SuggestedCategoryDto>>("/api/transactions/suggested-categories", Json))!);
    }

    // ---- Nothing counted twice -----------------------------------------------------------------------

    [Fact]
    public async Task Transfers_and_card_payments_never_count_twice_across_checking_savings_and_card()
    {
        var c = await NewUserAsync();
        var checking = await AccountAsync(c, "Checking", "1111");
        var savings = await AccountAsync(c, "Savings", "2222");
        var card = await AccountAsync(c, "CreditCard", "4321");
        await ImportAsync(c, checking, ChaseChecking,
            "CREDIT,11/07/2026,\"PAYROLL DEMO AIR PPD ID: 1234567890\",1700.86,ACH_CREDIT,2900.86,,",
            "DEBIT,11/10/2026,\"Online Transfer to SAV ...2222 transaction#: 1\",-100.00,ACCT_XFER,2837.86,,",
            "DEBIT,11/12/2026,\"CORNER GROCERY\",-40.00,DEBIT_CARD,2797.86,,",
            "DEBIT,11/20/2026,\"Payment to Chase card ending in 4321 11/20\",-500.00,LOAN_PMT,2297.86,,",
            "CREDIT,11/22/2026,\"Online Transfer from SAV ...2222 transaction#: 2\",50.00,ACCT_XFER,2347.86,,");
        await ImportAsync(c, savings, ChaseChecking,
            "CREDIT,11/10/2026,\"Online Transfer from CHK ...1111 transaction#: 1\",100.00,ACCT_XFER,1100.00,,",
            "DEBIT,11/22/2026,\"Online Transfer to CHK ...1111 transaction#: 2\",-50.00,ACCT_XFER,1050.00,,");
        await ImportAsync(c, card, ChaseCard,
            "11/12/2026,11/13/2026,FRESH MARKET,Groceries,Sale,-200.00,",
            "11/14/2026,11/15/2026,TAQUERIA UNO,Food & Drink,Sale,-60.00,",
            "11/20/2026,11/20/2026,Payment Thank You-Mobile,,Payment,500.00,");

        var b = (await c.GetFromJsonAsync<BudgetSummaryDto>("/api/budget/summary", Json))!;
        long Spent(string name) => b.Groups.SelectMany(g => g.Categories).Single(x => x.Name == name).SpentCents;

        Assert.Equal(1_700_86, b.IncomeReceivedCents);          // savings moves are never income
        Assert.Equal(200_00 + 60_00, b.TotalVariableSpentCents); // the debit-card grocery has no label yet: waits for a category
        Assert.Equal(100_00, Spent("Rainy-day fund"));          // counted once, from the checking side
        Assert.Equal(500_00 - 260_00, Spent("Credit card payment")); // only what paid down the balance
        Assert.Equal(1, b.UncategorizedCount);
    }

    // ---- Partner contributions -----------------------------------------------------------------------

    [Fact]
    public async Task Partner_transfers_are_kept_apart_from_your_income()
    {
        var c = await NewUserAsync();
        var checking = await AccountAsync(c, "Checking", "1111");
        await ImportAsync(c, checking, ChaseChecking,
            "CREDIT,11/07/2026,\"PAYROLL DEMO AIR PPD ID: 1234567890\",1700.86,ACH_CREDIT,2900.86,,",
            "CREDIT,11/20/2026,\"Zelle payment from JANE PARTNER 24681357\",800.00,PARTNERFI_TO_CHASE,3737.86,,",
            "CREDIT,11/12/2026,\"Zelle payment from OLD FRIEND 1357\",25.00,PARTNERFI_TO_CHASE,3762.86,,");

        var names = (await c.GetFromJsonAsync<List<PartnerNameSuggestion>>("/api/settings/partner/suggestions", Json))!;
        Assert.Contains(names, n => n.Name == "JANE PARTNER" && n.TotalCents == 800_00);

        await c.PutAsJsonAsync("/api/settings/partner", new PartnerSettingsDto("Jane Partner"), Json); // today, Nov 20

        // Today's $800 from her waits for a label (no fixed contribution is set up here); you say it's her money.
        var txns = await All(c);
        var jane = txns.Single(t => t.Description.Contains("JANE PARTNER"));
        Assert.Equal(("PartnerTransfer", "Income"), (jane.Kind, txns.Single(t => t.Description.Contains("OLD FRIEND")).Kind));
        await c.PatchAsJsonAsync($"/api/transactions/{jane.Id}", new UpdateTransactionRequest(null, false, null, PartnerPurpose: "contribution"), Json);
        Assert.Equal("PartnerContribution", (await All(c)).Single(t => t.Id == jane.Id).Kind);

        await c.PutAsJsonAsync("/api/budget/months/current/income", new IncomeRequest(2_532_86, 800_00), Json);
        var b = (await c.GetFromJsonAsync<BudgetSummaryDto>("/api/budget/summary", Json))!;
        Assert.Equal((2_532_86L, 800_00L), (b.IncomeExpectedCents, b.PartnerExpectedCents));
        Assert.Equal((1_700_86L + 25_00L, 800_00L), (b.IncomeReceivedCents, b.PartnerReceivedCents));
        Assert.Equal(2_532_86 + 800_00 - b.PlannedCents, b.UnallocatedCents);

        await c.PutAsJsonAsync("/api/settings/partner", new PartnerSettingsDto(null), Json);
        Assert.Equal("Income", (await All(c)).Single(t => t.Description.Contains("JANE PARTNER")).Kind);
    }

    [Fact]
    public async Task Money_between_you_waits_for_a_label_and_then_lands_where_you_say()
    {
        // DECISIONS.md 100: no tally. You bought $120 of groceries and she sent $60; she paid for something and you
        // sent her $35; she sent $25 for no reason in particular. Nothing counts until you label it.
        var c = await NewUserAsync();
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 19, 0, 0, TimeSpan.Zero)); // you name her on Nov 7
        await c.PutAsJsonAsync("/api/settings/partner", new PartnerSettingsDto("Jane Partner"), Json);
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero));
        var cats = (await c.GetFromJsonAsync<List<CategoryDto>>("/api/budget/categories", Json))!;
        var groceries = cats.Single(x => x.Name == "Groceries").Id;
        var eatingOut = cats.Single(x => x.Name == "Eating out").Id;

        var card = await AccountAsync(c, "CreditCard", "4321");
        var checking = await AccountAsync(c, "Checking", "1111");
        await ImportAsync(c, card, ChaseCard, "11/10/2026,11/11/2026,FRESH MARKET,Groceries,Sale,-120.00,");
        await ImportAsync(c, checking, ChaseChecking,
            "CREDIT,11/13/2026,\"Zelle payment from JANE PARTNER 111\",60.00,PARTNERFI_TO_CHASE,1060.00,,",
            "DEBIT,11/16/2026,\"Zelle payment to Jane 222\",-35.00,CHASE_TO_PARTNERFI,1025.00,,",
            "CREDIT,11/18/2026,\"Zelle payment from JANE PARTNER 444\",25.00,PARTNERFI_TO_CHASE,1050.00,,");

        var txns = await All(c);
        var payback = txns.Single(t => t.Description.Contains("111"));
        var sent = txns.Single(t => t.Description.Contains("to Jane"));
        var gift = txns.Single(t => t.Description.Contains("444"));
        Assert.All([payback, sent, gift], t => Assert.Equal(("PartnerTransfer", true, true, (Guid?)null), (t.Kind, t.IsPartnerTransfer, t.NeedsReview, t.CategoryId)));
        Assert.Equal((groceries, "Looks like half of FRESH MARKET $120.00 on Nov 10."), (payback.SuggestedCategoryId!.Value, payback.CategoryReason));
        Assert.Equal("Sent to Jane: what was it for?", sent.CategoryReason);
        Assert.Contains(await Review(c), t => t.Id == sent.Id); // all three wait in Review…
        Assert.DoesNotContain((await c.GetFromJsonAsync<List<TransactionDto>>("/api/transactions/inbox", Json))!, t => t.Id == sent.Id);

        var before = (await c.GetFromJsonAsync<BudgetSummaryDto>("/api/budget/summary", Json))!;
        long Spent(BudgetSummaryDto b, string name) => b.Groups.SelectMany(g => g.Categories).Single(x => x.Name == name).SpentCents;
        Assert.Equal((120_00L, 0L, 0L), (Spent(before, "Groceries"), before.PartnerReceivedCents, before.IncomeReceivedCents)); // …counted nowhere

        // A ✓ can't settle them: they need a label.
        await c.PostAsJsonAsync("/api/transactions/review/confirm", new ConfirmRequest([payback.Id, sent.Id, gift.Id]), Json);
        Assert.Equal(3, (await Review(c)).Count(t => t.Kind == "PartnerTransfer"));

        await c.PatchAsJsonAsync($"/api/transactions/{payback.Id}", new UpdateTransactionRequest(groceries, false, null), Json); // paid me back for groceries
        await c.PatchAsJsonAsync($"/api/transactions/{sent.Id}", new UpdateTransactionRequest(eatingOut, false, null), Json);   // my share of her dinner
        await c.PatchAsJsonAsync($"/api/transactions/{gift.Id}", new UpdateTransactionRequest(null, false, null, PartnerPurpose: "contribution"), Json);

        var after = (await c.GetFromJsonAsync<BudgetSummaryDto>("/api/budget/summary", Json))!;
        Assert.Equal((60_00L, 35_00L), (Spent(after, "Groceries"), Spent(after, "Eating out"))); // groceries net of her half
        Assert.Equal((25_00L, 0L), (after.PartnerReceivedCents, after.IncomeReceivedCents));
        txns = await All(c);
        Assert.Equal(("Refund", "Purchase", "PartnerContribution"),
            (txns.Single(t => t.Id == payback.Id).Kind, txns.Single(t => t.Id == sent.Id).Kind, txns.Single(t => t.Id == gift.Id).Kind));

        // Labels stick through a re-run.
        await c.PostAsync("/api/transactions/auto-categorize", null);
        Assert.Equal("Groceries", (await All(c)).Single(t => t.Id == payback.Id).CategoryName);
    }

    [Fact]
    public async Task Years_of_small_transfers_from_her_stay_her_contribution_and_only_new_ones_wait()
    {
        // What happened with real data: two years of small Zelles from her. Those stay her money; only ones after you
        // name her wait for a label.
        var c = await NewUserAsync();
        var checking = await AccountAsync(c, "Checking", "1111");
        await ImportAsync(c, checking, ChaseChecking,
            "CREDIT,03/04/2026,\"Zelle payment from JANE PARTNER 1\",42.00,PARTNERFI_TO_CHASE,1042.00,,",
            "CREDIT,07/09/2026,\"Zelle payment from JANE PARTNER 2\",18.75,PARTNERFI_TO_CHASE,1060.75,,",
            "CREDIT,10/21/2026,\"Zelle payment from JANE PARTNER 3\",800.00,PARTNERFI_TO_CHASE,1860.75,,");
        await c.PutAsJsonAsync("/api/settings/partner", new PartnerSettingsDto("Jane Partner"), Json); // today, Nov 20

        Assert.All(await All(c), t => Assert.Equal(("PartnerContribution", false), (t.Kind, t.NeedsReview))); // her money, as before

        await ImportAsync(c, checking, ChaseChecking, "CREDIT,11/20/2026,\"Zelle payment from JANE PARTNER 4\",25.00,PARTNERFI_TO_CHASE,1885.75,,");
        var waiting = (await All(c)).Single(t => t.Description.EndsWith(" 4"));
        Assert.Equal(("PartnerTransfer", true), (waiting.Kind, waiting.NeedsReview)); // today counts as "after"
    }

    [Fact]
    public async Task Splitting_subscriptions_gives_each_its_own_line_and_moves_their_charges()
    {
        var c = await NewUserAsync(); // has a grouped "Subscriptions"
        var card = await AccountAsync(c, "CreditCard", "4321");
        foreach (var m in new[] { 8, 9, 10 })
            await ImportAsync(c, card, ChaseCard,
                $"{m:00}/03/2026,{m:00}/04/2026,NETFLIX.COM,Entertainment,Sale,-15.49,",
                $"{m:00}/05/2026,{m:00}/06/2026,SPOTIFY USA,Entertainment,Sale,-11.99,");
        var subs = (await c.GetFromJsonAsync<List<CategoryDto>>("/api/budget/categories", Json))!.Single(x => x.Name == "Subscriptions").Id;
        var first = (await All(c)).First(t => t.Merchant == "NETFLIX.COM");
        await c.PatchAsJsonAsync($"/api/transactions/{first.Id}", new UpdateTransactionRequest(subs, false, null), Json); // one you filed yourself

        var d = (await c.GetFromJsonAsync<CategoryDiscoveryDto>("/api/budget/categories/discover", Json))!;
        var bills = d.Proposed.Where(p => p.Type == "FixedBill").ToList();
        Assert.Equal(["Netflix", "Spotify"], bills.Select(p => p.Name).Order());
        Assert.All(bills, p => Assert.Equal("Subscriptions", p.Replaces));
        Assert.Contains(d.Unused, u => u.Name == "Subscriptions");

        await c.PostAsJsonAsync("/api/budget/categories/discover/apply", new ApplyDiscoveryRequest(
            bills.Select(p => new NewCategoryRequest(p.Name, CategoryType.FixedBill, false, p.MerchantPattern, p.Replaces)).ToList(),
            d.Unused.Where(u => u.Name == "Subscriptions").Select(u => u.Id).ToList()), Json);
        Assert.All((await All(c)).Where(t => t.Merchant == "NETFLIX.COM"), t => Assert.Equal("Netflix", t.CategoryName));
        Assert.All((await All(c)).Where(t => t.Merchant == "SPOTIFY USA"), t => Assert.Equal("Spotify", t.CategoryName));

        await ImportAsync(c, card, ChaseCard, "11/08/2026,11/09/2026,NETFLIX.COM,Entertainment,Sale,-15.49,");
        var next = (await All(c)).Single(t => t.Date == new DateOnly(2026, 11, 8));
        Assert.Equal(("Netflix", "Rule", true), (next.CategoryName, next.CategorySource, next.NeedsReview)); // still yours to confirm
    }

    // ---- Plaid ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Plaid_confidence_decides_likely_or_unsure_and_a_resync_backfills_without_duplicates()
    {
        var c = await NewUserAsync();
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
        Assert.Equal(("Eating out", true), (txns.Single(t => t.Merchant == "CORNER BISTRO").CategoryName, txns.Single(t => t.Merchant == "CORNER BISTRO").Unsure));
        Assert.Equal(("Groceries", false), (txns.Single(t => t.Merchant == "KING SOOPERS").CategoryName, txns.Single(t => t.Merchant == "KING SOOPERS").Unsure));
        Assert.All(txns, t => Assert.True(t.NeedsReview));

        app.Plaid.QueueSync(access, added: [Txn("a", "CORNER BISTRO", "FOOD_AND_DRINK_RESTAURANT", "HIGH"), Txn("b", "KING SOOPERS", "FOOD_AND_DRINK_GROCERIES", "VERY_HIGH")]);
        var res = await c.PostAsync($"/api/plaid/items/{link.ItemId}/resync", null);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());

        var after = await All(c);
        Assert.Equal(2, after.Count);
        Assert.False(after.Single(t => t.Merchant == "CORNER BISTRO").Unsure);
        Assert.Equal("FOOD_AND_DRINK_RESTAURANT", after.Single(t => t.Merchant == "CORNER BISTRO").BankCategory);
    }
}
