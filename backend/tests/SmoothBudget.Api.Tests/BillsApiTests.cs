using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
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
        // Each charge, newest first, so you can see when the price changed (DECISIONS.md 167).
        Assert.Equal(6, stream.Charges.Count);
        Assert.Equal((new DateOnly(2026, 10, 3), 17_99L), (stream.Charges[0].Date, stream.Charges[0].AmountCents));
        Assert.Equal((new DateOnly(2026, 5, 3), 15_49L), (stream.Charges[^1].Date, stream.Charges[^1].AmountCents));
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

    [Fact]
    public async Task A_bill_already_in_a_category_can_be_moved_to_another()
    {
        var c = await HistoryAsync();
        var stream = (await Bills(c))!.Items.Single(i => i.Name == "Streamco");
        Assert.Equal("Entertainment", stream.CategoryName);
        var subs = (await c.GetFromJsonAsync<List<CategoryDto>>("/api/budget/categories", Json))!.Single(x => x.Name == "Subscriptions").Id;

        var res = await c.PostAsJsonAsync("/api/recurring/category", new MoveBillRequest(stream.Key, subs), Json);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());

        var after = (await Bills(c))!.Items.Single(i => i.Key == stream.Key);
        Assert.Equal(("Subscriptions", true), (after.CategoryName, after.InBillCategory));
        var txns = (await c.GetFromJsonAsync<TransactionListDto>("/api/transactions?from=2026-01-01&to=2026-12-31", Json))!.Items;
        Assert.All(txns.Where(t => t.Merchant == "STREAMCO.COM"), t => Assert.Equal(("Subscriptions", "User"), (t.CategoryName, t.CategorySource)));

        // Future charges follow, and an unknown category or bill is turned away.
        var card = (await c.GetFromJsonAsync<List<AccountDto>>("/api/accounts", Json))!.Single(a => a.Mask == "4321").Id;
        await ImportAsync(c, card, ChaseCard, ["11/03/2026,11/04/2026,STREAMCO.COM,Entertainment,Sale,-17.99,"]);
        txns = (await c.GetFromJsonAsync<TransactionListDto>("/api/transactions?from=2026-01-01&to=2026-12-31", Json))!.Items;
        Assert.Equal("Subscriptions", txns.Single(t => t.Date == new DateOnly(2026, 11, 3)).CategoryName);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/recurring/category", new MoveBillRequest(stream.Key, Guid.NewGuid()), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/recurring/category", new MoveBillRequest("NOPE", subs), Json)).StatusCode);
    }

    [Fact]
    public async Task A_phone_hears_about_a_bill_due_tomorrow_once_and_never_at_night()
    {
        var c = await HistoryAsync();
        var household = (await c.GetFromJsonAsync<MeResponse>("/api/auth/me", Json))!.HouseholdId;
        var endpoint = $"https://fcm.googleapis.com/fcm/send/test-{Guid.NewGuid():N}";
        var status = await c.GetFromJsonAsync<SmoothBudget.Api.Notifications.PushStatusDto>("/api/push", Json);
        Assert.True(status!.Enabled);

        // Only a browser's own push service is accepted: the server posts to this address.
        foreach (var bad in new[] { "https://evil.example/push", "http://fcm.googleapis.com/x", "https://fcm.googleapis.com.evil.example/x" })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/push/subscribe", new { endpoint = bad, keys = new { p256dh = "k", auth = "a" } }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/push/subscribe", new { endpoint, keys = new { p256dh = "k", auth = "a" } }, Json)).StatusCode);

        async Task<List<SmoothBudget.Api.Notifications.PushMessage>> CheckAt(DateTimeOffset utc)
        {
            app.Clock.SetUtcNow(utc);
            var before = app.Push.Sent.Count(s => s.Endpoint == endpoint);
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SmoothBudget.Api.Notifications.NotificationService>().CheckAsync(household);
            return app.Push.Sent.Where(s => s.Endpoint == endpoint).Skip(before).Select(s => s.Message).ToList();
        }

        // City Electric is paid on the 12th. At 10 p.m. Denver time on the 11th: nothing; the next morning would be too late,
        // so the check at 10 a.m. on the 11th is the one that tells you, and it says so only once.
        Assert.Empty(await CheckAt(new DateTimeOffset(2026, 11, 12, 5, 0, 0, TimeSpan.Zero)));
        var morning = await CheckAt(new DateTimeOffset(2026, 11, 11, 17, 0, 0, TimeSpan.Zero));
        var bill = Assert.Single(morning, m => m.Tag == "bills");
        Assert.Equal("Bill due tomorrow", bill.Title);
        Assert.StartsWith("City Electric $", bill.Body);
        Assert.DoesNotContain(await CheckAt(new DateTimeOffset(2026, 11, 11, 20, 0, 0, TimeSpan.Zero)), m => m.Tag == "bills");

        // A phone that was wiped stops being sent to.
        app.Push.Gone[endpoint] = true;
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/push/test", null)).StatusCode);
        using (var scope = app.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().PushDevices.AnyAsync(d => d.Endpoint == endpoint));
    }

    [Fact]
    public async Task Each_kind_of_notification_has_its_own_switch_and_a_bank_that_needs_you_says_so()
    {
        var c = await HistoryAsync();
        var household = (await c.GetFromJsonAsync<MeResponse>("/api/auth/me", Json))!.HouseholdId;
        var endpoint = $"https://fcm.googleapis.com/fcm/send/test-{Guid.NewGuid():N}";
        await c.PostAsJsonAsync("/api/push/subscribe", new { endpoint, keys = new { p256dh = "k", auth = "a" } }, Json);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PlaidItems.Add(new PlaidItem
            {
                Id = Guid.NewGuid(), HouseholdId = household, PlaidItemId = $"item-{Guid.NewGuid():N}", AccessTokenEncrypted = "not-a-real-token",
                InstitutionName = "Fake Bank", Status = PlaidItemStatus.LoginRequired, CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        async Task<List<SmoothBudget.Api.Notifications.PushMessage>> CheckAt(DateTimeOffset utc)
        {
            app.Clock.SetUtcNow(utc);
            var before = app.Push.Sent.Count(s => s.Endpoint == endpoint);
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SmoothBudget.Api.Notifications.NotificationService>().CheckAsync(household);
            return app.Push.Sent.Where(s => s.Endpoint == endpoint).Skip(before).Select(s => s.Message).ToList();
        }

        // Every kind starts on.
        var types = (await c.GetFromJsonAsync<List<SmoothBudget.Api.Notifications.NotificationTypeDto>>("/api/push/types", Json))!;
        Assert.Equal(["payday", "bills", "over", "low", "cash", "price", "partner", "bank", "review"], types.Select(t => t.Id));
        Assert.All(types, t => Assert.True(t.On));

        // Bills turned off: the morning before City Electric is due says nothing about it, but the bank still gets through.
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync("/api/push/types/bills", new SmoothBudget.Api.Notifications.NotificationTypeRequest(false), Json)).StatusCode);
        Assert.False((await c.GetFromJsonAsync<List<SmoothBudget.Api.Notifications.NotificationTypeDto>>("/api/push/types", Json))!.Single(t => t.Id == "bills").On);
        var morning = await CheckAt(new DateTimeOffset(2026, 11, 11, 17, 0, 0, TimeSpan.Zero));
        Assert.DoesNotContain(morning, m => m.Tag == "bills");
        var bank = Assert.Single(morning, m => m.Tag == "bank");
        Assert.Equal("Fake Bank needs you", bank.Title);
        // Once a week while it's broken, not every half hour.
        Assert.DoesNotContain(await CheckAt(new DateTimeOffset(2026, 11, 11, 18, 0, 0, TimeSpan.Zero)), m => m.Tag == "bank");

        // Back on: what's still true (the bill is still due tomorrow) arrives.
        await c.PutAsJsonAsync("/api/push/types/bills", new SmoothBudget.Api.Notifications.NotificationTypeRequest(true), Json);
        Assert.Contains(await CheckAt(new DateTimeOffset(2026, 11, 11, 19, 0, 0, TimeSpan.Zero)), m => m.Tag == "bills");
        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync("/api/push/types/nope", new SmoothBudget.Api.Notifications.NotificationTypeRequest(false), Json)).StatusCode);
    }
}

public class PushKeyTests
{
    [Theory]
    [InlineData("  {0}\n")]
    [InlineData("\"{0}\"")]
    [InlineData("VAPID_PUBLIC_KEY={0}")]
    [InlineData("{0}=")]
    public void A_pasted_key_is_cleaned_up(string pasted)
    {
        var o = new SmoothBudget.Api.Notifications.PushOptions { PublicKey = string.Format(pasted, TestPushKeys.Public), PrivateKey = TestPushKeys.Private };
        Assert.Equal(TestPushKeys.Public, o.PublicKey);
        Assert.True(o.IsConfigured);
    }

    [Fact]
    public void A_wrong_key_says_so_instead_of_failing_on_the_phone()
    {
        var o = new SmoothBudget.Api.Notifications.PushOptions { PublicKey = TestPushKeys.Private, PrivateKey = TestPushKeys.Private };
        Assert.False(o.IsConfigured);
        Assert.Contains("VAPID_PUBLIC_KEY", o.Problem);
        Assert.Equal("Notifications aren't set up on the server yet.", new SmoothBudget.Api.Notifications.PushOptions().Problem);
    }
}
