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
using SmoothBudget.Api.Seeding;
using SmoothBudget.Api.Transactions;

namespace SmoothBudget.Api.Tests;

/// <summary>
/// Milestone 2 "done when": a month of exports from a checking account and two cards imports with no
/// double-counted card payments. Every row below is invented; the layouts follow the banks' CSV exports.
/// </summary>
public class ImportApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // Chase checking. Note Chase's trailing comma on data rows.
    private const string ChaseChecking = """
        Details,Posting Date,Description,Amount,Type,Balance,Check or Slip #
        CREDIT,11/06/2026,"PAYROLL DEMO AIR PPD ID: 1234567890",1700.86,ACH_CREDIT,2900.86,,
        DEBIT,11/07/2026,"MAPLE COURT APTS WEB PMTS",-1650.00,ACH_DEBIT,1287.86,,
        DEBIT,11/10/2026,"Card Purchase 11/09 King Soopers #0012 Denver CO Card 1234",-64.30,DEBIT_CARD,1223.56,,
        DEBIT,11/14/2026,"Payment to Chase card ending in 4321 11/14",-300.00,LOAN_PMT,923.56,,
        DEBIT,11/15/2026,"APPLECARD GSBANK PAYMENT 12345678901 WEB ID: 9999999999",-150.00,ACH_DEBIT,773.56,,
        DEBIT,11/18/2026,"DEMO CARD SVCS ONLINE PMT",-75.00,ACH_DEBIT,698.56,,
        CREDIT,11/20/2026,"Zelle payment from PARTNER DEMO 24681357",800.00,PARTNERFI_TO_CHASE,1498.56,,
        DEBIT,11/21/2026,"Online Transfer to SAV ...9876 transaction#: 1234567890 11/21",-100.00,ACCT_XFER,1398.56,,
        """;

    private const string ChaseCard = """
        Transaction Date,Post Date,Description,Category,Type,Amount,Memo
        11/08/2026,11/09/2026,TAQUERIA UNO,Food & Drink,Sale,-18.45,
        11/09/2026,11/10/2026,FUEL STOP #123,Gas,Sale,-44.10,
        11/11/2026,11/12/2026,BIG BOX STORE,Shopping,Sale,-36.99,
        11/13/2026,11/14/2026,BIG BOX STORE,Shopping,Return,12.00,
        11/14/2026,11/14/2026,Payment Thank You-Mobile,,Payment,300.00,
        11/16/2026,11/17/2026,MYSTERY MERCHANT,Professional Services,Sale,-20.00,
        """;

    private const string AppleCard = """
        Transaction Date,Clearing Date,Description,Merchant,Category,Type,Amount (USD),Purchased By
        11/10/2026,11/11/2026,"FRESH MARKET 0042 DENVER CO",Fresh Market,Grocery,Purchase,91.45,Demo Person
        11/12/2026,11/12/2026,"CINEMA 8 DENVER CO",Cinema 8,Entertainment,Purchase,14.00,Demo Person
        11/15/2026,11/15/2026,"ACH DEPOSIT INTERNET TRANSFER FROM ACCOUNT ENDING IN 1234",Payment,Payment,Payment,-150.00,Demo Person
        11/19/2026,11/19/2026,"ACH DEPOSIT INTERNET TRANSFER FROM ACCOUNT ENDING IN 1234",Payment,Payment,Payment,-75.00,Demo Person
        11/20/2026,11/21/2026,"NOODLE HOUSE DENVER CO",Noodle House,Restaurants,Purchase,32.10,Demo Person
        """;

    private sealed record Setup(HttpClient Client, Guid HouseholdId, Guid Checking, Guid Chase, Guid Apple);

    private async Task<Setup> SetupAsync()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 22, 19, 0, 0, TimeSpan.Zero)); // noon in Denver
        var client = app.CreateClient();
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        var householdId = (await reg.Content.ReadFromJsonAsync<MeResponse>(Json))!.HouseholdId;

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Categories.AddRange(DemoSeeder.DefaultCategories(householdId));
            await db.SaveChangesAsync();
        }

        async Task<Guid> Account(string name, string type, string mask)
        {
            var res = await client.PostAsJsonAsync("/api/accounts", new { name, type, mask }, Json);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            return (await res.Content.ReadFromJsonAsync<AccountDto>(Json))!.Id;
        }

        return new Setup(client, householdId,
            await Account("Chase Checking", "Checking", "1111"),
            await Account("Chase Freedom", "CreditCard", "4321"),
            await Account("Apple Card", "CreditCard", null!));
    }

    private static async Task<HttpResponseMessage> PostCsv(HttpClient client, Guid accountId, string csv, string fileName = "export.csv")
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(csv, Encoding.UTF8, "text/csv"), "file", fileName);
        form.Add(new StringContent(accountId.ToString()), "accountId");
        return await client.PostAsync("/api/import/csv", form);
    }

    private static async Task<ImportResultDto> Import(HttpClient client, Guid accountId, string csv)
    {
        var res = await PostCsv(client, accountId, csv);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<ImportResultDto>(Json))!;
    }

    private static async Task<List<TransactionDto>> Month(HttpClient client) =>
        (await client.GetFromJsonAsync<TransactionListDto>("/api/transactions", Json))!.Items.ToList();

    private async Task<Setup> ImportAllAsync()
    {
        var s = await SetupAsync();
        await Import(s.Client, s.Checking, ChaseChecking); // checking first: its card payments arrive before the card files
        await Import(s.Client, s.Chase, ChaseCard);
        await Import(s.Client, s.Apple, AppleCard);
        return s;
    }

    [Fact]
    public async Task Three_exports_import_with_no_double_counted_card_payments()
    {
        var s = await ImportAllAsync();
        var txns = await Month(s.Client);
        Assert.Equal(8 + 6 + 5, txns.Count);

        // Every card payment, on both sides, is a transfer and never spending.
        var payments = txns.Where(t => t.Kind == "CardPayment").ToList();
        Assert.Equal(6, payments.Count); // 300, 150 and 75, each seen from checking and from the card
        Assert.All(payments, p => Assert.True(p.IsTransfer, p.Description));

        // The checking side counts as a debt payment (SPEC section 3), the card side has no category.
        Assert.All(payments.Where(p => p.AccountId == s.Checking), p => Assert.Equal("Credit card payment", p.CategoryName));
        Assert.All(payments.Where(p => p.AccountId != s.Checking), p => Assert.Null(p.CategoryName));

        // The $75 payment had an unrecognized description; it was caught by pairing with Apple's payment row.
        Assert.True(txns.Single(t => t.Description == "DEMO CARD SVCS ONLINE PMT").IsTransfer);

        // The move to savings is a transfer too, even though the savings account isn't imported.
        Assert.True(txns.Single(t => t.Description.StartsWith("Online Transfer to SAV")).IsTransfer);

        // Paycheck and partner Zelle are income: not spending, not transfers, not in the inbox.
        Assert.All(txns.Where(t => t.Description.StartsWith("PAYROLL") || t.Description.StartsWith("Zelle")),
            t => Assert.Equal(("Income", false), (t.Kind, t.IsTransfer)));

        // Left to Spend only sees real purchases:
        // 18.45 + 44.10 + (36.99 - 12.00) + 91.45 + 14.00 + 32.10 = 225.09 of a 1,010.00 variable budget.
        var summary = (await s.Client.GetFromJsonAsync<HomeSummaryDto>("/api/home/summary", Json))!;
        Assert.Equal(225_09, summary.TotalVariableSpentCents);
        Assert.Equal(2, summary.UncategorizedCount); // the rent is recognized by its name
        // The two without a category (the $20.00 mystery merchant and $64.30 at King Soopers) are already spent, so they
        // come off Left to Spend until they're filed (DECISIONS.md 177).
        Assert.Equal(84_30, summary.UnfiledSpendingCents);
        Assert.Equal(1_010_00 - 225_09 - 84_30, summary.LeftToSpendCents);
    }

    [Fact]
    public async Task The_inbox_holds_only_purchases_without_a_category()
    {
        var s = await ImportAllAsync();
        var inbox = (await s.Client.GetFromJsonAsync<List<TransactionDto>>("/api/transactions/inbox", Json))!;

        Assert.Equal(["MYSTERY MERCHANT", "KING SOOPERS DENVER CO"], inbox.Select(t => t.Merchant));
        var count = await s.Client.GetFromJsonAsync<JsonElement>("/api/transactions/inbox/count");
        Assert.Equal(2, count.GetProperty("count").GetInt32());

        // The bank didn't label the rent, but its name says rent: it's filed there and waits for you to confirm.
        var rent = (await Month(s.Client)).Single(t => t.Merchant == "MAPLE COURT APTS WEB PMTS");
        Assert.Equal(("Rent", true), (rent.CategoryName, rent.NeedsReview));
    }

    [Fact]
    public async Task Bank_categories_are_mapped_to_budget_categories()
    {
        var s = await ImportAllAsync();
        var txns = await Month(s.Client);
        string? Cat(string merchant) => txns.First(t => t.Merchant == merchant).CategoryName;

        Assert.Equal("Eating out", Cat("TAQUERIA UNO"));
        Assert.Equal("Gas", Cat("FUEL STOP"));
        Assert.Equal("Groceries", Cat("FRESH MARKET"));
        Assert.Equal("Eating out", Cat("NOODLE HOUSE"));
        Assert.Equal("Shopping", txns.Single(t => t.Kind == "Refund").CategoryName);
    }

    [Fact]
    public async Task Reimporting_the_same_files_adds_nothing()
    {
        var s = await ImportAllAsync();

        var again = new[]
        {
            await Import(s.Client, s.Checking, ChaseChecking),
            await Import(s.Client, s.Chase, ChaseCard),
            await Import(s.Client, s.Apple, AppleCard),
        };

        Assert.All(again, r => Assert.Equal(0, r.Imported));
        Assert.Equal(8 + 6 + 5, again.Sum(r => r.Duplicates));
        Assert.Equal(19, (await Month(s.Client)).Count);
    }

    [Fact]
    public async Task An_overlapping_export_adds_only_the_new_rows()
    {
        var s = await ImportAllAsync();
        var later = ChaseCard + "\n11/21/2026,11/22/2026,TAQUERIA UNO,Food & Drink,Sale,-18.45,\n";

        var r = await Import(s.Client, s.Chase, later);

        Assert.Equal((1, 6), (r.Imported, r.Duplicates));
    }

    [Fact]
    public async Task Recategorizing_with_a_rule_applies_to_matching_rows_and_future_imports()
    {
        var s = await ImportAllAsync();
        var categories = (await s.Client.GetFromJsonAsync<List<CategoryDto>>("/api/budget/categories", Json))!;
        var groceries = categories.Single(c => c.Name == "Groceries").Id;
        var kingSoopers = (await Month(s.Client)).Single(t => t.Merchant == "KING SOOPERS DENVER CO");

        var res = await s.Client.PatchAsJsonAsync($"/api/transactions/{kingSoopers.Id}", new UpdateTransactionRequest(groceries, false, "king soopers"), Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var updated = (await res.Content.ReadFromJsonAsync<UpdateTransactionResult>(Json))!;
        Assert.Equal(("Groceries", "User"), (updated.Transaction.CategoryName, updated.Transaction.CategorySource));

        var rules = (await s.Client.GetFromJsonAsync<List<RuleDto>>("/api/rules", Json))!;
        Assert.Equal(("KING SOOPERS", "Groceries"), (rules.Single().Pattern, rules.Single().CategoryName));

        // A later export with another King Soopers trip is categorized by the rule.
        var next = ChaseChecking + "\nDEBIT,11/22/2026,\"Card Purchase 11/21 King Soopers #0099 Aurora CO Card 1234\",-23.10,DEBIT_CARD,1375.46,,\n";
        var r = await Import(s.Client, s.Checking, next);
        Assert.Equal(1, r.Imported);
        var newTrip = (await Month(s.Client)).Single(t => t.Merchant == "KING SOOPERS AURORA CO");
        Assert.Equal(("Groceries", "Rule"), (newTrip.CategoryName, newTrip.CategorySource));

        var summary = (await s.Client.GetFromJsonAsync<HomeSummaryDto>("/api/home/summary", Json))!;
        Assert.Equal(225_09 + 64_30 + 23_10, summary.TotalVariableSpentCents);
    }

    [Fact]
    public async Task A_rule_does_not_override_a_users_own_choice()
    {
        var s = await ImportAllAsync();
        var categories = (await s.Client.GetFromJsonAsync<List<CategoryDto>>("/api/budget/categories", Json))!;
        var shopping = categories.Single(c => c.Name == "Shopping").Id;
        var household = categories.Single(c => c.Name == "Household").Id;
        var bigBox = (await Month(s.Client)).Where(t => t.Merchant == "BIG BOX STORE").ToList();

        await s.Client.PatchAsJsonAsync($"/api/transactions/{bigBox[0].Id}", new UpdateTransactionRequest(household, false, null), Json);
        var res = await s.Client.PatchAsJsonAsync($"/api/transactions/{bigBox[1].Id}", new UpdateTransactionRequest(shopping, false, "BIG BOX"), Json);

        Assert.Equal(0, (await res.Content.ReadFromJsonAsync<UpdateTransactionResult>(Json))!.RuleAppliedTo);
        Assert.Equal("Household", (await Month(s.Client)).Single(t => t.Id == bigBox[0].Id).CategoryName);
    }

    [Fact]
    public async Task Marking_a_transaction_as_a_transfer_removes_it_from_spending_and_the_inbox()
    {
        var s = await ImportAllAsync();
        var mystery = (await Month(s.Client)).Single(t => t.Merchant == "MYSTERY MERCHANT");

        var res = await s.Client.PatchAsJsonAsync($"/api/transactions/{mystery.Id}", new UpdateTransactionRequest(null, true, null), Json);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var inbox = (await s.Client.GetFromJsonAsync<List<TransactionDto>>("/api/transactions/inbox", Json))!;
        Assert.DoesNotContain(inbox, t => t.Id == mystery.Id);
    }

    [Fact]
    public async Task A_card_export_cannot_go_into_a_checking_account()
    {
        var s = await SetupAsync();
        var res = await PostCsv(s.Client, s.Checking, AppleCard);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Apple Card", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unrecognized_file_is_rejected()
    {
        var s = await SetupAsync();
        var res = await PostCsv(s.Client, s.Checking, "Date,Amount\n11/01/2026,5.00");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Unrecognized", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Undoing_an_import_removes_exactly_its_rows()
    {
        var s = await ImportAllAsync();
        var batches = (await s.Client.GetFromJsonAsync<List<ImportBatchDto>>("/api/import/batches", Json))!;
        var chase = batches.Single(b => b.AccountId == s.Chase);

        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.DeleteAsync($"/api/import/batches/{chase.Id}")).StatusCode);

        var txns = await Month(s.Client);
        Assert.Equal(8 + 5, txns.Count);
        Assert.DoesNotContain(txns, t => t.AccountId == s.Chase);
        Assert.Equal(2, (await s.Client.GetFromJsonAsync<List<ImportBatchDto>>("/api/import/batches", Json))!.Count);
    }

    [Fact]
    public async Task Nothing_that_looks_like_an_account_number_or_a_persons_name_is_stored()
    {
        var s = await ImportAllAsync();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Transactions.Where(t => t.HouseholdId == s.HouseholdId)
            .Select(t => t.Description + " " + t.Merchant + " " + t.BankCategory + " " + t.Fingerprint).ToListAsync();

        Assert.All(stored, text =>
        {
            Assert.DoesNotContain("12345678901", text);
            Assert.DoesNotContain("1234567890", text);
            Assert.DoesNotContain("Demo Person", text); // Apple's "Purchased By"
        });
    }

    [Fact]
    public async Task Account_mask_must_be_only_the_last_four_digits()
    {
        var s = await SetupAsync();
        var res = await s.Client.PostAsJsonAsync("/api/accounts", new { name = "Bad", type = "Checking", mask = "123456789" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Another_household_cannot_import_into_or_see_my_accounts()
    {
        var mine = await SetupAsync();
        var other = await SetupAsync();

        var res = await PostCsv(other.Client, mine.Checking, ChaseChecking);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Account not found", await res.Content.ReadAsStringAsync());
        var accounts = (await other.Client.GetFromJsonAsync<List<AccountDto>>("/api/accounts", Json))!;
        Assert.DoesNotContain(accounts, a => a.Id == mine.Checking);
    }

    // Chase savings, newest first as Chase exports it (DECISIONS.md 186).
    private const string ChaseSavings = """
        Details,Posting Date,Description,Amount,Type,Balance,Check or Slip #
        CREDIT,11/21/2026,"Online Transfer from CHK ...1111 transaction#: 1234567890",100.00,ACCT_XFER,2600.00,,
        CREDIT,11/21/2026,"INTEREST PAYMENT",0.42,MISC_CREDIT,2500.00,,
        DEBIT,11/02/2026,"Online Transfer to CHK ...1111 transaction#: 1234567891",-50.00,ACCT_XFER,2499.58,,
        """;

    private const string OlderChaseSavings = """
        Details,Posting Date,Description,Amount,Type,Balance,Check or Slip #
        CREDIT,10/15/2026,"Online Transfer from CHK ...1111 transaction#: 1234567892",25.00,ACCT_XFER,2549.58,,
        """;

    [Fact]
    public async Task A_savings_csv_sets_the_balance_from_its_newest_line_and_an_older_file_does_not_roll_it_back()
    {
        var s = await SetupAsync();
        var res = await s.Client.PostAsJsonAsync("/api/accounts", new { name = "Chase Savings", type = "Savings", mask = "8810" }, Json);
        var savings = (await res.Content.ReadFromJsonAsync<AccountDto>(Json))!.Id;

        async Task<long> Balance()
        {
            using var scope = app.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Accounts.SingleAsync(a => a.Id == savings)).CurrentBalanceCents;
        }

        Assert.Equal(2_600_00L, (await Import(s.Client, savings, ChaseSavings)).BalanceSetCents);
        Assert.Equal(2_600_00L, await Balance());

        Assert.Null((await Import(s.Client, savings, OlderChaseSavings)).BalanceSetCents);
        Assert.Equal(2_600_00L, await Balance());

        // Importing the same file again (nothing new) still keeps the balance current.
        Assert.Equal(2_600_00L, (await Import(s.Client, savings, ChaseSavings)).BalanceSetCents);
        Assert.Null((await Import(s.Client, s.Chase, ChaseCard)).BalanceSetCents); // cards: no Balance column
    }
}
