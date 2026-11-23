using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Home;
using SmoothBudget.Api.Import;
using SmoothBudget.Api.Payday;
using SmoothBudget.Core.Pay;
using SmoothBudget.Core.Tests;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Tests;

/// <summary>
/// Milestone 9: schedule import, pay stub import and learning, the forecast and payday plan, and predicted vs actual.
/// Every schedule, stub and amount is invented (PayFixtures).
/// </summary>
public class PaydayApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = PaydayService.Json;

    private async Task<HttpClient> NewUserAsync(DateTimeOffset now)
    {
        app.Clock.SetUtcNow(now);
        var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        return client;
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient c, string url, byte[] bytes, string fileName, string type, bool? confirm = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", fileName);
        if (confirm is not null) form.Add(new StringContent(confirm.Value ? "true" : "false"), "confirm");
        return await c.PostAsync(url, form);
    }

    private static Task<HttpResponseMessage> Schedule(HttpClient c, string text, string name, bool confirm) =>
        Upload(c, "/api/payday/schedule", Encoding.UTF8.GetBytes(text), name, "text/plain", confirm);

    private static async Task<PaydayDto> Payday(HttpClient c) => (await c.GetFromJsonAsync<PaydayDto>("/api/payday", Json))!;

    [Fact]
    public async Task A_schedule_is_previewed_then_saved_and_each_check_is_forecast_by_day()
    {
        var c = await NewUserAsync(new DateTimeOffset(2027, 1, 25, 18, 0, 0, TimeSpan.Zero));

        // Preview: the grouped trips to confirm, nothing saved yet.
        var preview = await Schedule(c, PayFixtures.Text, "schedule.txt", confirm: false);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var month = (await preview.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("month").Deserialize<ScheduleMonthDto>(Json)!;
        Assert.Equal((2027, 2, "text", 2, 1834, 80 * 60 - 1834), (month.Year, month.Month, month.Source, month.Trips.Count, month.CreditMinutes, month.ShortfallMinutes));
        Assert.Equal((1078, 4311, 10), (month.Trips[0].CreditMinutes, month.Trips[0].TafbMinutes, month.Trips[0].RevenueLegs));
        Assert.Equal(Hm.Pay(1078, 30_32) + 75_80 + 161_66, month.Trips[0].PayCents); // credit + boarding + per diem
        Assert.Empty(month.Warnings);
        Assert.Empty((await c.GetFromJsonAsync<List<ScheduleMonthDto>>("/api/payday/schedule", Json))!);

        Assert.Equal(HttpStatusCode.OK, (await Schedule(c, PayFixtures.Text, "schedule.txt", confirm: true)).StatusCode);
        var p = await Payday(c);
        Assert.Equal((new DateOnly(2027, 1, 22), true), (p.Checks[0].PayDate, p.Checks[0].IsCurrent)); // the check you're living on now
        Assert.Equal([new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 22), new DateOnly(2027, 3, 8)], p.Checks.Skip(1).Select(x => x.PayDate));
        Assert.False(p.Checks[1].HasSchedule); // January isn't imported
        var feb22 = p.Checks[2];
        Assert.Equal([("Regular", 799_44L), ("Boarding Pay", 83_38L), ("NonTax Per Diem", 239_85L)], feb22.Lines.Select(l => (l.Name, l.Cents)));
        Assert.True(feb22.LowConfidence); // no stubs yet: a flat 80% of gross
        Assert.Equal((long)Math.Round(feb22.GrossCents * 0.8m), feb22.NetCents);
        Assert.DoesNotContain(feb22.Plan!.AllParts(), i => i.Kind == "spending"); // Home's Left to Spend covers it (DECISIONS.md 154)

        // Overtime on the first trip adds the extra half on the check that covers it; a re-import keeps the choice.
        await c.PutAsJsonAsync("/api/payday/schedule/2027/2", new ScheduleChoicesRequest(false, ["T1001 02/04"], []), Json);
        Assert.Equal(feb22.GrossCents + 272_37, (await Payday(c)).Checks[2].GrossCents);
        await Schedule(c, PayFixtures.Text, "schedule.txt", confirm: true);
        Assert.True((await c.GetFromJsonAsync<List<ScheduleMonthDto>>("/api/payday/schedule", Json))![0].Trips[0].Overtime);

        // Dropping a trip leaves it on the list and out of pay; a re-import keeps that choice while the trip is still there.
        await c.PutAsJsonAsync("/api/payday/schedule/2027/2", new ScheduleChoicesRequest(false, [], [], ["T1001 02/04"]), Json);
        var dropped = (await c.GetFromJsonAsync<List<ScheduleMonthDto>>("/api/payday/schedule", Json))![0];
        Assert.Equal((true, 756), (dropped.Trips[0].Dropped, dropped.CreditMinutes));
        Assert.Equal(Hm.Pay(504, 30_32), (await Payday(c)).Checks[2].Lines.Single(l => l.Name == "Regular").Cents);
        await Schedule(c, PayFixtures.Text, "schedule.txt", confirm: true);
        Assert.True((await c.GetFromJsonAsync<List<ScheduleMonthDto>>("/api/payday/schedule", Json))![0].Trips[0].Dropped);
        await c.PutAsJsonAsync("/api/payday/schedule/2027/2", new ScheduleChoicesRequest(false, ["T1001 02/04"], []), Json);

        // The CSV export is the fallback; a file that's neither is refused.
        var csv = await Schedule(c, PayFixtures.Csv, "schedule.csv", confirm: false);
        Assert.Equal("csv", (await csv.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("month").GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Schedule(c, "hello", "notes.txt", confirm: false)).StatusCode);
    }

    [Fact]
    public async Task Stubs_are_reviewed_then_saved_the_model_is_learned_and_predicted_vs_actual_is_tracked()
    {
        var c = await NewUserAsync(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero));

        foreach (var stub in PayFixtures.Stubs)
        {
            var review = await Upload(c, "/api/payday/paystubs", FakeStubPdf.Build(stub), "stub.pdf", "application/pdf");
            var body = await review.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
            foreach (var personal in new[] { FakeStubPdf.Name, FakeStubPdf.Street, FakeStubPdf.Ssn, FakeStubPdf.EmployeeId, FakeStubPdf.BankAccount, "4321" })
                Assert.DoesNotContain(personal, body);
            var parsed = JsonSerializer.Deserialize<StubReviewDto>(body, Json)!;
            Assert.Empty(parsed.Problems);
            Assert.Equal((stub.PayDate, stub.PeriodStart, stub.PeriodEnd, stub.GrossCents, stub.FedTaxableCents, stub.NetCents, stub.RateCents),
                (parsed.Stub.PayDate, parsed.Stub.PeriodStart, parsed.Stub.PeriodEnd, parsed.Stub.GrossCents, parsed.Stub.FedTaxableCents, parsed.Stub.NetCents, parsed.Stub.RateCents));
            Assert.Equal(stub.Earnings.Select(e => (e.Name, e.CurrentCents)), parsed.Stub.Earnings.Select(e => (e.Name, e.CurrentCents)));
            Assert.Equal(stub.Taxes, parsed.Stub.Taxes);
            Assert.Equal((stub.UserHoursEarned, stub.VacationHoursEarned), (parsed.Stub.UserHoursEarned, parsed.Stub.VacationHoursEarned));
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/payday/paystubs/confirm", parsed.Stub, Json)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(c, "/api/payday/paystubs", [1, 2, 3], "stub.jpg", "image/jpeg")).StatusCode);

        var model = (await c.GetFromJsonAsync<ModelDto>("/api/payday/model", Json))!;
        Assert.Equal(5, model.StubCount);
        Assert.Equal(["Commuter Subsid", "NonTax Per Diem", "Uni Allowance"], model.Model!.NonTaxable);
        Assert.All(model.BackTests, b => Assert.InRange(b.ErrorCents, -1, 1));

        // A schedule for November (the invented February trips moved to November 2026), then the forecast for Nov 23.
        var nov = PayFixtures.Csv.Replace("/2027", "/2026").Replace(",02/", ",11/");
        Assert.Equal(HttpStatusCode.OK, (await Schedule(c, nov, "schedule.csv", confirm: true)).StatusCode);
        var p = await Payday(c);
        var check = p.Checks.First(x => !x.IsCurrent);
        Assert.Equal((new DateOnly(2026, 11, 23), true), (check.PayDate, check.HasSchedule));
        Assert.Contains(check.Lines, l => l.Name == "Uni Allowance");
        Assert.DoesNotContain(check.Lines, l => l.Name == "Commuter Subsid"); // only on the check paid on the 7th
        Assert.Equal(check.GrossCents - check.Taxes.Sum(t => t.Cents) - check.Deductions.Sum(d => d.Cents), check.NetCents);
        Assert.True(check.LowNetCents <= check.NetCents && check.NetCents <= check.HighNetCents); // no history yet: no miss to widen it
        var home = (await c.GetFromJsonAsync<HomeSummaryDto>("/api/home/summary", Json))!;
        Assert.Equal((new DateOnly(2026, 11, 23), check.NetCents), (home.NextPayday!.PayDate, home.NextPayday.NetCents));

        // The paycheck lands in checking: predicted vs actual from the deposit.
        var checking = (await (await c.PostAsJsonAsync("/api/accounts", new { name = "Test Checking", type = "Checking", mask = "1111" }, Json)).Content.ReadFromJsonAsync<AccountDto>(Json))!.Id;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var acct = await db.Accounts.SingleAsync(a => a.Id == checking);
            db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(), HouseholdId = acct.HouseholdId, AccountId = checking, Date = new DateOnly(2026, 11, 23), AmountCents = -(check.NetCents + 12_34),
                Merchant = "TEST AIRLINE PAYROLL", Description = "TEST AIRLINE PAYROLL", Kind = TransactionKind.Income, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 25, 19, 0, 0, TimeSpan.Zero));
        var landed = (await Payday(c)).History.Single(h => h.PayDate == new DateOnly(2026, 11, 23));
        Assert.Equal((check.NetCents, check.NetCents + 12_34, 12_34L, "deposit"), (landed.PredictedNetCents, landed.ActualNetCents!.Value, landed.VarianceCents!.Value, landed.Source));

        // Its stub arrives: the stub wins, the variance is stored, and the model refits with six stubs.
        var sixth = PayFixtures.Stub(new DateOnly(2026, 11, 23), 26m, 11, 106m);
        var saved = await (await c.PostAsJsonAsync("/api/payday/paystubs/confirm", sixth, Json)).Content.ReadFromJsonAsync<StubSavedDto>(Json);
        Assert.Equal((check.NetCents, sixth.NetCents - check.NetCents), (saved!.PredictedNetCents!.Value, saved.VarianceCents!.Value));
        var after = await Payday(c);
        Assert.Equal(("stub", sixth.NetCents), (after.History.Single(h => h.PayDate == new DateOnly(2026, 11, 23)).Source, after.History[0].ActualNetCents!.Value));
        Assert.Equal(6, after.StubCount);

        // The plan's checklist remembers what you sent.
        var next = after.Checks[0];
        var put = await c.PutAsJsonAsync($"/api/payday/plan/{next.PayDate:yyyy-MM-dd}/savings", new ChecklistRequest(true), Json);
        Assert.True(put.IsSuccessStatusCode);
        using (var scope = app.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaydayChecklistItems.AnyAsync(x => x.PayDate == next.PayDate && x.Key == "savings"));

        // A later check's tasks open on its payday (DECISIONS.md 221): ticking one off early is refused, unticking isn't.
        var later = after.Checks.First(x => !x.IsCurrent);
        var early = await c.PutAsJsonAsync($"/api/payday/plan/{later.PayDate:yyyy-MM-dd}/savings", new ChecklistRequest(true), Json);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api/payday/plan/{later.PayDate:yyyy-MM-dd}/savings", new ChecklistRequest(false), Json)).StatusCode);
        using (var scope = app.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaydayChecklistItems.AnyAsync(x => x.PayDate == later.PayDate));
    }

    [Fact]
    public void Tasks_sending_to_the_same_account_merge_with_a_breakdown()
    {
        // DECISIONS.md 220: the card's payment and paying off what you charged to it become one task; "keep" never merges.
        PlanItemDto Item(string key, string kind, long cents, string to, bool done = false, bool auto = false, string? status = null, bool skipped = false) =>
            new(key, kind, kind, cents, to, "why " + key, done, auto, status, Skippable: kind == "cardspend", Skipped: skipped);
        var items = new[]
        {
            Item("keep:Checking", "keep", 500_00, "Checking"),
            Item("debt", "debt", 200_00, "Visa ••1234", status: "Sent $50.00 of $200.00 so far."),
            Item("savings", "savings", 100_00, "Savings", done: true, auto: true),
            Item("cardspend:x", "cardspend", 70_00, "Visa ••1234", status: "Paid $50.00 of $270.00 to this card so far, counting its payment above."),
        };
        var merged = PaydayService.MergeSends(items);
        Assert.Equal(["keep:Checking", "send:debt+cardspend:x", "savings"], merged.Select(i => i.Key));
        var card = merged[1];
        Assert.Equal(("send", 270_00L, "Visa ••1234", false), (card.Kind, card.Cents, card.Destination, card.Done));
        Assert.Equal("Paid $50.00 of $270.00 to this card so far, counting its payment above.", card.Status);
        Assert.Equal(["debt", "cardspend:x"], card.Parts!.Select(p => p.Key));
        Assert.Equal(4, new PlanDto(merged, 0).AllParts().Count());

        // Skipping what you charged takes it off the amount; with the payment done too, the task is done.
        var after = PaydayService.MergeSends([Item("debt", "debt", 200_00, "Visa ••1234", done: true, auto: true), Item("cardspend:x", "cardspend", 70_00, "Visa ••1234", skipped: true)]);
        Assert.Equal((200_00L, true, true, false), (after[0].Cents, after[0].Done, after[0].Auto, after[0].Skipped));
    }

    [Fact]
    public async Task The_plan_for_the_check_you_are_on_fills_itself_in_from_your_accounts()
    {
        // Feb 24, 2027: living on the Feb 22 check (Feb 1-15, from the February schedule) until Mar 8.
        var c = await NewUserAsync(new DateTimeOffset(2027, 2, 24, 19, 0, 0, TimeSpan.Zero));
        await Schedule(c, PayFixtures.Text, "schedule.txt", confirm: true);
        var me = (await c.GetFromJsonAsync<MeResponse>("/api/auth/me", Json))!;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var h = me.HouseholdId;
            var checking = new Account { Id = Guid.NewGuid(), HouseholdId = h, Name = "Test Checking", Mask = "1111", Type = AccountType.Checking, Source = DataSource.Plaid, PlaidAccountId = "test-chk", CurrentBalanceCents = 2_000_00, CreatedUtc = DateTime.UtcNow };
            var savings = new Account { Id = Guid.NewGuid(), HouseholdId = h, Name = "Test Savings", Mask = "2222", Type = AccountType.Savings, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow };
            var power = new Category { Id = Guid.NewGuid(), HouseholdId = h, Name = "Utilities", Type = SmoothBudget.Core.Budgeting.CategoryType.FixedBill, MonthlyLimitCents = 90_00 };
            var cards = new Category { Id = Guid.NewGuid(), HouseholdId = h, Name = "Credit card payment", Type = SmoothBudget.Core.Budgeting.CategoryType.DebtPayment, MonthlyLimitCents = 400_00 };
            var save = new Category { Id = Guid.NewGuid(), HouseholdId = h, Name = "Savings", Type = SmoothBudget.Core.Budgeting.CategoryType.SavingsTransfer, MonthlyLimitCents = 100_00 };
            db.AddRange(checking, savings, power, cards, save);
            Transaction T(Account a, DateOnly d, long cents, TransactionKind kind, Guid? cat = null, bool transfer = false, string merchant = "TEST POWER CO") => new()
            {
                Id = Guid.NewGuid(), HouseholdId = h, AccountId = a.Id, Date = d, AmountCents = cents, Merchant = merchant, Description = "TEST",
                Kind = kind, CategoryId = cat, IsTransfer = transfer, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
            };
            foreach (var d in new[] { new DateOnly(2026, 11, 25), new DateOnly(2026, 12, 25), new DateOnly(2027, 1, 25) })
                db.Transactions.Add(T(checking, d, 90_00, TransactionKind.Purchase, power.Id)); // a monthly bill, next due Feb 25
            db.Transactions.Add(T(checking, new DateOnly(2027, 2, 23), 120_00, TransactionKind.CardPayment, cards.Id, transfer: true, merchant: "ONLINE CARD PAYMENT"));
            db.Transactions.Add(T(savings, new DateOnly(2027, 2, 23), -50_00, TransactionKind.Transfer, transfer: true, merchant: "TRANSFER FROM CHECKING"));
            await db.SaveChangesAsync();
        }

        var now = (await Payday(c)).Checks[0];
        Assert.Equal((new DateOnly(2027, 2, 22), true), (now.PayDate, now.IsCurrent));
        var items = now.Plan!.AllParts().ToDictionary(i => i.Kind);
        // One "keep" line for the bills paid from checking before Mar 8, done because the balance covers them.
        Assert.Equal(("1 bill due Feb 25", 90_00L, "Test Checking ••1111", true, true), (items["keep"].Label, items["keep"].Cents, items["keep"].Destination, items["keep"].Done, items["keep"].Auto));
        Assert.Equal("Test Checking has $2,000.00, enough for these.", items["keep"].Status);
        // Half the month's $400 card payment; $120 sent so far.
        Assert.Equal((200_00L, false, "Sent $120.00 of $200.00 so far."), (items["debt"].Cents, items["debt"].Done, items["debt"].Status));
        // Half the month's $100 savings: the $50 transfer covers it.
        Assert.Equal((50_00L, true, true), (items["savings"].Cents, items["savings"].Done, items["savings"].Auto));
        Assert.False(items.ContainsKey("spending"));

        // Two cards with minimums: each gets its own line. This check pays half of each minimum ($20 and $15), and the
        // rest of its $200 goes to the card with the higher APR (avalanche).
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DebtAccount Debt(string name, long balance, int apr, long min) => new()
            {
                Id = Guid.NewGuid(), HouseholdId = me.HouseholdId, Name = name, BalanceCents = balance, AprBps = apr, MinimumPaymentCents = min,
                StartingBalanceCents = balance, StartedOn = new DateOnly(2027, 1, 1), CreatedUtc = DateTime.UtcNow,
            };
            db.DebtAccounts.AddRange(Debt("Test Store Card", 500_00, 1999, 30_00), Debt("Test Visa", 2_000_00, 2499, 40_00));
            await db.SaveChangesAsync();
        }
        var debtLines = (await Payday(c)).Checks[0].Plan!.AllParts().Where(i => i.Kind == "debt").ToList();
        Assert.Equal([("Test Visa", 185_00L, "Minimum + extra"), ("Test Store Card", 15_00L, "Minimum payment")], debtLines.Select(i => (i.Destination, i.Cents, i.Label)));

        // Purchases on a credit card count in their categories, so the plan pays them off too or the card would grow
        // (DECISIONS.md 179). The check you're on (Feb 22) covers Feb 8-21; the next one shows what's charged since so far.
        var shortBefore = (await Payday(c)).Checks[0].Plan!.ShortCents;
        Guid cardId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var card = new Account { Id = Guid.NewGuid(), HouseholdId = me.HouseholdId, Name = "Sample Card", Mask = "3333", Type = AccountType.CreditCard, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow };
            cardId = card.Id;
            db.Accounts.Add(card);
            Transaction OnCard(DateOnly d, long cents, TransactionKind kind = TransactionKind.Purchase, bool transfer = false) => new()
            {
                Id = Guid.NewGuid(), HouseholdId = me.HouseholdId, AccountId = card.Id, Date = d, AmountCents = cents, Merchant = "TEST SHOP", Description = "TEST",
                Kind = kind, IsTransfer = transfer, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
            };
            db.Transactions.AddRange(
                OnCard(new DateOnly(2027, 2, 5), 99_00),   // before the last payday: an earlier check's
                OnCard(new DateOnly(2027, 2, 10), 40_00),
                OnCard(new DateOnly(2027, 2, 20), 35_00),
                OnCard(new DateOnly(2027, 2, 21), -5_00, TransactionKind.Refund),
                OnCard(new DateOnly(2027, 2, 23), 25_00)); // after payday: the next check's
            await db.SaveChangesAsync();
        }
        var withCard = await Payday(c);
        var spend = withCard.Checks[0].Plan!.AllParts().Single(i => i.Kind == "cardspend");
        Assert.Equal(("Pay off what you charged", 70_00L, "Sample Card ••3333", false), (spend.Label, spend.Cents, spend.Destination, spend.Done));
        Assert.Equal(shortBefore, withCard.Checks[0].Plan!.ShortCents); // it comes from spending money already in checking, not this check
        var soFar = withCard.Checks[1].Plan!.AllParts().Single(i => i.Kind == "cardspend");
        Assert.Equal(("Charged so far", 25_00L), (soFar.Label, soFar.Cents));

        // DECISIONS.md 195: paying off what you charged can be skipped on the check you're on (and only that line).
        Assert.Equal((true, false), (spend.Skippable, soFar.Skippable));
        var skipUrl = $"/api/payday/plan/{withCard.Checks[0].PayDate:yyyy-MM-dd}/{Uri.EscapeDataString(spend.Key)}";
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync(skipUrl, new { done = false, skipped = true }, Json)).StatusCode);
        var skipped = (await Payday(c)).Checks[0].Plan!.AllParts().Single(i => i.Kind == "cardspend");
        Assert.Equal((true, false), (skipped.Skipped, skipped.Done));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PutAsJsonAsync($"/api/payday/plan/{withCard.Checks[0].PayDate:yyyy-MM-dd}/savings", new { done = false, skipped = true }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync(skipUrl, new { done = false, skipped = false }, Json)).StatusCode);
        Assert.False((await Payday(c)).Checks[0].Plan!.AllParts().Single(i => i.Kind == "cardspend").Skipped);

        // The card's own account shows the payment: that line is done.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(), HouseholdId = me.HouseholdId, AccountId = cardId, Date = new DateOnly(2027, 2, 23), AmountCents = -70_00, Merchant = "PAYMENT THANK YOU",
                Description = "TEST", Kind = TransactionKind.CardPayment, IsTransfer = true, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        Assert.True((await Payday(c)).Checks[0].Plan!.AllParts().Single(i => i.Kind == "cardspend").Done);

        // The next checks keep their plans for reference, without statuses.
        Assert.All((await Payday(c)).Checks.Skip(1).Where(x => x.Plan is not null).SelectMany(x => x.Plan!.AllParts()), i => Assert.Null(i.Status));
    }

    [Fact]
    public async Task A_differently_printed_stub_still_reads_every_tax_line()
    {
        // Another print of the same pay advice places the tax amounts about 15 points right of their headings.
        var c = await NewUserAsync(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero));
        var stub = PayFixtures.Stubs[^1];
        var review = await Upload(c, "/api/payday/paystubs", FakeStubPdf.Build(stub, taxShift: 16), "stub.pdf", "application/pdf");
        var parsed = JsonSerializer.Deserialize<StubReviewDto>(await review.Content.ReadAsStringAsync(), Json)!;
        Assert.Empty(parsed.Problems);
        Assert.Equal(stub.Taxes, parsed.Stub.Taxes);
    }

    [Fact]
    public async Task Profile_rules_and_model_are_editable()
    {
        var c = await NewUserAsync(new DateTimeOffset(2027, 1, 25, 18, 0, 0, TimeSpan.Zero));
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync("/api/payday/profile", new ProfileRequest(10, true, true, 75), Json)).StatusCode);
        Assert.Equal(41_14, (await c.GetFromJsonAsync<ProfileDto>("/api/payday/profile", Json))!.RateCents);

        // A date of hire replaces years of service: today (Jan 25, 2027) that's 4 years, and the raise comes on the anniversary.
        await c.PutAsJsonAsync("/api/payday/profile", new ProfileRequest(0, true, true, 75, new DateOnly(2022, 3, 1)), Json);
        var hired = (await c.GetFromJsonAsync<ProfileDto>("/api/payday/profile", Json))!;
        Assert.Equal((4, 33_11L, new DateOnly(2027, 3, 1), 34_29L), (hired.YearsOfService, hired.RateCents, hired.NextRaiseOn!.Value, hired.NextRaiseRateCents!.Value));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/payday/profile", new ProfileRequest(0, true, true, 75, new DateOnly(2030, 1, 1)), Json)).StatusCode);
        await c.PutAsJsonAsync("/api/payday/profile", new ProfileRequest(10, true, true, 75), Json);

        var rules = PayRules.Default with { PerDiemCentsPerHour = 2_50 };
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsync("/api/payday/rules", JsonContent.Create(rules, options: Json))).StatusCode);
        await Schedule(c, PayFixtures.Text, "schedule.txt", confirm: true);
        var month = (await c.GetFromJsonAsync<List<ScheduleMonthDto>>("/api/payday/schedule", Json))![0];
        Assert.Equal(Hm.Pay(4311, 2_50), month.Trips[0].PerDiemCents);

        var bad = PayRules.Default with { Airports = [new("ZZZ", "Not/AZone")] };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsync("/api/payday/rules", JsonContent.Create(bad, options: Json))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/payday/rules")).StatusCode);

        foreach (var s in PayFixtures.Stubs.Take(3))
            await c.PostAsJsonAsync("/api/payday/paystubs/confirm", s, Json);
        var m = (await c.GetFromJsonAsync<ModelDto>("/api/payday/model", Json))!.Model!;
        var edited = m with { PtoHoursPerCheck = 0 };
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsync("/api/payday/model", JsonContent.Create(edited, options: Json))).StatusCode);
        var view = (await c.GetFromJsonAsync<ModelDto>("/api/payday/model", Json))!;
        Assert.True(view.Edited);
        Assert.Equal(0m, view.Model!.PtoHoursPerCheck);

        // A new stub refits the model (your edits are replaced by the recalibrated fit).
        await c.PostAsJsonAsync("/api/payday/paystubs/confirm", PayFixtures.Stubs[3], Json);
        Assert.False((await c.GetFromJsonAsync<ModelDto>("/api/payday/model", Json))!.Edited);
    }

    [Fact]
    public async Task A_paycheck_that_lands_early_is_the_check_youre_on_and_its_tasks_open()
    {
        // DECISIONS.md 270: Nov 22, the Nov 23 check is due tomorrow.
        var c = await NewUserAsync(new DateTimeOffset(2026, 11, 22, 19, 0, 0, TimeSpan.Zero));
        var nov = PayFixtures.Csv.Replace("/2027", "/2026").Replace(",02/", ",11/");
        Assert.Equal(HttpStatusCode.OK, (await Schedule(c, nov, "schedule.csv", confirm: true)).StatusCode);
        var check = (await Payday(c)).Checks.Single(x => x.PayDate == new DateOnly(2026, 11, 23));
        Assert.False(check.IsCurrent);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsJsonAsync("/api/payday/plan/2026-11-23/savings", new ChecklistRequest(true), Json)).StatusCode);

        // It lands a day early: it's the check you're on now, the one before it drops off, and its tasks open.
        var checking = (await (await c.PostAsJsonAsync("/api/accounts", new { name = "Test Checking", type = "Checking", mask = "1111" }, Json)).Content.ReadFromJsonAsync<AccountDto>(Json))!.Id;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var acct = await db.Accounts.SingleAsync(a => a.Id == checking);
            db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(), HouseholdId = acct.HouseholdId, AccountId = checking, Date = new DateOnly(2026, 11, 22), AmountCents = -check.NetCents,
                Merchant = "TEST AIRLINE PAYROLL", Description = "TEST AIRLINE PAYROLL", Kind = TransactionKind.Income, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var p = await Payday(c);
        Assert.Equal((new DateOnly(2026, 11, 23), true, (long?)check.NetCents), (p.Checks[0].PayDate, p.Checks[0].IsCurrent, p.Checks[0].LandedNetCents));
        Assert.Single(p.Checks, x => x.IsCurrent);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync("/api/payday/plan/2026-11-23/savings", new ChecklistRequest(true), Json)).StatusCode);
        using (var scope = app.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaydayChecklistItems.AnyAsync(x => x.PayDate == new DateOnly(2026, 11, 23) && x.Key == "savings"));
    }

    [Fact]
    public void Safe_to_spend_shows_only_when_checking_cant_cover_left_to_spend()
    {
        // DECISIONS.md 276. Nov 17, living on the Nov 6 check; the next is Nov 23, 6 days away. Left to Spend is $1,200.
        var today = new DateOnly(2026, 11, 17);
        var month = new SmoothBudget.Core.Budgeting.BudgetMonthRange(new DateOnly(2026, 11, 6), new DateOnly(2026, 12, 6), new DateOnly(2026, 11, 1));
        static CheckDto Check(DateOnly pay, bool current, long? spare, IReadOnlyList<ComingDto>? coming = null) => new(pay, pay.AddDays(-14), pay.AddDays(-1), true, 0, 0, 0, 0, 0, false, [], [], [], [], [],
            current ? new PlanDto([], 0, SpareCents: spare, Coming: coming) : null, IsCurrent: current);
        SmoothBudget.Core.Budgeting.LeftToSpendResult Month(long left, long perDay) =>
            new(month, today, left, 900_00, 0, 0, 0, 0, 20, perDay, SmoothBudget.Core.Budgeting.LeftToSpendStatus.Healthy, 0, []);
        SafeToSpendDto? Warn(long? spare, long left = 1_200_00, int nextIn = 6) =>
            HomeSummaryService.SafeToSpend([Check(new DateOnly(2026, 11, 6), true, spare), Check(today.AddDays(nextIn), false, null)], today, Month(left, 60_00));

        // Checking can spare $1,000 of the $1,200: Left to Spend counts money that isn't there yet, so it's capped.
        Assert.Equal((new DateOnly(2026, 11, 23), 6, 1_000_00L), Warn(1_000_00) is { } w ? (w.PayDate, w.Days, w.SafeCents) : default);
        Assert.Equal(0, Warn(-50_00)!.SafeCents); // already short: nothing is safe
        // Checking covers it: nothing to show.
        Assert.Null(Warn(5_000_00)); // plenty in checking
        Assert.Null(Warn(1_195_00)); // a few dollars under Left to Spend isn't worth a warning
        Assert.Null(Warn(null)); // checking not bank-synced: can't tell
        Assert.Null(Warn(200_00, left: 0)); // nothing left to spend anyway
        Assert.Equal(0, Warn(-50_00, left: 0)!.SafeCents); // but checking already short still warns (275)
        Assert.Equal(200_00, Warn(200_00, nextIn: 25)!.SafeCents); // payday after the month ends counts too (275)
        Assert.Null(Warn(1_500_00, nextIn: 25));

        // DECISIONS.md 277: it says what it's waiting for, the first money still to land before payday, or else payday.
        Assert.Equal("payday Nov 23", Warn(1_000_00)!.Until);
        string? Until(params ComingDto[] coming) => HomeSummaryService.SafeToSpend(
            [Check(new DateOnly(2026, 11, 6), true, 1_000_00, coming), Check(new DateOnly(2026, 11, 23), false, null)], today, Month(1_200_00, 60_00))?.Until;
        Assert.Equal("Bonus lands Nov 20", Until(new ComingDto("Bonus lands Nov 20", 500_00, new DateOnly(2026, 11, 20)), new ComingDto("Jane sends $800.00", 800_00, new DateOnly(2026, 11, 21))));
        Assert.Equal("payday Nov 23", Until(new ComingDto("Bonus lands Nov 25", 500_00, new DateOnly(2026, 11, 25))));
    }

    [Fact]
    public async Task Savings_a_bonus_still_to_come_pays_for_waits_for_it_in_what_checking_can_spare()
    {
        // DECISIONS.md 274. Feb 24, 2027: living on the Feb 22 check, with $400 a month of savings and $2,000 in checking.
        var c = await NewUserAsync(new DateTimeOffset(2027, 2, 24, 19, 0, 0, TimeSpan.Zero));
        await Schedule(c, PayFixtures.Text, "schedule.txt", confirm: true);
        var me = (await c.GetFromJsonAsync<MeResponse>("/api/auth/me", Json))!;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AddRange(
                new Account { Id = Guid.NewGuid(), HouseholdId = me.HouseholdId, Name = "Test Checking", Mask = "1111", Type = AccountType.Checking, Source = DataSource.Plaid, PlaidAccountId = "test-chk-bonus", CurrentBalanceCents = 2_000_00, CreatedUtc = DateTime.UtcNow },
                new Category { Id = Guid.NewGuid(), HouseholdId = me.HouseholdId, Name = "Savings", Type = SmoothBudget.Core.Budgeting.CategoryType.SavingsTransfer, MonthlyLimitCents = 400_00 });
            await db.SaveChangesAsync();
        }
        long Of(PaydayDto p, string kind) => p.Checks[0].Plan!.Items.Where(i => i.Kind == kind).Sum(i => i.Cents);

        // Without a bonus this check sends half the month's savings, and keeps what the Mar 8 check can't cover of its own
        // plan: checking can spare the rest.
        var p = await Payday(c);
        Assert.Equal((new DateOnly(2027, 2, 22), 200_00L), (p.Checks[0].PayDate, Of(p, "savings")));
        Assert.Equal(2_000_00L - 200_00 - Of(p, "hold"), p.Checks[0].Plan!.SpareCents);

        // A bonus expected Feb 26 pays for the month's savings (263), so this check's savings is all of it. It isn't in
        // checking yet, and neither does its savings have to come out before it lands: only the hold comes off the $2,000.
        (await c.PostAsJsonAsync("/api/payday/expected", new { name = "Test bonus", amountCents = 1_000_00, expectedDate = "2027-02-26" }, Json)).EnsureSuccessStatusCode();
        p = await Payday(c);
        Assert.Equal(400_00L, Of(p, "savings"));
        Assert.Equal(2_000_00L - Of(p, "hold"), p.Checks[0].Plan!.SpareCents);
    }

    [Fact]
    public async Task A_friends_part_of_a_bill_still_to_arrive_doesnt_count_as_checking_short_but_isnt_safe_to_spend()
    {
        // DECISIONS.md 275, 277. Feb 24, 2027, on the Feb 22 check: $1,650 rent is due Mar 1, before the Mar 8 payday, and a
        // friend sends $800 toward it that hasn't arrived yet.
        var c = await NewUserAsync(new DateTimeOffset(2027, 2, 24, 19, 0, 0, TimeSpan.Zero));
        await Schedule(c, PayFixtures.Text, "schedule.txt", confirm: true);
        var me = (await c.GetFromJsonAsync<MeResponse>("/api/auth/me", Json))!;
        var checkingId = Guid.NewGuid();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var h = me.HouseholdId;
            var rent = new Category { Id = Guid.NewGuid(), HouseholdId = h, Name = "Rent", Type = SmoothBudget.Core.Budgeting.CategoryType.FixedBill, MonthlyLimitCents = 1_650_00 };
            var friend = new Friend { Id = Guid.NewGuid(), HouseholdId = h, Name = "Test Friend", TransferName = "TEST FRIEND" };
            db.AddRange(rent, friend,
                new Account { Id = checkingId, HouseholdId = h, Name = "Test Checking", Mask = "1111", Type = AccountType.Checking, Source = DataSource.Plaid, PlaidAccountId = "test-chk-friend", CurrentBalanceCents = 1_000_00, CreatedUtc = DateTime.UtcNow },
                new PartnerArrangement { Id = Guid.NewGuid(), HouseholdId = h, FriendId = friend.Id, CategoryId = rent.Id, AmountCents = 800_00, StartsOn = new DateOnly(2026, 11, 6), CreatedUtc = DateTime.UtcNow });
            foreach (var d in new[] { new DateOnly(2026, 12, 1), new DateOnly(2027, 1, 1), new DateOnly(2027, 2, 1) })
                db.Transactions.Add(new Transaction
                {
                    Id = Guid.NewGuid(), HouseholdId = h, AccountId = checkingId, Date = d, AmountCents = 1_650_00, Merchant = "TEST LANDLORD", Description = "TEST",
                    Kind = TransactionKind.Purchase, CategoryId = rent.Id, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
                });
            await db.SaveChangesAsync();
        }
        async Task<PlanItemDto> Keep() => (await Payday(c)).Checks[0].Plan!.Items.Single(i => i.Kind == "keep");

        // $1,000 in checking covers the $850 that's yours: not short, though the $800 isn't in yet.
        var keep = await Keep();
        Assert.Equal((1_650_00L, 0L, true), (keep.Cents, keep.ShortCents, keep.Done));
        Assert.Equal("Test Checking has $1,000.00, enough for these with the $800.00 Test Friend sends still to come.", keep.Status);
        // But until it's in, it isn't safe to spend (DECISIONS.md 277): rent counts in full, and Home says what it waits for.
        var coming = Assert.Single((await Payday(c)).Checks[0].Plan!.Coming!);
        Assert.Equal(("Test Friend sends $800.00", 800_00L), (coming.Label, coming.Cents));
        var safe = (await c.GetFromJsonAsync<HomeSummaryDto>("/api/home/summary", Json))!.SafeToSpend!;
        Assert.Equal((0L, "Test Friend sends $800.00"), (safe.SafeCents, safe.Until));

        // With $500, it's short the $350 of your part.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Accounts.SingleAsync(a => a.Id == checkingId)).CurrentBalanceCents = 500_00;
            await db.SaveChangesAsync();
        }
        keep = await Keep();
        Assert.Equal((350_00L, false), (keep.ShortCents, keep.Done));
        Assert.Equal("Test Checking has $500.00: $350.00 short for these with the $800.00 Test Friend sends still to come.", keep.Status);
        // Home's one warning shows (DECISIONS.md 275): nothing is safe to spend until payday, even with nothing left to spend.
        Assert.Equal(0L, (await c.GetFromJsonAsync<HomeSummaryDto>("/api/home/summary", Json))!.SafeToSpend!.SafeCents);
    }

    [Fact]
    public async Task A_check_keeps_money_for_the_next_one_when_that_one_cannot_cover_its_bills()
    {
        // Feb 24, 2027: living on the Feb 22 check; the next is Mar 8, and rent is due Mar 10.
        var c = await NewUserAsync(new DateTimeOffset(2027, 2, 24, 19, 0, 0, TimeSpan.Zero));
        await Schedule(c, PayFixtures.Text, "schedule.txt", confirm: true);
        var me = (await c.GetFromJsonAsync<MeResponse>("/api/auth/me", Json))!;
        var nextNet = (await Payday(c)).Checks[1].NetCents;
        var rent = nextNet + 300_00; // $300 more than the Mar 8 check
        Guid checkingId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var h = me.HouseholdId;
            var checking = new Account { Id = Guid.NewGuid(), HouseholdId = h, Name = "Test Checking", Mask = "1111", Type = AccountType.Checking, Source = DataSource.Plaid, PlaidAccountId = "test-chk-hold", CurrentBalanceCents = 2_000_00, CreatedUtc = DateTime.UtcNow };
            checkingId = checking.Id;
            var home = new Category { Id = Guid.NewGuid(), HouseholdId = h, Name = "Rent", Type = SmoothBudget.Core.Budgeting.CategoryType.FixedBill, MonthlyLimitCents = rent };
            db.AddRange(checking, home);
            foreach (var d in new[] { new DateOnly(2026, 12, 10), new DateOnly(2027, 1, 10), new DateOnly(2027, 2, 10) })
                db.Transactions.Add(new Transaction
                {
                    Id = Guid.NewGuid(), HouseholdId = h, AccountId = checking.Id, Date = d, AmountCents = rent, Merchant = "TEST LANDLORD", Description = "TEST",
                    Kind = TransactionKind.Purchase, CategoryId = home.Id, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
                });
            await db.SaveChangesAsync();
        }
        async Task BalanceAsync(long cents)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Accounts.SingleAsync(a => a.Id == checkingId)).CurrentBalanceCents = cents;
            await db.SaveChangesAsync();
        }

        var p = await Payday(c);
        var (now, next) = (p.Checks[0], p.Checks[1]);
        Assert.Equal((new DateOnly(2027, 2, 22), new DateOnly(2027, 3, 8)), (now.PayDate, next.PayDate));
        Assert.True(now.NetCents > 300_00, "the test needs a Feb 22 check with room to keep $300");

        // DECISIONS.md 253: the Feb 22 check keeps the $300 the Mar 8 check lacks, and that check isn't short any more.
        var hold = now.Plan!.Items.Single(i => i.Kind == "hold");
        Assert.Equal(("bills after Mar 8", 300_00L, "Test Checking ••1111", "hold:2027-03-08"), (hold.Label, hold.Cents, hold.Destination, hold.Key));
        Assert.Contains("Leave $300.00 of this check in the account", hold.Why);
        Assert.Equal((true, true, "Test Checking has $2,000.00, enough for this."), (hold.Done, hold.Auto, hold.Status));
        Assert.Equal((0L, 300_00L, new DateOnly(2027, 2, 22)), (next.Plan!.ShortCents, next.Plan.HeldEarlierCents, next.Plan.HeldOn!.Value));
        Assert.DoesNotContain(next.Plan.Items, i => i.Kind == "hold" && i.Cents == 0);

        // DECISIONS.md 272: checking can spare its balance less the $300 it keeps for the Mar 8 check's bills (rent is
        // after Mar 8, so nothing is due before it), and less a charge still pending.
        Assert.Equal(1_700_00L, now.Plan.SpareCents);
        Assert.Null(next.Plan.SpareCents);
        var pendingId = Guid.NewGuid();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Transactions.Add(new Transaction
            {
                Id = pendingId, HouseholdId = me.HouseholdId, AccountId = checkingId, Date = new DateOnly(2027, 2, 24), AmountCents = 50_00, Merchant = "TEST SHOP",
                Description = "TEST", Kind = TransactionKind.Purchase, Pending = true, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(1_650_00L, (await Payday(c)).Checks[0].Plan!.SpareCents);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Transactions.Where(t => t.Id == pendingId).ExecuteDeleteAsync();
        }


        // DECISIONS.md 263: a bonus expected with the Mar 8 check counts toward it, so this check keeps only what's still
        // missing, and nothing once the bonus covers it all.
        var added = await c.PostAsJsonAsync("/api/payday/expected", new { name = "Test bonus", amountCents = 200_00, expectedDate = "2027-03-09" }, Json);
        added.EnsureSuccessStatusCode();
        hold = (await Payday(c)).Checks[0].Plan!.Items.Single(i => i.Kind == "hold");
        Assert.Equal(100_00, hold.Cents);
        Assert.Contains("plus Test bonus $200.00 expected Mar 9", hold.Why);
        await c.PostAsJsonAsync("/api/payday/expected", new { name = "Test bonus 2", amountCents = 500_00, expectedDate = "2027-03-12" }, Json);
        p = await Payday(c);
        Assert.DoesNotContain(p.Checks[0].Plan!.Items, i => i.Kind == "hold");
        Assert.Equal(0L, p.Checks[1].Plan!.ShortCents);
        foreach (var x in (await c.GetFromJsonAsync<List<SmoothBudget.Api.Payday.ExpectedIncomeDto>>("/api/payday/expected", Json))!)
            await c.DeleteAsync($"/api/payday/expected/{x.Id}");
        Assert.Equal(300_00, (await Payday(c)).Checks[0].Plan!.Items.Single(i => i.Kind == "hold").Cents);

        // Spent down: the task is open again, and says how far short the account is.
        await BalanceAsync(100_00);
        hold = (await Payday(c)).Checks[0].Plan!.Items.Single(i => i.Kind == "hold");
        Assert.Equal((false, "Test Checking has $100.00: $200.00 short of this."), (hold.Done, hold.Status));
        Assert.Equal(-200_00L, (await Payday(c)).Checks[0].Plan!.SpareCents);

        // On Mar 9 you're on the Mar 8 check. The money you kept is in the account, so it isn't short; with the account
        // empty, it's short what the check itself can't cover.
        app.Clock.SetUtcNow(new DateTimeOffset(2027, 3, 9, 19, 0, 0, TimeSpan.Zero));
        await BalanceAsync(rent + 5_00);
        var onIt = (await Payday(c)).Checks[0];
        Assert.Equal((new DateOnly(2027, 3, 8), true, 0L), (onIt.PayDate, onIt.IsCurrent, onIt.Plan!.ShortCents));
        await BalanceAsync(0);
        Assert.Equal(300_00, (await Payday(c)).Checks[0].Plan!.ShortCents);
    }
}
