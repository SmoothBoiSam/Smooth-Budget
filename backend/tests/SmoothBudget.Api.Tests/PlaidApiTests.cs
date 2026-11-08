using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Home;
using SmoothBudget.Api.Import;
using SmoothBudget.Api.Plaid;
using SmoothBudget.Api.Seeding;
using SmoothBudget.Api.Transactions;

namespace SmoothBudget.Api.Tests;

/// <summary>Milestone 3 through the real API, with Plaid replaced by <see cref="FakePlaid"/>. All data invented.</summary>
public class PlaidApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly FakePlaid.Account Checking = new("acc-chk", "Fake Checking", "1111", "depository", "checking", 1_204.33m);
    private static readonly FakePlaid.Account Card = new("acc-card", "Fake Rewards Card", "4321", "credit", "credit card", 812.40m);

    private sealed record Linked(HttpClient Client, Guid HouseholdId, Guid ItemId, string AccessToken, SyncResultDto FirstSync);

    private async Task<(HttpClient Client, Guid HouseholdId)> RegisterAsync()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero)); // noon in Denver; budget month 11-06..12-06
        var client = app.CreateClient();
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        var householdId = (await reg.Content.ReadFromJsonAsync<MeResponse>(Json))!.HouseholdId;
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Categories.AddRange(DemoSeeder.DefaultCategories(householdId));
        await db.SaveChangesAsync();
        return (client, householdId);
    }

    private async Task<Linked> LinkAsync(HttpClient client, Guid householdId, Func<string, Task>? beforeConfirm = null,
        Func<LinkResultDto, List<AccountMappingRequest>>? mappings = null, params FakePlaid.Account[] accounts)
    {
        var publicToken = app.Plaid.NewLogin(accounts.Length > 0 ? accounts : [Checking, Card]);
        var access = app.Plaid.AccessTokenFor(publicToken);
        var res = await client.PostAsJsonAsync("/api/plaid/exchange", new ExchangeRequest(publicToken), Json);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var link = (await res.Content.ReadFromJsonAsync<LinkResultDto>(Json))!;
        if (beforeConfirm is not null)
            await beforeConfirm(access);

        var map = mappings?.Invoke(link) ?? link.Accounts.Select(a => new AccountMappingRequest(a.PlaidAccountId, "new", null)).ToList();
        var confirm = await client.PostAsJsonAsync($"/api/plaid/items/{link.ItemId}/accounts", new ConfirmAccountsRequest(map), Json);
        Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync());
        return new Linked(client, householdId, link.ItemId, access, (await confirm.Content.ReadFromJsonAsync<SyncResultDto>(Json))!);
    }

    private static async Task<List<SyncResultDto>> SyncAsync(HttpClient client)
    {
        var res = await client.PostAsync("/api/sync", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<List<SyncResultDto>>(Json))!;
    }

    private static async Task<List<TransactionDto>> Month(HttpClient client) =>
        (await client.GetFromJsonAsync<TransactionListDto>("/api/transactions", Json))!.Items.ToList();

    private static async Task<HomeSummaryDto> Home(HttpClient client) =>
        (await client.GetFromJsonAsync<HomeSummaryDto>("/api/home/summary", Json))!;

    // ---- The milestone 3 "done when" -------------------------------------------------------------

    [Fact]
    public async Task A_pending_purchase_is_not_counted_twice_after_it_posts()
    {
        var (client, household) = await RegisterAsync();
        var l = await LinkAsync(client, household, async access =>
            app.Plaid.QueueSync(access, added: [FakePlaid.Txn("p1", "acc-card", 25.00m, "2026-11-18", "TAQUERIA UNO", pending: true)]));

        Assert.Equal(1, l.FirstSync.Added);
        var pending = (await Month(client)).Single();
        Assert.True(pending.Pending);
        Assert.Equal("Eating out", pending.CategoryName);
        Assert.Equal(25_00, (await Home(client)).TotalVariableSpentCents); // pending counts immediately

        // The user files it somewhere else while it's still pending.
        var categories = (await client.GetFromJsonAsync<List<CategoryDto>>("/api/budget/categories", Json))!;
        var entertainment = categories.Single(c => c.Name == "Entertainment").Id;
        await client.PatchAsJsonAsync($"/api/transactions/{pending.Id}", new UpdateTransactionRequest(entertainment, false, null), Json);

        // It posts two days later with a tip: Plaid sends a new posted transaction pointing at the pending one, and removes the pending id.
        app.Plaid.QueueSync(l.AccessToken,
            added: [FakePlaid.Txn("t1", "acc-card", 30.00m, "2026-11-20", "TAQUERIA UNO", pendingId: "p1", authorized: "2026-11-18")],
            removed: ["p1"]);
        var result = (await SyncAsync(client)).Single();

        Assert.Equal((0, 1, 1, 0), (result.Added, result.Updated, result.PendingPosted, result.Removed));
        var posted = (await Month(client)).Single(); // still exactly one row
        Assert.Equal((pending.Id, false, 30_00L), (posted.Id, posted.Pending, posted.AmountCents));
        Assert.Equal(new DateOnly(2026, 11, 18), posted.Date); // the purchase date
        Assert.Equal("Entertainment", posted.CategoryName); // the user's choice survives posting
        Assert.Equal(30_00, (await Home(client)).TotalVariableSpentCents);
    }

    [Fact]
    public async Task Pending_and_posted_arriving_in_the_same_sync_count_once()
    {
        var (client, household) = await RegisterAsync();
        var l = await LinkAsync(client, household, async access => app.Plaid.QueueSync(access,
            added:
            [
                FakePlaid.Txn("t1", "acc-card", 18.45m, "2026-11-19", "TAQUERIA UNO", pendingId: "p1"),
                FakePlaid.Txn("p1", "acc-card", 18.45m, "2026-11-18", "TAQUERIA UNO", pending: true),
            ],
            removed: ["p1"]));

        Assert.Equal(1, l.FirstSync.Added);
        Assert.Equal(18_45, (await Home(client)).TotalVariableSpentCents);
    }

    [Fact]
    public async Task Sync_pages_are_all_read_before_saving_and_the_cursor_advances()
    {
        var (client, household) = await RegisterAsync();
        var l = await LinkAsync(client, household, async access =>
        {
            app.Plaid.QueueSync(access, added: [FakePlaid.Txn("a", "acc-card", 10m, "2026-11-10", "CINEMA 8", primary: "ENTERTAINMENT", detailed: "ENTERTAINMENT_TV_AND_MOVIES")], hasMore: true);
            app.Plaid.QueueSync(access, added: [FakePlaid.Txn("b", "acc-chk", 64.30m, "2026-11-12", "KING SOOPERS", primary: "FOOD_AND_DRINK", detailed: "FOOD_AND_DRINK_GROCERIES")]);
        });

        Assert.Equal(2, l.FirstSync.Added);
        var cursors = app.Plaid.Requests.Where(r => r.Path == "transactions/sync" && r.Body.Contains(l.AccessToken))
            .Select(r => JsonDocument.Parse(r.Body).RootElement.TryGetProperty("cursor", out var c) ? c.GetString() : null).ToList();
        Assert.Equal([null, "+"], cursors);

        await SyncAsync(client);
        var next = app.Plaid.Requests.Last(r => r.Path == "transactions/sync" && r.Body.Contains(l.AccessToken));
        Assert.Equal("++", JsonDocument.Parse(next.Body).RootElement.GetProperty("cursor").GetString());

        var txns = await Month(client);
        Assert.Equal("Groceries", txns.Single(t => t.Merchant == "KING SOOPERS").CategoryName); // detailed Plaid category wins
        Assert.Equal("Entertainment", txns.Single(t => t.Merchant == "CINEMA").CategoryName);
    }

    [Fact]
    public async Task Card_payments_from_Plaid_are_transfers_and_never_spending()
    {
        var (client, household) = await RegisterAsync();
        await LinkAsync(client, household, async access => app.Plaid.QueueSync(access, added:
        [
            FakePlaid.Txn("pay-out", "acc-chk", 300m, "2026-11-14", "Payment to Chase card ending in 4321", primary: "LOAN_PAYMENTS", detailed: "LOAN_PAYMENTS_CREDIT_CARD_PAYMENT"),
            FakePlaid.Txn("pay-in", "acc-card", -300m, "2026-11-14", "Payment Thank You-Mobile", primary: "LOAN_PAYMENTS", detailed: "LOAN_PAYMENTS_CREDIT_CARD_PAYMENT"),
            FakePlaid.Txn("food", "acc-card", 18.45m, "2026-11-12", "TAQUERIA UNO"),
            FakePlaid.Txn("pay", "acc-chk", -1700.86m, "2026-11-06", "PAYROLL DEMO AIR", primary: "INCOME", detailed: "INCOME_WAGES"),
        ]));

        var txns = await Month(client);
        Assert.All(txns.Where(t => t.Description.Contains("Payment")), t => Assert.Equal((true, "CardPayment"), (t.IsTransfer, t.Kind)));
        Assert.Equal("Credit card payment", txns.Single(t => t.Description.StartsWith("Payment to")).CategoryName);
        Assert.Equal("Income", txns.Single(t => t.Description.StartsWith("PAYROLL")).Kind);
        Assert.Equal(18_45, (await Home(client)).TotalVariableSpentCents);
    }

    // ---- Linking, accounts and CSV history -------------------------------------------------------

    [Fact]
    public async Task Linking_suggests_the_matching_CSV_account_and_does_not_duplicate_its_history()
    {
        var (client, household) = await RegisterAsync();
        var csvAccount = await (await client.PostAsJsonAsync("/api/accounts", new { name = "Chase Freedom", type = "CreditCard", mask = "4321" }, Json))
            .Content.ReadFromJsonAsync<AccountDto>(Json);
        const string csv = """
            Transaction Date,Post Date,Description,Category,Type,Amount,Memo
            11/08/2026,11/09/2026,TAQUERIA UNO,Food & Drink,Sale,-18.45,
            11/11/2026,11/12/2026,BIG BOX STORE,Shopping,Sale,-36.99,
            """;
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent(csv, Encoding.UTF8, "text/csv"), "file", "card.csv");
            form.Add(new StringContent(csvAccount!.Id.ToString()), "accountId");
            Assert.True((await client.PostAsync("/api/import/csv", form)).IsSuccessStatusCode);
        }

        LinkResultDto? proposal = null;
        var l = await LinkAsync(client, household,
            beforeConfirm: async access => app.Plaid.QueueSync(access, added:
            [
                FakePlaid.Txn("h1", "acc-card", 18.45m, "2026-11-09", "TAQUERIA UNO", authorized: "2026-11-08"),  // same as CSV row
                FakePlaid.Txn("h2", "acc-card", 36.99m, "2026-11-12", "BIG BOX STORE", primary: "GENERAL_MERCHANDISE", detailed: "GENERAL_MERCHANDISE_SUPERSTORES"), // same
                FakePlaid.Txn("n1", "acc-card", 14.00m, "2026-11-16", "CINEMA 8", primary: "ENTERTAINMENT", detailed: "ENTERTAINMENT_TV_AND_MOVIES"),         // new
            ]),
            mappings: link =>
            {
                proposal = link;
                return link.Accounts.Select(a => new AccountMappingRequest(a.PlaidAccountId,
                    a.SuggestedAccountId is null ? "skip" : "existing", a.SuggestedAccountId)).ToList();
            },
            accounts: [Checking, Card]);

        Assert.Equal(csvAccount.Id, proposal!.Accounts.Single(a => a.PlaidAccountId.StartsWith("acc-card")).SuggestedAccountId);
        Assert.Null(proposal.Accounts.Single(a => a.PlaidAccountId.StartsWith("acc-chk")).SuggestedAccountId);
        Assert.Equal((1, 2), (l.FirstSync.Added, l.FirstSync.MatchedToCsv));

        var txns = await Month(client);
        Assert.Equal(3, txns.Count); // two CSV rows + one new, no duplicates
        Assert.Equal(18_45 + 36_99 + 14_00, (await Home(client)).TotalVariableSpentCents);
        var accounts = (await client.GetFromJsonAsync<List<AccountDto>>("/api/accounts", Json))!;
        Assert.Single(accounts); // the checking account was skipped
    }

    private static async Task<ImportResultDto> ImportCsvAsync(HttpClient client, Guid accountId, string csv)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(csv, Encoding.UTF8, "text/csv"), "file", "card.csv");
        form.Add(new StringContent(accountId.ToString()), "accountId");
        var res = await client.PostAsync("/api/import/csv", form);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<ImportResultDto>(Json))!;
    }

    [Fact]
    public async Task Plaid_first_then_a_CSV_for_the_same_account_is_not_counted_twice()
    {
        var (client, household) = await RegisterAsync();
        await LinkAsync(client, household, async access => app.Plaid.QueueSync(access, added:
        [
            FakePlaid.Txn("s1", "acc-card", 18.45m, "2026-11-09", "TAQUERIA UNO", authorized: "2026-11-08"),
            FakePlaid.Txn("s2", "acc-card", 44.10m, "2026-11-10", "FUEL STOP", primary: "TRANSPORTATION", detailed: "TRANSPORTATION_GAS"),
            FakePlaid.Txn("s3", "acc-card", 36.99m, "2026-11-12", "BIG BOX STORE", primary: "GENERAL_MERCHANDISE", detailed: "GENERAL_MERCHANDISE_SUPERSTORES"),
            FakePlaid.Txn("s4", "acc-card", 14.00m, "2026-11-16", "CINEMA 8", primary: "ENTERTAINMENT", detailed: "ENTERTAINMENT_TV_AND_MOVIES"),
            FakePlaid.Txn("s5", "acc-card", 50.00m, "2026-11-28", "FRESH MARKET", primary: "FOOD_AND_DRINK", detailed: "FOOD_AND_DRINK_GROCERIES"),
        ]), accounts: [Card]);
        var card = (await client.GetFromJsonAsync<List<AccountDto>>("/api/accounts", Json))!.Single();
        Assert.True(card.BankSynced);
        var spentBefore = (await Home(client)).TotalVariableSpentCents;

        // The user then uploads a Chase credit CSV for the same card (CSV uses purchase dates).
        const string csv = """
            Transaction Date,Post Date,Description,Category,Type,Amount,Memo
            11/02/2026,11/03/2026,OLD PURCHASE,Shopping,Sale,-9.99,
            11/08/2026,11/09/2026,TAQUERIA UNO,Food & Drink,Sale,-18.45,
            11/10/2026,11/10/2026,FUEL STOP #123,Gas,Sale,-44.10,
            11/11/2026,11/12/2026,BIG BOX STORE,Shopping,Sale,-36.99,
            11/13/2026,11/14/2026,ODD CHARGE,Shopping,Sale,-20.00,
            11/16/2026,11/17/2026,CINEMA 8,Entertainment,Sale,-14.00,
            """;
        var r = await ImportCsvAsync(client, card.Id, csv);

        Assert.Equal(4, r.AlreadySynced);  // same purchases Plaid already brought in
        Assert.Equal(1, r.CoveredBySync);  // no exact match, but well inside the synced period
        Assert.Equal(1, r.Imported);       // from before the bank sync's history started
        Assert.Equal(spentBefore, (await Home(client)).TotalVariableSpentCents); // nothing counted twice
        var all = (await client.GetFromJsonAsync<TransactionListDto>("/api/transactions?from=2026-10-01&to=2026-12-31", Json))!.Items;
        Assert.Equal(6, all.Count);

        // Uploading the same file again adds nothing either.
        var again = await ImportCsvAsync(client, card.Id, csv);
        Assert.Equal(0, again.Imported);
        Assert.Equal(6, (await client.GetFromJsonAsync<TransactionListDto>("/api/transactions?from=2026-10-01&to=2026-12-31", Json))!.Items.Count);
    }

    [Fact]
    public async Task A_CSV_into_a_second_account_for_a_bank_synced_card_is_refused()
    {
        var (client, household) = await RegisterAsync();
        await LinkAsync(client, household, accounts: [Card]); // syncs the card ••4321
        var twin = (await (await client.PostAsJsonAsync("/api/accounts", new { name = "Chase Freedom (CSV)", type = "CreditCard", mask = "4321" }, Json))
            .Content.ReadFromJsonAsync<AccountDto>(Json))!;

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Transaction Date,Post Date,Description,Category,Type,Amount,Memo\n11/08/2026,11/09/2026,TAQUERIA UNO,Food & Drink,Sale,-18.45,", Encoding.UTF8, "text/csv"), "file", "card.csv");
        form.Add(new StringContent(twin.Id.ToString()), "accountId");
        var res = await client.PostAsync("/api/import/csv", form);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("already syncs this account", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Only_checking_savings_and_cards_can_be_imported_and_masks_are_last_four_only()
    {
        var (client, household) = await RegisterAsync();
        var loan = new FakePlaid.Account("acc-loan", "Car Loan", "99887766", "loan", "auto");
        var publicToken = app.Plaid.NewLogin(Checking, loan);
        var link = (await (await client.PostAsJsonAsync("/api/plaid/exchange", new ExchangeRequest(publicToken), Json)).Content.ReadFromJsonAsync<LinkResultDto>(Json))!;

        Assert.Null(link.Accounts.Single(a => a.PlaidAccountId.StartsWith("acc-loan")).AccountType);
        Assert.Equal("7766", link.Accounts.Single(a => a.PlaidAccountId.StartsWith("acc-loan")).Mask);
        var res = await client.PostAsJsonAsync($"/api/plaid/items/{link.ItemId}/accounts",
            new ConfirmAccountsRequest([new(link.Accounts.Single(a => a.PlaidAccountId.StartsWith("acc-loan")).PlaidAccountId, "new", null)]), Json);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Access_tokens_are_encrypted_at_rest_and_never_returned()
    {
        var (client, household) = await RegisterAsync();
        var l = await LinkAsync(client, household);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.PlaidItems.SingleAsync(i => i.Id == l.ItemId);
        Assert.DoesNotContain("access-sandbox", stored.AccessTokenEncrypted);

        var items = await client.GetStringAsync("/api/plaid/items");
        Assert.DoesNotContain("access-", items);
        Assert.Contains("Fake Bank", items);

        // Credentials travel in headers on every Plaid call.
        Assert.All(app.Plaid.Requests, r => Assert.Equal(("test-client-id", "test-secret"), (r.ClientId, r.Secret)));
    }

    [Fact]
    public async Task Removing_a_connection_keeps_accounts_and_history()
    {
        var (client, household) = await RegisterAsync();
        var l = await LinkAsync(client, household, async access => app.Plaid.QueueSync(access, added: [FakePlaid.Txn("x", "acc-card", 9.99m, "2026-11-15", "TAQUERIA UNO")]));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/plaid/items/{l.ItemId}")).StatusCode);

        Assert.Single(await Month(client));
        Assert.Equal(2, (await client.GetFromJsonAsync<List<AccountDto>>("/api/accounts", Json))!.Count);
        Assert.Empty((await client.GetFromJsonAsync<List<PlaidItemDto>>("/api/plaid/items", Json))!);
        Assert.Contains(app.Plaid.Requests, r => r.Path == "item/remove" && r.Body.Contains(l.AccessToken));
    }

    [Fact]
    public async Task Another_household_cannot_touch_my_connection()
    {
        var (mine, mineHousehold) = await RegisterAsync();
        var l = await LinkAsync(mine, mineHousehold);
        var (other, _) = await RegisterAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/plaid/items/{l.ItemId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsJsonAsync($"/api/plaid/items/{l.ItemId}/accounts",
            new ConfirmAccountsRequest([]), Json)).StatusCode);
        Assert.Empty((await other.GetFromJsonAsync<List<PlaidItemDto>>("/api/plaid/items", Json))!);
    }

    // ---- Failures and staleness are visible on Home ----------------------------------------------

    [Fact]
    public async Task A_bank_that_needs_a_new_login_shows_on_Home_and_reconnecting_clears_it()
    {
        var (client, household) = await RegisterAsync();
        var l = await LinkAsync(client, household);
        Assert.Equal("ok", (await Home(client)).Sync.State);

        app.Plaid.SyncErrors[l.AccessToken] = "ITEM_LOGIN_REQUIRED";
        var failed = (await SyncAsync(client)).Single();

        Assert.False(failed.Ok);
        var home = await Home(client);
        Assert.Equal("error", home.Sync.State);
        Assert.Contains("sign in again", home.Sync.Message);
        Assert.Equal("LoginRequired", (await client.GetFromJsonAsync<List<PlaidItemDto>>("/api/plaid/items", Json))!.Single().Status);

        var res = await client.PostAsync($"/api/plaid/items/{l.ItemId}/reconnected", null);
        Assert.True(res.IsSuccessStatusCode);
        Assert.Equal("ok", (await Home(client)).Sync.State);
    }

    [Fact]
    public async Task Data_older_than_a_day_is_flagged_stale()
    {
        var (client, household) = await RegisterAsync();
        await LinkAsync(client, household);

        app.Clock.SetUtcNow(app.Clock.GetUtcNow().AddHours(25));

        var home = await Home(client);
        Assert.Equal("stale", home.Sync.State);
        Assert.Contains("hasn't synced", home.Sync.Message);
    }

    [Fact]
    public async Task Without_bank_connections_Home_reports_none()
    {
        var (client, _) = await RegisterAsync();
        Assert.Equal("none", (await Home(client)).Sync.State);
    }

    // ---- Webhooks --------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> PostWebhook(object payload, Func<byte[], string>? sign = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/plaid/webhook") { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new("application/json");
        req.Headers.Add("Plaid-Verification", (sign ?? (b => app.WebhookKeys.Sign(b, app.Clock.GetUtcNow())))(body));
        return await app.CreateClient().SendAsync(req);
    }

    [Fact]
    public async Task A_signed_sync_webhook_triggers_a_background_sync()
    {
        var (client, household) = await RegisterAsync();
        var l = await LinkAsync(client, household);
        app.Plaid.QueueSync(l.AccessToken, added: [FakePlaid.Txn("wh1", "acc-card", 12.75m, "2026-11-19", "COFFEE CART")]);

        var res = await PostWebhook(new { webhook_type = "TRANSACTIONS", webhook_code = "SYNC_UPDATES_AVAILABLE", item_id = FakePlaid.ItemIdFor(l.AccessToken) });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        for (var i = 0; i < 50 && (await Month(client)).Count == 0; i++)
            await Task.Delay(100);
        Assert.Equal("COFFEE CART", Assert.Single(await Month(client)).Merchant);
    }

    [Fact]
    public async Task An_item_error_webhook_marks_the_connection()
    {
        var (client, household) = await RegisterAsync();
        var l = await LinkAsync(client, household);

        await PostWebhook(new { webhook_type = "ITEM", webhook_code = "ERROR", item_id = FakePlaid.ItemIdFor(l.AccessToken), error = new { error_code = "ITEM_LOGIN_REQUIRED" } });

        Assert.Equal("error", (await Home(client)).Sync.State);
    }

    [Fact]
    public async Task Unsigned_tampered_old_or_wrongly_signed_webhooks_are_rejected()
    {
        var payload = new { webhook_type = "TRANSACTIONS", webhook_code = "SYNC_UPDATES_AVAILABLE", item_id = "item-x" };
        using var otherKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhook(payload, _ => "")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhook(payload, _ => "not.a.jwt")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhook(payload, b => app.WebhookKeys.Sign([.. b, (byte)' '], app.Clock.GetUtcNow()))).StatusCode); // body changed
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhook(payload, b => app.WebhookKeys.Sign(b, app.Clock.GetUtcNow().AddMinutes(-6)))).StatusCode); // too old
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhook(payload, b => app.WebhookKeys.Sign(b, app.Clock.GetUtcNow(), signWith: otherKey))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhook(payload, b => app.WebhookKeys.Sign(b, app.Clock.GetUtcNow(), alg: "none"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(payload)).StatusCode); // correctly signed, unknown item: acknowledged
    }

    [Fact]
    public async Task Connecting_the_same_bank_again_points_to_adding_accounts_on_the_first_connection()
    {
        var (client, household) = await RegisterAsync();
        var first = await LinkAsync(client, household);

        // A second login to the same bank (to share a new savings account) is a second connection with new account ids.
        var again = (await (await client.PostAsJsonAsync("/api/plaid/exchange", new ExchangeRequest(app.Plaid.NewLogin(Checking, Card)), Json))
            .Content.ReadFromJsonAsync<LinkResultDto>(Json))!;
        Assert.Equal(first.ItemId, again.DuplicateOfItemId);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/plaid/items/{again.ItemId}")).StatusCode);

        // Add accounts opens Link in update mode with account selection on the first connection.
        Assert.True((await client.PostAsJsonAsync("/api/plaid/link-token", new LinkTokenRequest(first.ItemId, AddAccounts: true), Json)).IsSuccessStatusCode);
        Assert.Contains(app.Plaid.Requests, r => r.Path == "link/token/create" && r.Body.Contains("account_selection_enabled"));
        app.Plaid.AddAccount(first.AccessToken, new FakePlaid.Account("acc-sav", "Fake Savings", "2222", "depository", "savings", 50m));

        var setup = (await client.GetFromJsonAsync<LinkResultDto>($"/api/plaid/items/{first.ItemId}/accounts", Json))!;
        Assert.Equal(3, setup.Accounts.Count);
        Assert.Null(setup.DuplicateOfItemId);
        var map = setup.Accounts.Select(a => a.SuggestedAccountId is { } id
            ? new AccountMappingRequest(a.PlaidAccountId, "existing", id) : new AccountMappingRequest(a.PlaidAccountId, "new", null)).ToList();
        Assert.Equal(2, map.Count(m => m.Action == "existing")); // the accounts already syncing stay as they are
        var confirm = await client.PostAsJsonAsync($"/api/plaid/items/{first.ItemId}/accounts", new ConfirmAccountsRequest(map), Json);
        Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync());

        var accounts = (await client.GetFromJsonAsync<List<AccountDto>>("/api/accounts", Json))!;
        Assert.Equal(["Fake Checking", "Fake Rewards Card", "Fake Savings"], accounts.Select(a => a.Name).Order().ToList());
        Assert.Equal("Savings", accounts.Single(a => a.Name == "Fake Savings").Type);
    }
}
