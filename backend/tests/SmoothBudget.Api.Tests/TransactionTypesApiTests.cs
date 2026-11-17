using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Debt;
using SmoothBudget.Api.Import;
using SmoothBudget.Api.Payday;
using SmoothBudget.Api.Seeding;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Tests;

/// <summary>
/// One review and edit card for every transaction (DECISIONS.md 259): Spending, Bill, Income, Transfer or Interest; transfers
/// whose other half files itself; "most likely" for everything; filters on the Transactions page. All data invented.
/// </summary>
public class TransactionTypesApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private async Task<HttpClient> NewUserAsync()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero));
        var client = app.CreateClient();
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        var household = (await reg.Content.ReadFromJsonAsync<MeResponse>(Json))!.HouseholdId;
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Categories.AddRange(DemoSeeder.DefaultCategories(household));
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<Guid> AccountAsync(HttpClient c, string type, string mask) =>
        (await (await c.PostAsJsonAsync("/api/accounts", new { name = $"Test {type}", type, mask }, Json)).Content.ReadFromJsonAsync<AccountDto>(Json))!.Id;

    private static async Task ImportAsync(HttpClient c, Guid account, string header, params string[] rows)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(header + "\n" + string.Join("\n", rows), Encoding.UTF8, "text/csv"), "file", "x.csv");
        form.Add(new StringContent(account.ToString()), "accountId");
        var res = await c.PostAsync("/api/import/csv", form);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    private const string Card = "Transaction Date,Post Date,Description,Category,Type,Amount,Memo";
    private const string Bank = "Details,Posting Date,Description,Amount,Type,Balance,Check or Slip #";

    private static async Task<List<TransactionDto>> All(HttpClient c) =>
        (await c.GetFromJsonAsync<TransactionListDto>("/api/transactions?from=2026-01-01&to=2026-12-31", Json))!.Items.ToList();

    private static async Task<List<TransactionDto>> Search(HttpClient c, string query) =>
        (await c.GetFromJsonAsync<SearchResultDto>("/api/transactions/search?" + query, Json))!.Items.ToList();

    [Fact]
    public async Task Confirming_one_half_of_a_transfer_files_the_other_half_already_there()
    {
        var c = await NewUserAsync();
        var checking = await AccountAsync(c, "Checking", "1111");
        var savings = await AccountAsync(c, "Savings", "2222");
        // A week apart: too far apart for the app to call them a likely transfer on its own.
        await ImportAsync(c, checking, Bank, "DEBIT,11/02/2026,\"WEB XFER 8812\",-100.00,ACCT_XFER,900.00,,");
        await ImportAsync(c, savings, Bank, "CREDIT,11/09/2026,\"WEB XFER IN 4410\",100.00,ACCT_XFER,1100.00,,");
        var all = await All(c);
        var sent = all.Single(t => t.AccountId == checking);
        var landed = all.Single(t => t.AccountId == savings);
        Assert.True(sent.NeedsReview);
        Assert.True(landed.NeedsReview); // every transaction goes through Review

        var res = await c.PostAsJsonAsync($"/api/transactions/{sent.Id}/transfer", new TransferRequest(savings), Json);
        var result = (await res.Content.ReadFromJsonAsync<TransferResultDto>(Json))!;
        Assert.True(result.OtherHalfFound);
        Assert.Equal((true, "User", false, savings), (result.Transaction.IsTransfer, result.Transaction.CategorySource, result.Transaction.NeedsReview, result.Transaction.TransferAccountId));

        var other = (await All(c)).Single(t => t.Id == landed.Id);
        Assert.Equal((true, "Transfer", false, checking), (other.IsTransfer, other.CategorySource, other.NeedsReview, other.TransferAccountId));
        Assert.Empty(await Search(c, "unconfirmed=true"));
    }

    [Fact]
    public async Task The_other_half_coming_in_later_files_itself_and_skips_review()
    {
        var c = await NewUserAsync();
        var checking = await AccountAsync(c, "Checking", "1111");
        var savings = await AccountAsync(c, "Savings", "2222");
        await ImportAsync(c, checking, Bank, "DEBIT,11/02/2026,\"WEB XFER 8812\",-60.00,ACCT_XFER,900.00,,");
        var sent = (await All(c)).Single();
        var result = (await (await c.PostAsJsonAsync($"/api/transactions/{sent.Id}/transfer", new TransferRequest(savings), Json))
            .Content.ReadFromJsonAsync<TransferResultDto>(Json))!;
        Assert.False(result.OtherHalfFound);

        // Five days later, it shows up on savings.
        await ImportAsync(c, savings, Bank, "CREDIT,11/07/2026,\"WEB XFER IN 4410\",60.00,ACCT_XFER,1060.00,,");
        var landed = (await All(c)).Single(t => t.AccountId == savings);
        Assert.Equal((true, "Transfer", false, checking), (landed.IsTransfer, landed.CategorySource, landed.NeedsReview, landed.TransferAccountId));

        // Past the wait (10 days), a matching amount is just another deposit for Review.
        await ImportAsync(c, savings, Bank, "CREDIT,11/19/2026,\"WEB XFER IN 4411\",60.00,ACCT_XFER,1120.00,,");
        Assert.True((await All(c)).Single(t => t.Date == new DateOnly(2026, 11, 19)).NeedsReview);
    }

    [Fact]
    public async Task Same_amount_out_of_one_account_and_into_another_is_most_likely_a_transfer_but_still_reviewed()
    {
        var c = await NewUserAsync();
        var checking = await AccountAsync(c, "Checking", "1111");
        var savings = await AccountAsync(c, "Savings", "2222");
        await ImportAsync(c, checking, Bank, "DEBIT,11/10/2026,\"WEB XFER 7001\",-250.00,ACCT_XFER,750.00,,");
        await ImportAsync(c, savings, Bank, "CREDIT,11/12/2026,\"WEB XFER IN 7002\",250.00,ACCT_XFER,1250.00,,");

        var all = await All(c);
        Assert.All(all, t => Assert.Equal((true, true), (t.IsTransfer, t.NeedsReview)));
        Assert.Equal(savings, all.Single(t => t.AccountId == checking).TransferAccountId);
        Assert.Equal(checking, all.Single(t => t.AccountId == savings).TransferAccountId);

        // "Use most likely" on one settles both.
        await c.PostAsJsonAsync("/api/transactions/most-likely", new IdsRequest([all.Single(t => t.AccountId == checking).Id]), Json);
        Assert.All(await All(c), t => Assert.Equal((true, false), (t.IsTransfer, t.NeedsReview)));
    }

    [Fact]
    public async Task Applying_to_a_merchant_files_its_unconfirmed_charges_the_same_way()
    {
        var c = await NewUserAsync();
        var card = await AccountAsync(c, "CreditCard", "4321");
        await ImportAsync(c, card, Card,
            "11/03/2026,11/04/2026,CORNER CAFE 12,Shopping,Sale,-8.00,",
            "11/08/2026,11/09/2026,CORNER CAFE 12,Shopping,Sale,-9.00,",
            "11/12/2026,11/13/2026,CORNER CAFE 12,Shopping,Sale,-7.00,");
        var categories = (await c.GetFromJsonAsync<List<CategoryDto>>("/api/budget/categories", Json))!;
        var dining = categories.First(x => x.Name is "Dining" or "Restaurants" or "Eating out" or "Groceries").Id;
        var all = await All(c);
        await c.PatchAsJsonAsync($"/api/transactions/{all[0].Id}", new UpdateTransactionRequest(dining, false, null), Json);

        var res = await c.PostAsync($"/api/transactions/{all[0].Id}/apply-to-merchant", null);
        Assert.Equal(2, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applied").GetInt32());
        Assert.All(await All(c), t => Assert.Equal((dining, "User", false), (t.CategoryId!.Value, t.CategorySource, t.NeedsReview)));
    }

    [Fact]
    public async Task Interest_is_charged_by_a_debt_or_paid_by_savings()
    {
        var c = await NewUserAsync();
        var card = await AccountAsync(c, "CreditCard", "4321");
        var savings = await AccountAsync(c, "Savings", "2222");
        await c.PostAsJsonAsync("/api/debt", new DebtRequest("Test loan", 1_000_00, 1200, 50_00, null, null, null), Json);
        await ImportAsync(c, savings, Bank, "CREDIT,11/01/2026,\"INTEREST PAYMENT\",1.25,MISC_CREDIT,1001.25,,");
        await ImportAsync(c, card, Card, "11/05/2026,11/05/2026,LATE CHARGE ADJ,Fees & Adjustments,Fee,-4.00,");

        // Savings interest: most likely interest, counted nowhere, still reviewed.
        var earned = (await All(c)).Single(t => t.AccountId == savings);
        Assert.Equal(("Interest", savings, true, null), (earned.Kind, earned.TransferAccountId!.Value, earned.NeedsReview, earned.CategoryId));

        // You say the card charge was the loan's interest.
        var choices = (await c.GetFromJsonAsync<ChoicesDto>("/api/transactions/choices", Json))!;
        var loan = choices.Debts.Single(d => d.Name == "Test loan").Id;
        var charge = (await All(c)).Single(t => t.AccountId == card);
        var filed = (await (await c.PostAsJsonAsync($"/api/transactions/{charge.Id}/interest", new InterestRequest(loan, null), Json))
            .Content.ReadFromJsonAsync<TransactionDto>(Json))!;
        Assert.Equal((CardInterest.CategoryName, loan, false), (filed.CategoryName, filed.DebtAccountId!.Value, filed.NeedsReview));
        Assert.Equal(2, (await Search(c, "type=interest")).Count);

        // Then you say it was savings interest after all? Only money from a savings account: the card isn't one.
        var bad = await c.PostAsJsonAsync($"/api/transactions/{charge.Id}/interest", new InterestRequest(null, card), Json);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        var ok = await c.PostAsJsonAsync($"/api/transactions/{earned.Id}/interest", new InterestRequest(null, savings), Json);
        Assert.False((await ok.Content.ReadFromJsonAsync<TransactionDto>(Json))!.NeedsReview);
    }

    [Fact]
    public async Task Income_is_a_paycheck_a_friend_new_or_known_a_bonus_new_or_expected_or_other()
    {
        var c = await NewUserAsync();
        var checking = await AccountAsync(c, "Checking", "1111");
        await ImportAsync(c, checking, Bank,
            "CREDIT,11/03/2026,\"ZELLE FROM PAT EXAMPLE\",40.00,QUICKPAY_CREDIT,1040.00,,",
            "CREDIT,11/04/2026,\"ACME CO DIRECT DEP\",500.00,ACH_CREDIT,1540.00,,",
            "CREDIT,11/05/2026,\"STATE REFUND\",30.00,ACH_CREDIT,1570.00,,");
        var all = await All(c);
        Assert.All(all, t => Assert.True(t.NeedsReview));

        var zelle = all.Single(t => t.Merchant.Contains("PAT"));
        var res = await c.PostAsJsonAsync($"/api/transactions/{zelle.Id}/income", new MarkIncomeRequest("friend", NewFriendName: "Pat Example"), Json);
        var fromFriend = (await res.Content.ReadFromJsonAsync<TransactionDto>(Json))!;
        Assert.Equal(("Pat Example", "friend", false), (fromFriend.FriendName, fromFriend.IncomeType, fromFriend.NeedsReview));
        var choices = (await c.GetFromJsonAsync<ChoicesDto>("/api/transactions/choices", Json))!;
        Assert.Contains(choices.Friends, f => f.Name == "Pat Example");

        var bonus = all.Single(t => t.Merchant.Contains("ACME"));
        await c.PostAsJsonAsync($"/api/transactions/{bonus.Id}/income", new MarkIncomeRequest("bonus", NewBonusName: "Year-end bonus"), Json);
        var expected = (await c.GetFromJsonAsync<List<ExpectedIncomeDto>>("/api/payday/expected", Json))!.Single();
        Assert.Equal(("Year-end bonus", new DateOnly(2026, 11, 4)), (expected.Name, expected.LandedDate!.Value));
        choices = (await c.GetFromJsonAsync<ChoicesDto>("/api/transactions/choices", Json))!;
        Assert.Equal(bonus.Id, choices.Bonuses.Single().DepositTransactionId);

        var refund = all.Single(t => t.Merchant.Contains("STATE"));
        var other = (await (await c.PostAsJsonAsync($"/api/transactions/{refund.Id}/income", new MarkIncomeRequest("other"), Json))
            .Content.ReadFromJsonAsync<TransactionDto>(Json))!;
        Assert.Equal(("Income", "other", "User"), (other.Kind, other.IncomeType, other.CategorySource));

        Assert.Equal(3, (await Search(c, "type=income")).Count);
        Assert.Empty(await Search(c, "unconfirmed=true"));
    }

    [Fact]
    public async Task The_transactions_page_filters_by_type_account_and_unconfirmed()
    {
        var c = await NewUserAsync();
        var checking = await AccountAsync(c, "Checking", "1111");
        var card = await AccountAsync(c, "CreditCard", "4321");
        await ImportAsync(c, checking, Bank,
            "DEBIT,11/03/2026,\"MYSTERY SHOP\",-10.00,DEBIT_CARD,990.00,,",
            "CREDIT,11/04/2026,\"ACME CO DIRECT DEP\",500.00,ACH_CREDIT,1490.00,,");
        await ImportAsync(c, card, Card, "11/05/2026,11/06/2026,FRESH MARKET,Groceries,Sale,-55.00,");

        Assert.Equal(["ACME CO DIRECT DEP"], (await Search(c, "type=income")).Select(t => t.Merchant));
        Assert.Equal(2, (await Search(c, $"accountId={checking}")).Count);
        Assert.Equal(["FRESH MARKET"], (await Search(c, "accountType=CreditCard")).Select(t => t.Merchant));
        Assert.Equal(3, (await Search(c, "unconfirmed=true")).Count);
        Assert.Equal(2, (await Search(c, "type=spending")).Count);

        foreach (var scope in new[] { "month", "earlier", "older" })
            await c.PostAsJsonAsync("/api/transactions/review/most-likely", new ConfirmLikelyRequest(scope), Json);
        Assert.Empty(await Search(c, "unconfirmed=true"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.GetAsync("/api/transactions/search?type=nonsense")).StatusCode);
    }
}
