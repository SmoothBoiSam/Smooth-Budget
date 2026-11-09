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
