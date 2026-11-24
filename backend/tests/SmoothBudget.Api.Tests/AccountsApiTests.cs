using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Accounts;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Payday;
using SmoothBudget.Api.Savings;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Security;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Tests;

/// <summary>
/// More → Accounts (DECISIONS.md 318): closing an account keeps what happened on it and moves what pointed at it; deleting
/// takes its transactions with it. Every name and amount is invented.
/// </summary>
public class AccountsApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = PaydayService.Json;

    private async Task<(HttpClient Client, Guid Household)> NewUserAsync()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2027, 2, 24, 19, 0, 0, TimeSpan.Zero));
        var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        var h = (await client.GetFromJsonAsync<MeResponse>("/api/auth/me", Json))!.HouseholdId;
        return (client, h);
    }

    private static Account New(Guid h, string name, AccountType type, long balance, int daysOld, int? percent = null) => new()
    {
        Id = Guid.NewGuid(), HouseholdId = h, Name = name, Mask = $"{Random.Shared.Next(1000, 9999)}", Type = type, Source = DataSource.Csv,
        CurrentBalanceCents = balance, BalanceAsOf = new DateOnly(2027, 2, 24), SavingsPercent = percent, CreatedUtc = DateTime.UtcNow.AddDays(-daysOld),
    };

    private static Transaction Spend(Guid h, Account a, Category c, long cents, int day) => new()
    {
        Id = Guid.NewGuid(), HouseholdId = h, AccountId = a.Id, Date = new DateOnly(2027, 2, day), AmountCents = cents, Merchant = "TEST MARKET",
        Description = "TEST", Kind = TransactionKind.Purchase, CategoryId = c.Id, CategorySource = CategorySource.User, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
    };

    private async Task SeedAsync(params object[] rows)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = app.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task<long> SpentAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<BudgetSummaryDto>("/api/budget/summary", Json))!.TotalVariableSpentCents;

    [Fact]
    public async Task Closing_an_account_keeps_its_spending_and_moves_what_pointed_at_it()
    {
        var (c, h) = await NewUserAsync();
        var oldChecking = New(h, "Old Checking", AccountType.Checking, 300_00, 90);
        var newChecking = New(h, "New Checking", AccountType.Checking, 1_200_00, 10);
        var oldSavings = New(h, "Old Savings", AccountType.Savings, 0, 90, percent: 60);
        var newSavings = New(h, "New Savings", AccountType.Savings, 900_00, 10, percent: 40);
        var food = new Category { Id = Guid.NewGuid(), HouseholdId = h, Name = "Test Food", Type = CategoryType.Variable, MonthlyLimitCents = 400_00 };
        var trip = new SavingsGoal { Id = Guid.NewGuid(), HouseholdId = h, Name = "Test Trip", TargetCents = 2_000_00, AccountId = oldSavings.Id, CreatedUtc = DateTime.UtcNow };
        await SeedAsync(oldChecking, newChecking, oldSavings, newSavings, food, trip, Spend(h, oldChecking, food, 55_00, 20));
        var spent = await SpentAsync(c);
        Assert.True(spent >= 55_00);

        // A replacement has to be open and of the same kind.
        var wrong = await c.PostAsJsonAsync($"/api/accounts/{oldSavings.Id}/close", new CloseAccountRequest(newChecking.Id), Json);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        // What closing the old savings would touch, then close it into the new one: its goal and its share go along.
        var impact = (await c.GetFromJsonAsync<AccountImpactDto>($"/api/accounts/{oldSavings.Id}/impact", Json))!;
        Assert.Equal(["Test Trip"], impact.Goals);
        Assert.Equal([newSavings.Id], impact.MoveTo.Select(x => x.Id));
        Assert.Equal(60, impact.SavingsPercent);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync($"/api/accounts/{oldSavings.Id}/close", new CloseAccountRequest(newSavings.Id), Json)).StatusCode);
        var savings = (await c.GetFromJsonAsync<SavingsSummaryDto>("/api/savings", Json))!;
        Assert.Equal([(newSavings.Id, 100)], savings.Accounts.Select(a => (a.Id, a.SharePercent)));
        Assert.Equal(newSavings.Id, savings.Goals.Single(g => g.Name == "Test Trip").AccountId);

        // Closing the old checking with nothing named: its spending this month still counts, its balance doesn't.
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync($"/api/accounts/{oldChecking.Id}/close", new CloseAccountRequest(null), Json)).StatusCode);
        Assert.Equal(spent, await SpentAsync(c));
        var checking = (await c.GetFromJsonAsync<List<AccountBalanceDto>>("/api/accounts/balances?type=checking", Json))!;
        Assert.Equal([newChecking.Id], checking.Select(a => a.Id));
        var listed = (await c.GetFromJsonAsync<List<ManagedAccountDto>>("/api/accounts/manage", Json))!.Single(a => a.Id == oldChecking.Id);
        Assert.Equal((new DateOnly(2027, 2, 24), AccountNames.Label(newChecking), 1), (listed.ClosedOn!.Value, listed.ReplacedBy!, listed.TransactionCount));

        // Reopened, it's back.
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/accounts/{oldChecking.Id}/reopen", null)).StatusCode);
        Assert.Equal(2, (await c.GetFromJsonAsync<List<AccountBalanceDto>>("/api/accounts/balances?type=checking", Json))!.Count);
    }

    [Fact]
    public async Task A_bill_or_leftover_move_naming_a_closed_account_goes_to_what_replaced_it()
    {
        var (c, h) = await NewUserAsync();
        var oldSavings = New(h, "Old Savings", AccountType.Savings, 0, 90);
        var newSavings = New(h, "New Savings", AccountType.Savings, 500_00, 10);
        var month = new BudgetMonth { Id = Guid.NewGuid(), HouseholdId = h, StartDate = new DateOnly(2027, 1, 1), EndDate = new DateOnly(2027, 1, 31) };
        var sweep = new SweepTask { Id = Guid.NewGuid(), HouseholdId = h, BudgetMonthId = month.Id, Kind = SweepKind.Savings, AmountCents = 30_00, Destination = AccountNames.Imported(oldSavings) };
        await SeedAsync(oldSavings, newSavings, month, sweep);

        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync($"/api/accounts/{oldSavings.Id}/close", new CloseAccountRequest(newSavings.Id), Json)).StatusCode);
        Assert.Equal(AccountNames.Imported(newSavings), await DbAsync(db => db.SweepTasks.Where(s => s.Id == sweep.Id).Select(s => s.Destination).SingleAsync()));
        // And a label saved before is read as the account that took over.
        var all = await DbAsync(db => db.Accounts.Where(a => a.HouseholdId == h).ToListAsync());
        Assert.Equal(AccountNames.Label(newSavings), AccountNames.Current(all, AccountNames.Imported(oldSavings)));
    }

    [Fact]
    public async Task Deleting_an_account_takes_its_transactions_but_not_a_bonus_that_landed_on_it()
    {
        var (c, h) = await NewUserAsync();
        var keep = New(h, "Kept Checking", AccountType.Checking, 800_00, 60);
        var gone = New(h, "Gone Checking", AccountType.Checking, 100_00, 90);
        var savings = New(h, "Only Savings", AccountType.Savings, 250_00, 90);
        var food = new Category { Id = Guid.NewGuid(), HouseholdId = h, Name = "Test Food", Type = CategoryType.Variable, MonthlyLimitCents = 400_00 };
        var spend = Spend(h, gone, food, 40_00, 18);
        var deposit = new Transaction
        {
            Id = Guid.NewGuid(), HouseholdId = h, AccountId = gone.Id, Date = new DateOnly(2027, 2, 15), AmountCents = -700_00, Merchant = "TEST EMPLOYER BONUS",
            Description = "TEST", Kind = TransactionKind.Income, IncomeType = IncomeType.Other, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
        };
        var moveIn = new Transaction
        {
            Id = Guid.NewGuid(), HouseholdId = h, AccountId = keep.Id, Date = new DateOnly(2027, 2, 20), AmountCents = -100_00, Merchant = "TRANSFER IN",
            Description = "TEST", Kind = TransactionKind.Transfer, IsTransfer = true, TransferAccountId = gone.Id, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
        };
        var bonus = new ExpectedIncome { Id = Guid.NewGuid(), HouseholdId = h, Name = "Test bonus", AmountCents = 700_00, ExpectedDate = new DateOnly(2027, 2, 15), DepositTransactionId = deposit.Id, CreatedUtc = DateTime.UtcNow };
        var goal = new SavingsGoal { Id = Guid.NewGuid(), HouseholdId = h, Name = "Test Fund", TargetCents = 1_000_00, AccountId = savings.Id, CreatedUtc = DateTime.UtcNow };
        await SeedAsync(keep, gone, savings, food, spend, deposit, moveIn, bonus, goal);
        var spent = await SpentAsync(c);

        var impact = (await c.GetFromJsonAsync<AccountImpactDto>($"/api/accounts/{gone.Id}/impact", Json))!;
        Assert.Equal((2, new DateOnly(2027, 2, 15)), (impact.TransactionCount, impact.FirstDate!.Value));
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/accounts/{gone.Id}")).StatusCode);

        // Its spending is gone from the month; the other side of its transfer stays, pointing nowhere.
        Assert.Equal(spent - 40_00, await SpentAsync(c));
        Assert.Null(await DbAsync(db => db.Transactions.Where(t => t.Id == moveIn.Id).Select(t => t.TransferAccountId).SingleAsync()));
        // The bonus stays landed: it isn't expected all over again.
        await DbAsync(async db => { await ExpectedIncomes.MatchAsync(db, h, default); return 0; });
        Assert.Equal(deposit.Id, await DbAsync(db => db.ExpectedIncomes.Where(x => x.Id == bonus.Id).Select(x => x.DepositTransactionId).SingleAsync()));

        // Deleting the only savings account: its goal keeps what it had, tracked by hand.
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/accounts/{savings.Id}")).StatusCode);
        var fund = (await c.GetFromJsonAsync<SavingsSummaryDto>("/api/savings", Json))!.Goals.Single(g => g.Name == "Test Fund");
        Assert.Equal(((Guid?)null, 250_00L, 250_00L), (fund.AccountId, fund.ManualSavedCents, fund.SavedCents));
    }

    [Fact]
    public async Task A_savings_account_that_was_checking_all_along_files_its_transfers_again()
    {
        // DECISIONS.md 319: the import guessed savings; it's really a second checking account.
        var (c, h) = await NewUserAsync();
        var checking = New(h, "Main Checking", AccountType.Checking, 900_00, 60);
        var wrong = New(h, "Second Account", AccountType.Savings, 300_00, 60, percent: 100);
        var card = New(h, "Test Card", AccountType.CreditCard, -50_00, 60);
        var line = new Category { Id = Guid.NewGuid(), HouseholdId = h, Name = "Savings", Type = CategoryType.SavingsTransfer, MonthlyLimitCents = 100_00 };
        var goal = new SavingsGoal { Id = Guid.NewGuid(), HouseholdId = h, Name = "Test Goal", TargetCents = 1_000_00, AccountId = wrong.Id, CreatedUtc = DateTime.UtcNow };
        var move = new Transaction
        {
            Id = Guid.NewGuid(), HouseholdId = h, AccountId = checking.Id, Date = new DateOnly(2027, 2, 20), AmountCents = 80_00, Merchant = "TRANSFER OUT",
            Description = "TEST", Kind = TransactionKind.Transfer, NeedsReview = true, Source = DataSource.Csv, CreatedUtc = DateTime.UtcNow,
        };
        await SeedAsync(checking, wrong, card, line, goal, move);
        await c.PostAsJsonAsync($"/api/transactions/{move.Id}/transfer", new TransferRequest(wrong.Id), Json);
        Assert.Equal(line.Id, await DbAsync(db => db.Transactions.Where(t => t.Id == move.Id).Select(t => t.CategoryId).SingleAsync()));

        // A card can't trade places with a bank account.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/accounts/{card.Id}/type", new AccountTypeRequest(AccountType.Checking), Json)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api/accounts/{wrong.Id}/type", new AccountTypeRequest(AccountType.Checking), Json)).StatusCode);
        // Money into checking isn't saving: off the Savings line. Its goal keeps what it had, by hand.
        Assert.Null(await DbAsync(db => db.Transactions.Where(t => t.Id == move.Id).Select(t => t.CategoryId).SingleAsync()));
        var g = await DbAsync(db => db.SavingsGoals.SingleAsync(x => x.Id == goal.Id));
        Assert.Equal(((Guid?)null, (long?)300_00), (g.AccountId, g.ManualSavedCents));
        Assert.Equal(2, (await c.GetFromJsonAsync<List<AccountBalanceDto>>("/api/accounts/balances?type=checking", Json))!.Count);
    }

    [Fact]
    public async Task Leaving_an_account_out_of_the_budget_takes_its_spending_out_and_back()
    {
        var (c, h) = await NewUserAsync();
        var side = New(h, "Side Checking", AccountType.Checking, 100_00, 60);
        var food = new Category { Id = Guid.NewGuid(), HouseholdId = h, Name = "Test Food", Type = CategoryType.Variable, MonthlyLimitCents = 400_00 };
        await SeedAsync(side, food, Spend(h, side, food, 35_00, 19));
        var spent = await SpentAsync(c);

        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api/accounts/{side.Id}/budget", new CountInBudgetRequest(false), Json)).StatusCode);
        Assert.Equal(spent - 35_00, await SpentAsync(c));
        var listed = (await c.GetFromJsonAsync<List<ManagedAccountDto>>("/api/accounts/manage", Json))!.Single(a => a.Id == side.Id);
        Assert.False(listed.IncludedInBudget);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api/accounts/{side.Id}/budget", new CountInBudgetRequest(true), Json)).StatusCode);
        Assert.Equal(spent, await SpentAsync(c));
    }

    [Fact]
    public async Task An_account_you_track_yourself_starts_with_its_balance_and_one_without_asks_for_it()
    {
        var (c, _) = await NewUserAsync();
        var mine = (await (await c.PostAsJsonAsync("/api/accounts", new SmoothBudget.Api.Import.CreateAccountRequest("Cash Jar", AccountType.Checking, null, 120_50), Json))
            .Content.ReadFromJsonAsync<SmoothBudget.Api.Import.AccountDto>(Json))!;
        var csv = (await (await c.PostAsJsonAsync("/api/accounts", new SmoothBudget.Api.Import.CreateAccountRequest("Uploaded", AccountType.Savings, "1234"), Json))
            .Content.ReadFromJsonAsync<SmoothBudget.Api.Import.AccountDto>(Json))!;
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync("/api/accounts", new SmoothBudget.Api.Import.CreateAccountRequest("Card", AccountType.CreditCard, null, 10_00), Json)).StatusCode);

        var list = (await c.GetFromJsonAsync<List<ManagedAccountDto>>("/api/accounts/manage", Json))!;
        var m = list.Single(a => a.Id == mine.Id);
        Assert.Equal(("Manual", 120_50L, new DateOnly(2027, 2, 24), (string?)null), (m.Source, m.BalanceCents, m.BalanceAsOf!.Value, m.Attention));
        Assert.StartsWith("No balance yet", list.Single(a => a.Id == csv.Id).Attention);
    }

    [Fact]
    public async Task Disconnecting_a_bank_can_keep_its_accounts_as_closed_history()
    {
        var (c, h) = await NewUserAsync();
        string token;
        using (var scope = app.Services.CreateScope())
            token = scope.ServiceProvider.GetRequiredService<SecretBox>().Encrypt("access-sandbox-test");
        var item = new PlaidItem { Id = Guid.NewGuid(), HouseholdId = h, PlaidItemId = $"item-{Guid.NewGuid():N}", AccessTokenEncrypted = token, InstitutionName = "Test Bank", CreatedUtc = DateTime.UtcNow };
        var linked = New(h, "Linked Checking", AccountType.Checking, 400_00, 60);
        (linked.PlaidItemId, linked.PlaidAccountId, linked.Source) = (item.Id, $"acct-{Guid.NewGuid():N}", DataSource.Plaid);
        var food = new Category { Id = Guid.NewGuid(), HouseholdId = h, Name = "Test Food", Type = CategoryType.Variable, MonthlyLimitCents = 400_00 };
        await SeedAsync(item, linked, food, Spend(h, linked, food, 25_00, 21));
        var spent = await SpentAsync(c);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/plaid/items/{item.Id}?accounts=close")).StatusCode);
        var a = (await c.GetFromJsonAsync<List<ManagedAccountDto>>("/api/accounts/manage", Json))!.Single(x => x.Id == linked.Id);
        Assert.Equal((new DateOnly(2027, 2, 24), false, (Guid?)null), (a.ClosedOn!.Value, a.BankSynced, a.BankId));
        Assert.Equal(spent, await SpentAsync(c)); // its history stays
    }
}
