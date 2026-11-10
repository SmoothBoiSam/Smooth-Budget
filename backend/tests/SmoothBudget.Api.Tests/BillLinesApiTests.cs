using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Payday;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Tests;

/// <summary>
/// Bill lines that budget themselves (DECISIONS.md 158) and card interest as a bill (159). All data invented.
/// </summary>
public class BillLinesApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = PaydayService.Json;

    private record Setup(HttpClient Client, Guid HouseholdId, Guid Checking, Guid Card);

    /// <summary>Three past months of groceries and income, a checking account and a card, the automatic budget on.</summary>
    private async Task<Setup> NewHouseholdAsync()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2027, 2, 10, 19, 0, 0, TimeSpan.Zero)); // budget month Feb 8 - Mar 7
        var c = app.CreateClient();
        var me = (await (await c.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"))).Content.ReadFromJsonAsync<MeResponse>(Json))!;
        Guid checking = Guid.NewGuid(), card = Guid.NewGuid(), groceries = Guid.NewGuid();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Accounts.AddRange(
                new Account { Id = checking, HouseholdId = me.HouseholdId, Name = "Test Checking", Type = AccountType.Checking, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow },
                new Account { Id = card, HouseholdId = me.HouseholdId, Name = "Test Visa", Type = AccountType.CreditCard, Source = DataSource.Csv, CurrentBalanceCents = -2_000_00, CreatedUtc = DateTime.UtcNow });
            db.Categories.Add(new Category { Id = groceries, HouseholdId = me.HouseholdId, Name = "Groceries", Type = CategoryType.Variable, MonthlyLimitCents = 400_00 });
            foreach (var m in new[] { new DateOnly(2026, 11, 15), new DateOnly(2026, 12, 15), new DateOnly(2027, 1, 15) })
                db.Transactions.AddRange(T(me.HouseholdId, checking, m, groceries, 400_00, "TEST MARKET"), T(me.HouseholdId, checking, m, null, -3_200_00, "PAYROLL", TransactionKind.Income));
            await db.SaveChangesAsync();
        }
        await c.PutAsJsonAsync("/api/settings/budget", new BudgetSettingsDto(SweepTarget.Debt, 50, AutoBudget: true), Json);
        return new Setup(c, me.HouseholdId, checking, card);
    }

    private static Transaction T(Guid household, Guid account, DateOnly d, Guid? cat, long cents, string merchant, TransactionKind kind = TransactionKind.Purchase,
        CategorySource source = CategorySource.User) => new()
    {
        Id = Guid.NewGuid(), HouseholdId = household, AccountId = account, CategoryId = cat, Date = d, AmountCents = cents, Merchant = merchant,
        Kind = kind, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow, CategorySource = cat is null ? CategorySource.None : source,
    };

    private static async Task<BudgetSummaryDto> Summary(HttpClient c) => (await c.GetFromJsonAsync<BudgetSummaryDto>("/api/budget/summary", Json))!;

    [Fact]
    public async Task Card_interest_leaves_spending_for_its_own_bill_line_planned_from_the_balance()
    {
        var (c, household, _, card) = await NewHouseholdAsync();
        var fees = Guid.NewGuid();
        var interestIds = new List<Guid>();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Categories.Add(new Category { Id = fees, HouseholdId = household, Name = "Fees & interest", Type = CategoryType.Variable, MonthlyLimitCents = 40_00, SortOrder = 1 });
            db.DebtAccounts.Add(new DebtAccount
            {
                Id = Guid.NewGuid(), HouseholdId = household, AccountId = card, Name = "Test Visa", BalanceCents = 2_000_00, AprBps = 2499, MinimumPaymentCents = 60_00,
                StartingBalanceCents = 2_000_00, StartedOn = new DateOnly(2026, 11, 1), CreatedUtc = DateTime.UtcNow,
            });
            foreach (var d in new[] { new DateOnly(2026, 11, 25), new DateOnly(2026, 12, 25), new DateOnly(2027, 1, 25) })
            {
                var t = T(household, card, d, fees, 38_00, "PURCHASE INTEREST CHARGE", TransactionKind.Fee); // you'd confirmed it as a fee
                interestIds.Add(t.Id);
                db.Transactions.Add(t);
            }
            db.Transactions.Add(T(household, card, new DateOnly(2027, 1, 5), fees, 40_00, "LATE FEE", TransactionKind.Fee));
            await db.SaveChangesAsync();
        }

        var s = await Summary(c);
        var line = s.Groups.SelectMany(g => g.Categories).Single(x => x.Name == "Card interest");
        Assert.Equal("FixedBill", line.Type);
        // Still a bill line underneath, but listed with the debt payments (DECISIONS.md 196).
        Assert.Contains(s.Groups.Single(g => g.Type == "DebtPayment").Categories, x => x.Name == "Card interest");
        Assert.DoesNotContain(s.Groups.Where(g => g.Type == "FixedBill").SelectMany(g => g.Categories), x => x.Name == "Card interest");
        Assert.Equal(41_65, line.LimitCents); // $2,000 at 24.99% ÷ 12, not what interest used to be
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var moved = await db.Transactions.Where(t => interestIds.Contains(t.Id)).Select(t => t.CategoryId).Distinct().ToListAsync();
            Assert.Equal([line.Id], moved);
            Assert.Equal(fees, await db.Transactions.Where(t => t.HouseholdId == household && t.Merchant == "LATE FEE").Select(t => t.CategoryId).SingleAsync());
        }
    }

    [Fact]
    public async Task A_new_bill_category_from_review_budgets_itself_and_gathers_the_bill_s_earlier_charges()
    {
        var (c, household, checking, _) = await NewHouseholdAsync();
        var charges = new[] { new DateOnly(2026, 10, 20), new DateOnly(2026, 11, 20), new DateOnly(2026, 12, 20), new DateOnly(2027, 1, 20) }
            .Select(d => T(household, checking, d, null, 15_49, "STREAMFLIX")).ToList();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Transactions.AddRange(charges);
            await db.SaveChangesAsync();
        }

        // Review's "new bill category" sends no amount.
        var created = await (await c.PostAsJsonAsync("/api/budget/categories", new CategoryRequest("Streamflix", CategoryType.FixedBill, 0), Json))
            .Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = created.GetProperty("id").GetGuid();
        var r = await (await c.PatchAsJsonAsync($"/api/transactions/{charges[^1].Id}", new UpdateTransactionRequest(id, false, null), Json))
            .Content.ReadFromJsonAsync<UpdateTransactionResult>(Json);
        Assert.Equal(3, r!.BillChargesMoved);

        var s = await Summary(c);
        Assert.Equal(15_49, s.Groups.SelectMany(g => g.Categories).Single(x => x.Id == id).LimitCents);
        using var check = app.Services.CreateScope();
        var cat = await check.ServiceProvider.GetRequiredService<AppDbContext>().Categories.SingleAsync(x => x.Id == id);
        Assert.False(cat.LimitSetByUser);
    }

    [Fact]
    public async Task Review_files_a_charge_as_a_new_bill_or_one_you_already_have()
    {
        // DECISIONS.md 225: a repeating charge the app found is offered as a new bill, named after it; saying so gives it
        // its own line with its earlier charges. A second charge at a new price then joins that bill instead.
        var (c, household, checking, _) = await NewHouseholdAsync();
        var charges = new[] { new DateOnly(2026, 10, 20), new DateOnly(2026, 11, 20), new DateOnly(2026, 12, 20), new DateOnly(2027, 1, 20) }
            .Select(d => T(household, checking, d, null, 15_49, "STREAMFLIX")).ToList();
        var newPrice = T(household, checking, new DateOnly(2027, 2, 9), null, 17_99, "STREAMFLIX PREMIUM");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Transactions.AddRange([.. charges, newPrice]);
            await db.SaveChangesAsync();
        }

        var choices = (await c.GetFromJsonAsync<Bills.ChargeBillChoicesDto>($"/api/recurring/for/{charges[^1].Id}", Json))!;
        Assert.NotNull(choices.Detected);
        Assert.Null(choices.Current);
        var res = await c.PostAsJsonAsync("/api/recurring/file-charge", new Bills.FileChargeAsBillRequest(charges[^1].Id, Name: "Streaming"), Json);
        var filed = (await res.Content.ReadFromJsonAsync<Bills.FiledAsBillDto>(Json))!;
        Assert.Equal(("Streaming", 4), (filed.CategoryName, filed.Moved)); // its 3 earlier charges, and the new price by its merchant rule

        var again = (await c.GetFromJsonAsync<Bills.ChargeBillChoicesDto>($"/api/recurring/for/{newPrice.Id}", Json))!;
        var bill = again.Bills.Single(b => b.Name == "Streaming");
        var joined = (await (await c.PostAsJsonAsync("/api/recurring/file-charge", new Bills.FileChargeAsBillRequest(newPrice.Id, BillKey: bill.Key), Json))
            .Content.ReadFromJsonAsync<Bills.FiledAsBillDto>(Json))!;
        Assert.Equal(filed.CategoryId, joined.CategoryId);
        using var check = app.Services.CreateScope();
        var rows = await check.ServiceProvider.GetRequiredService<AppDbContext>().Transactions.Where(t => t.HouseholdId == household && t.Merchant.StartsWith("STREAMFLIX")).ToListAsync();
        Assert.All(rows, t => Assert.Equal((filed.CategoryId, false), (t.CategoryId!.Value, t.NeedsReview)));

        // Neither a bill nor a name: turned away.
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/recurring/file-charge", new Bills.FileChargeAsBillRequest(newPrice.Id), Json)).StatusCode);
    }

    [Fact]
    public async Task Debt_and_savings_money_goes_toward_an_account()
    {
        // DECISIONS.md 227: you pick the debt or savings, and it's filed under the line named after it, or your first.
        var (c, household, checking, _) = await NewHouseholdAsync();
        Guid savingsAcct = Guid.NewGuid(), debtLine = Guid.NewGuid(), fundLine = Guid.NewGuid();
        var payment = T(household, checking, new DateOnly(2027, 2, 9), null, 200_00, "ONLINE PAYMENT");
        var moved = T(household, checking, new DateOnly(2027, 2, 9), null, 100_00, "TRANSFER OUT");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Accounts.Add(new Account { Id = savingsAcct, HouseholdId = household, Name = "Test Savings", Mask = "9999", Type = AccountType.Savings, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow });
            db.SavingsGoals.Add(new SavingsGoal { Id = Guid.NewGuid(), HouseholdId = household, Name = "Test fund", TargetCents = 1_000_00, AccountId = savingsAcct, CreatedUtc = DateTime.UtcNow });
            db.Categories.AddRange(
                new Category { Id = debtLine, HouseholdId = household, Name = "Card payments", Type = CategoryType.DebtPayment, SortOrder = 5 },
                new Category { Id = Guid.NewGuid(), HouseholdId = household, Name = "Savings", Type = CategoryType.SavingsTransfer, SortOrder = 6 },
                new Category { Id = fundLine, HouseholdId = household, Name = "Test fund", Type = CategoryType.SavingsTransfer, SortOrder = 7 });
            db.Transactions.AddRange(payment, moved);
            await db.SaveChangesAsync();
        }
        await c.GetAsync("/api/debt"); // the card becomes a debt
        var choices = (await c.GetFromJsonAsync<TowardChoicesDto>("/api/transactions/toward", Json))!;
        var visa = choices.Debts.Single(d => d.Name == "Test Visa");
        var fund = choices.Savings.Single(); // your goals, not the account they're in (DECISIONS.md 228)
        Assert.Equal("Test fund", fund.Name);

        var r = (await (await c.PostAsJsonAsync($"/api/transactions/{payment.Id}/toward", new TowardRequest("debt", visa.Id), Json)).Content.ReadFromJsonAsync<TowardResultDto>(Json))!;
        Assert.Equal(("Test Visa", "Card payments"), (r.TowardName, r.CategoryName));
        r = (await (await c.PostAsJsonAsync($"/api/transactions/{moved.Id}/toward", new TowardRequest("savings", fund.Id), Json)).Content.ReadFromJsonAsync<TowardResultDto>(Json))!;
        Assert.Equal(("Test fund", "Test fund"), (r.TowardName, r.CategoryName)); // the goal names its line
        using var check = app.Services.CreateScope();
        var rows = await check.ServiceProvider.GetRequiredService<AppDbContext>().Transactions.Where(t => t.Id == payment.Id || t.Id == moved.Id).ToListAsync();
        Assert.Equal((debtLine, "Toward Test Visa: you said so."), (rows.Single(t => t.Id == payment.Id).CategoryId!.Value, rows.Single(t => t.Id == payment.Id).CategoryReason));
        Assert.Equal(fundLine, rows.Single(t => t.Id == moved.Id).CategoryId);
    }

    [Fact]
    public async Task A_bill_you_filed_one_charge_of_gathers_the_rest_on_the_next_refresh()
    {
        var (c, household, checking, _) = await NewHouseholdAsync();
        var bill = Guid.NewGuid();
        var charges = new[] { new DateOnly(2026, 10, 3), new DateOnly(2026, 11, 3), new DateOnly(2026, 12, 3), new DateOnly(2027, 1, 3) }
            .Select((d, i) => T(household, checking, d, i == 3 ? bill : null, 62_00, "CITY WATER UTIL")).ToList();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Filed before this change: the bill line was "yours" at $0.
            db.Categories.Add(new Category { Id = bill, HouseholdId = household, Name = "Water", Type = CategoryType.FixedBill, LimitSetByUser = false, SortOrder = 2 });
            db.Transactions.AddRange(charges);
            await db.SaveChangesAsync();
        }

        var s = await Summary(c);
        Assert.Equal(62_00, s.Groups.SelectMany(g => g.Categories).Single(x => x.Id == bill).LimitCents);
        using var check = app.Services.CreateScope();
        var ids = charges.Select(x => x.Id).ToList();
        Assert.Equal([bill], await check.ServiceProvider.GetRequiredService<AppDbContext>().Transactions.Where(t => ids.Contains(t.Id)).Select(t => t.CategoryId).Distinct().ToListAsync());
    }
}
