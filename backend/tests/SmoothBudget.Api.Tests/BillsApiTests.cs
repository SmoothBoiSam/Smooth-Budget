using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Bills;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Home;
using SmoothBudget.Api.Import;
using SmoothBudget.Api.Seeding;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Tests;

/// <summary>Milestone 6: bills and subscriptions are found, a price change is flagged, and the budget uses current prices. All data invented.</summary>
public class BillsApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string ChaseCard = "Transaction Date,Post Date,Description,Category,Type,Amount,Memo";
    private const string ChaseChecking = "Details,Posting Date,Description,Amount,Type,Balance,Check or Slip #";

    private async Task<HttpClient> NewUserAsync()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 6, 19, 0, 0, TimeSpan.Zero));
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

    private static async Task ImportAsync(HttpClient c, Guid account, string header, IEnumerable<string> rows)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(header + "\n" + string.Join("\n", rows), Encoding.UTF8, "text/csv"), "file", "x.csv");
        form.Add(new StringContent(account.ToString()), "accountId");
        var res = await c.PostAsync("/api/import/csv", form);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    private static Task<RecurringSummaryDto?> Bills(HttpClient c) => c.GetFromJsonAsync<RecurringSummaryDto>("/api/recurring", Json);

    /// <summary>StreamCo at $15.49 from May to September, $17.99 in October (labeled "Entertainment" by the bank); a varying electric bill; weekly groceries.</summary>
    private async Task<HttpClient> HistoryAsync()
    {
        var c = await NewUserAsync();
        var card = await AccountAsync(c, "CreditCard", "4321");
        var checking = await AccountAsync(c, "Checking", "1111");
        var stream = new[] { 5, 6, 7, 8, 9 }.Select(m => $"{m:00}/03/2026,{m:00}/04/2026,STREAMCO.COM,Entertainment,Sale,-15.49,")
            .Append("10/03/2026,10/04/2026,STREAMCO.COM,Entertainment,Sale,-17.99,");
        var groceries = Enumerable.Range(0, 20).Select(i => new DateOnly(2026, 6, 2).AddDays(i * 7))
            .Select((d, i) => $"{d:MM/dd/yyyy},{d:MM/dd/yyyy},CORNER GROCERY #12,Groceries,Sale,-{40 + i * 3 % 50}.{i:00},");
        await ImportAsync(c, card, ChaseCard, stream.Concat(groceries));
        long[] electric = [92_10, 81_44, 104_90, 131_02, 118_75, 88_60];
        await ImportAsync(c, checking, ChaseChecking, electric.Select((a, i) =>
            $"DEBIT,{5 + i:00}/12/2026,\"CITY ELECTRIC 5550001 WEB PMT\",-{a / 100}.{a % 100:00},ACH_DEBIT,1000.00,,"));
        return c;
    }

    [Fact]
    public async Task Recurring_items_are_found_and_the_price_change_is_flagged()
    {
        var c = await HistoryAsync();
        var s = (await Bills(c))!;

        Assert.Equal(["City Electric", "Streamco"], s.Items.Select(i => i.Name).Order()); // groceries aren't a bill
        var stream = s.Items.Single(i => i.Name == "Streamco");
        Assert.Equal(("Subscription", "Monthly", "priceUp", 17_99L, 15_49L), (stream.Kind, stream.Frequency, stream.Status, stream.AmountCents, stream.PreviousAmountCents!.Value));
        Assert.Equal((new DateOnly(2026, 11, 3), "Entertainment", false), (stream.NextExpected, stream.CategoryName, stream.InBillCategory));
        var electric = s.Items.Single(i => i.Name == "City Electric");
        Assert.Equal(("Bill", "active", 88_60L), (electric.Kind, electric.Status, electric.AmountCents));

        Assert.Equal((17_99L + 88_60L, (17_99L + 88_60L) * 12), (s.MonthlyCents, s.AnnualCents));
        Assert.Equal(1, s.AlertCount);

        // "Got it" clears the flag until the price changes again.
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync("/api/recurring", new RecurringUpdateRequest(stream.Key, AcknowledgePrice: true), Json)).StatusCode);
        Assert.Equal("active", (await Bills(c))!.Items.Single(i => i.Key == stream.Key).Status);
    }

    [Fact]
    public async Task A_streaming_charge_labeled_entertainment_becomes_its_own_bill_and_the_budget_uses_the_new_price()
    {
        var c = await HistoryAsync();
        var stream = (await Bills(c))!.Items.Single(i => i.Name == "Streamco");

        var res = await c.PostAsJsonAsync("/api/recurring/track-as-bill", new TrackAsBillRequest(stream.Key), Json);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());

        var after = (await Bills(c))!.Items.Single(i => i.Key == stream.Key);
        Assert.Equal(("Streamco", true, true), (after.CategoryName, after.InBillCategory, after.Confirmed));
        var txns = (await c.GetFromJsonAsync<TransactionListDto>("/api/transactions?from=2026-01-01&to=2026-12-31", Json))!.Items;
        Assert.All(txns.Where(t => t.Merchant == "STREAMCO.COM"), t => Assert.Equal(("Streamco", "User"), (t.CategoryName, t.CategorySource)));

        // A new charge follows the merchant rule onto the same line.
        var card = (await c.GetFromJsonAsync<List<AccountDto>>("/api/accounts", Json))!.Single(a => a.Mask == "4321").Id;
        await ImportAsync(c, card, ChaseCard, ["11/03/2026,11/04/2026,STREAMCO.COM,Entertainment,Sale,-17.99,"]);
        txns = (await c.GetFromJsonAsync<TransactionListDto>("/api/transactions?from=2026-01-01&to=2026-12-31", Json))!.Items;
        Assert.Equal("Streamco", txns.Single(t => t.Date == new DateOnly(2026, 11, 3)).CategoryName);

        // The generator proposes the bill at its current price, not the old median.
        var g = (await c.GetFromJsonAsync<GeneratorDto>("/api/budget/generator", Json))!;
        var line = g.Categories.Single(x => x.Name == "Streamco");
        Assert.Equal(17_99, line.ProposedCents);
        Assert.StartsWith("Current price: Streamco $17.99 a month", line.Explanation);
    }

    [Fact]
    public async Task You_can_rename_set_a_due_day_dismiss_or_say_you_canceled()
    {
        var c = await HistoryAsync();
        var electric = (await Bills(c))!.Items.Single(i => i.Name == "City Electric");

        await c.PutAsJsonAsync("/api/recurring", new RecurringUpdateRequest(electric.Key, Name: "Electricity", DueDay: 20, Kind: RecurringKind.Bill), Json);
        var e = (await Bills(c))!.Items.Single(i => i.Key == electric.Key);
        Assert.Equal(("Electricity", "City Electric", 20, new DateOnly(2026, 10, 20)), (e.Name, e.DetectedName, e.DueDay!.Value, e.NextExpected));

        var stream = (await Bills(c))!.Items.Single(i => i.Name == "Streamco");
        await c.PutAsJsonAsync("/api/recurring", new RecurringUpdateRequest(stream.Key, Canceled: true), Json);
        var s = (await Bills(c))!;
        Assert.Equal("canceled", s.Items.Single(i => i.Key == stream.Key).Status);
        Assert.Equal(e.MonthlyCents, s.MonthlyCents); // canceled doesn't count

        await c.PutAsJsonAsync("/api/recurring", new RecurringUpdateRequest(electric.Key, Dismissed: true), Json);
        s = (await Bills(c))!;
        Assert.DoesNotContain(s.Items, i => i.Key == electric.Key);
        Assert.Equal("Electricity", Assert.Single(s.Dismissed).Name);
        Assert.Equal(0, s.MonthlyCents);

        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync("/api/recurring", new RecurringUpdateRequest("NOPE"), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/recurring", new RecurringUpdateRequest(electric.Key, DueDay: 40), Json)).StatusCode);
    }

    [Fact]
    public async Task Home_shows_the_next_bills_due()
    {
        var c = await HistoryAsync();
        var home = (await c.GetFromJsonAsync<HomeSummaryDto>("/api/home/summary", Json))!;
        // Today is Nov 6: City Electric was due Nov 12 (monthly from Oct 12); Streamco Nov 3 has passed.
        Assert.Equal([("City Electric", new DateOnly(2026, 11, 12))], home.UpcomingBills!.Select(b => (b.Name, b.Date)));
    }
}
