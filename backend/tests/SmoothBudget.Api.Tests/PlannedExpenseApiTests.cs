using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Home;
using SmoothBudget.Api.Payday;
using SmoothBudget.Core.Tests;

namespace SmoothBudget.Api.Tests;

/// <summary>DECISIONS.md 327: setting money aside in checking for a big expense. Every amount invented.</summary>
public class PlannedExpenseApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = PaydayService.Json;

    [Fact]
    public async Task Checks_before_a_planned_expense_keep_their_parts_and_the_month_its_due_counts_them_in()
    {
        // Feb 24, 2027: living on the Feb 22 check; the next ones are Mar 8 and Mar 22.
        app.Clock.SetUtcNow(new DateTimeOffset(2027, 2, 24, 19, 0, 0, TimeSpan.Zero));
        var c = app.CreateClient();
        await c.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        using (var form = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(PayFixtures.Text));
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            form.Add(file, "file", "schedule.txt");
            form.Add(new StringContent("true"), "confirm");
            (await c.PostAsync("/api/payday/schedule", form)).EnsureSuccessStatusCode();
        }
        var me = (await c.GetFromJsonAsync<MeResponse>("/api/auth/me", Json))!;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(), HouseholdId = me.HouseholdId, Name = "Test Checking", Mask = "1111", Type = AccountType.Checking, Source = DataSource.Plaid,
                PlaidAccountId = "test-chk-planned", CurrentBalanceCents = 2_000_00, CreatedUtc = DateTime.UtcNow,
            });
            db.Categories.Add(new Category { Id = Guid.NewGuid(), HouseholdId = me.HouseholdId, Name = "Groceries", Type = Core.Budgeting.CategoryType.Variable, MonthlyLimitCents = 400_00 });
            await db.SaveChangesAsync();
        }

        // Bad input: a day gone by, a name a category already has, nothing to set aside.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/planned", new PlannedExpenseRequest("Test repair", 900_00, new DateOnly(2027, 2, 1)), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/planned", new PlannedExpenseRequest("groceries", 900_00, new DateOnly(2027, 3, 25)), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/planned", new PlannedExpenseRequest("Test repair", 0, new DateOnly(2027, 3, 25)), Json)).StatusCode);

        var added = await c.PostAsJsonAsync("/api/planned", new PlannedExpenseRequest("Test repair", 900_00, new DateOnly(2027, 3, 25)), Json);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var planned = Assert.Single((await c.GetFromJsonAsync<List<PlannedExpenseDto>>("/api/planned", Json))!);
        // Three checks before Mar 25 (Feb 22, Mar 8 and one around Mar 22): $300 each.
        Assert.Equal([300_00L, 300_00L, 300_00L], planned.Checks.Select(x => x.Cents).ToList());
        Assert.Equal((new DateOnly(2027, 2, 22), new DateOnly(2027, 3, 8)), (planned.Checks[0].PayDate, planned.Checks[1].PayDate));
        Assert.Equal((300_00L, 0L, false), (planned.SetAsideCents, planned.SpentCents, planned.Paid));

        // Each check up to the due date keeps its part in checking, on top of what the ones before it kept.
        var p = (await c.GetFromJsonAsync<PaydayDto>("/api/payday", Json))!;
        var key = $"setaside:{planned.Id}";
        var now = p.Checks[0].Plan!.Items.Single(i => i.Key == key);
        Assert.Equal(("hold", "Test repair, due Mar 25", 300_00L, "Test Checking ••1111"), (now.Kind, now.Label, now.Cents, now.Destination));
        Assert.StartsWith("$300.00 from this check.", now.Why);
        Assert.True(now.Done);
        Assert.Equal(600_00, p.Checks[1].Plan!.Items.Single(i => i.Key == key).Cents);
        Assert.Contains("on top of $300.00 set aside by earlier checks", p.Checks[1].Plan!.Items.Single(i => i.Key == key).Why);
        // Checking can spare its balance less what it keeps for the expense.
        Assert.True(p.Checks[0].Plan!.SpareCents <= 1_700_00);

        // This month's budget sets aside February's part on the expense's own line.
        await c.GetFromJsonAsync<HomeSummaryDto>("/api/home/summary", Json);
        Guid categoryId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var line = await db.Categories.SingleAsync(x => x.HouseholdId == me.HouseholdId && x.Name == "Test repair");
            categoryId = line.Id;
            Assert.Equal((Core.Budgeting.CategoryType.FixedBill, 300_00L, true), (line.Type, line.MonthlyLimitCents, line.LimitSetByUser));
            Assert.Equal(0L, (await db.BudgetMonths.Where(m => m.HouseholdId == me.HouseholdId).OrderByDescending(m => m.StartDate).FirstAsync()).SetAsideInCents);
        }

        // March, the month it's due: its line is the whole cost, and February's part counts as money in.
        app.Clock.SetUtcNow(new DateTimeOffset(2027, 3, 10, 19, 0, 0, TimeSpan.Zero));
        await c.GetFromJsonAsync<HomeSummaryDto>("/api/home/summary", Json);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(900_00L, (await db.Categories.SingleAsync(x => x.Id == categoryId)).MonthlyLimitCents);
            Assert.Equal(300_00L, (await db.BudgetMonths.Where(m => m.HouseholdId == me.HouseholdId).OrderByDescending(m => m.StartDate).FirstAsync()).SetAsideInCents);
        }
        var summary = (await c.GetFromJsonAsync<BudgetSummaryDto>("/api/budget/summary", Json))!;
        Assert.Equal(300_00L, summary.SetAsideInCents);

        // Paid: filed under its line, it's done, and the checks stop keeping money for it.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var checking = await db.Accounts.SingleAsync(a => a.HouseholdId == me.HouseholdId && a.Type == AccountType.Checking);
            db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(), HouseholdId = me.HouseholdId, AccountId = checking.Id, Date = new DateOnly(2027, 3, 10), AmountCents = 900_00, Merchant = "TEST GARAGE",
                Description = "TEST", Kind = Core.Transactions.TransactionKind.Purchase, CategoryId = categoryId, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        planned = Assert.Single((await c.GetFromJsonAsync<List<PlannedExpenseDto>>("/api/planned", Json))!);
        Assert.Equal((900_00L, true), (planned.SpentCents, planned.Paid));
        Assert.DoesNotContain((await c.GetFromJsonAsync<PaydayDto>("/api/payday", Json))!.Checks.SelectMany(x => x.Plan?.Items ?? []), i => i.Key == key);

        // Removed: its line stays, as an ordinary bill line, since something's filed under it.
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/planned/{planned.Id}")).StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<List<PlannedExpenseDto>>("/api/planned", Json))!);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var line = await db.Categories.SingleAsync(x => x.Id == categoryId);
            Assert.Equal((false, false), (line.IsHidden, line.LimitSetByUser));
        }
    }
}
