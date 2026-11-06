using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Home;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Api.Seeding;

/// <summary>
/// Fills a household with FAKE data for the budget month that contains "today", so the Home screen
/// always has something realistic to show. Every merchant, account and amount here is invented.
/// Transactions are laid out by day offset from the budget month start; only those dated on or
/// before today are created, and anything dated today or yesterday is marked pending.
/// </summary>
public class DemoSeeder(AppDbContext db, UserManager<AppUser> users, TimeProvider clock, ILogger<DemoSeeder> log)
{
    public const string DemoEmail = "demo@smoothbudget.test";

    /// <summary>Carried-over deficit from the (fictional) previous month, to exercise that line on Home.</summary>
    public const long DemoCarriedInDeficitCents = 42_50;

    /// <summary>Creates (or resets) the demo login and its household, then seeds it.</summary>
    public async Task<Guid> SeedDemoUserAsync(string password, CancellationToken ct = default)
    {
        var user = await users.FindByEmailAsync(DemoEmail);
        if (user is null)
        {
            var household = new Household { Id = Guid.NewGuid(), Name = "Demo household", CreatedUtc = clock.GetUtcNow().UtcDateTime };
            db.Households.Add(household);
            await db.SaveChangesAsync(ct);

            user = new AppUser { Id = Guid.NewGuid(), UserName = DemoEmail, Email = DemoEmail, EmailConfirmed = true, HouseholdId = household.Id };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
                throw new InvalidOperationException("Could not create demo user: " + string.Join("; ", created.Errors.Select(e => e.Description)));
            log.LogInformation("Created demo user {Email}", DemoEmail);
        }
        else
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var reset = await users.ResetPasswordAsync(user, token, password);
            if (!reset.Succeeded)
                throw new InvalidOperationException("Could not reset demo password: " + string.Join("; ", reset.Errors.Select(e => e.Description)));
            await users.SetLockoutEndDateAsync(user, null);
        }

        await SeedHouseholdAsync(user.HouseholdId, ct);
        return user.HouseholdId;
    }

    /// <summary>Deletes the household's financial data and re-creates the fake data set.</summary>
    public async Task SeedHouseholdAsync(Guid householdId, CancellationToken ct = default)
    {
        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var today = HomeSummaryService.TodayIn(household.TimeZoneId, clock);
        var month = new BudgetMonthCalendar(household.BudgetMonthStartDay).MonthContaining(today);

        await db.Transactions.Where(t => t.HouseholdId == householdId).ExecuteDeleteAsync(ct);
        await db.Categories.Where(c => c.HouseholdId == householdId).ExecuteDeleteAsync(ct);
        await db.Accounts.Where(a => a.HouseholdId == householdId).ExecuteDeleteAsync(ct);
        await db.BudgetMonths.Where(b => b.HouseholdId == householdId).ExecuteDeleteAsync(ct);

        Account NewAccount(string name, AccountType type, string mask, long balance) => new()
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, Name = name, Type = type, Mask = mask,
            CurrentBalanceCents = balance, Source = DataSource.Seed, CreatedUtc = now,
        };
        var checking = NewAccount("Demo Checking", AccountType.Checking, "0001", 1_204_33);
        var visa = NewAccount("Demo Visa", AccountType.CreditCard, "0002", -3_812_40);
        var mastercard = NewAccount("Demo Mastercard", AccountType.CreditCard, "0003", -1_455_10);
        var savings = NewAccount("Demo Savings", AccountType.Savings, "0004", 250_00);
        db.Accounts.AddRange(checking, visa, mastercard, savings);

        var order = 0;
        Category NewCategory(string name, CategoryType type, long limit, bool discretionary = false) => new()
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, Name = name, Type = type,
            MonthlyLimitCents = limit, IsDiscretionary = discretionary, SortOrder = order++,
        };
        var rent = NewCategory("Rent", CategoryType.FixedBill, 1_650_00);
        var phone = NewCategory("Phone", CategoryType.FixedBill, 65_00);
        var insurance = NewCategory("Car insurance", CategoryType.FixedBill, 120_00);
        var utilities = NewCategory("Utilities & internet", CategoryType.FixedBill, 140_00);
        var subscriptions = NewCategory("Subscriptions", CategoryType.FixedBill, 35_00);
        var cardPayment = NewCategory("Credit card payment", CategoryType.DebtPayment, 300_00);
        var rainyDay = NewCategory("Rainy-day fund", CategoryType.SavingsTransfer, 100_00);
        var groceries = NewCategory("Groceries", CategoryType.Variable, 450_00);
        var eatingOut = NewCategory("Eating out", CategoryType.Variable, 150_00, discretionary: true);
        var gas = NewCategory("Gas", CategoryType.Variable, 160_00);
        var shopping = NewCategory("Shopping", CategoryType.Variable, 100_00, discretionary: true);
        var entertainment = NewCategory("Entertainment", CategoryType.Variable, 60_00, discretionary: true);
        var household_ = NewCategory("Household", CategoryType.Variable, 50_00);
        var personal = NewCategory("Personal care", CategoryType.Variable, 40_00);
        db.Categories.AddRange(rent, phone, insurance, utilities, subscriptions, cardPayment, rainyDay,
            groceries, eatingOut, gas, shopping, entertainment, household_, personal);

        // (day offset from month start, account, category, merchant, cents, isTransfer)
        var plan = new (int Day, Account Account, Category? Category, string Merchant, long Cents, bool Transfer)[]
        {
            // Income: money in, uncategorized, never spending.
            (0, checking, null, "Payroll deposit", -1_700_86, false),
            (15, checking, null, "Zelle payment from Partner", -800_00, false),
            (16, checking, null, "Payroll deposit", -760_00, false),

            // Fixed bills.
            (0, checking, rent, "Maple Court Apartments", 1_650_00, false),
            (3, visa, phone, "Example Wireless", 65_00, false),
            (5, mastercard, subscriptions, "StreamCo", 15_49, false),
            (10, checking, insurance, "Acme Auto Insurance", 120_00, false),
            (12, visa, utilities, "Front Range Internet", 70_00, false),
            (14, checking, utilities, "City Power & Light", 68_40, false),

            // Transfers: both legs excluded from spending.
            (1, checking, rainyDay, "Transfer to Demo Savings", 100_00, true),
            (1, savings, null, "Transfer from Demo Checking", -100_00, true),
            (17, checking, cardPayment, "Payment to Demo Visa", 300_00, true),
            (17, visa, null, "Payment received, thank you", -300_00, true),

            // Variable spending: these drive Left to Spend.
            (1, visa, groceries, "Corner Grocery", 82_17, false),
            (2, mastercard, eatingOut, "Taqueria Uno", 18_45, false),
            (4, visa, gas, "Fuel Stop", 44_10, false),
            (5, mastercard, eatingOut, "Noodle House", 32_10, false),
            (6, visa, groceries, "Corner Grocery", 64_30, false),
            (7, mastercard, shopping, "Big Box Store", 36_99, false),
            (8, mastercard, eatingOut, "Coffee Cart", 12_75, false),
            (9, mastercard, shopping, "Big Box Store (return)", -12_00, false),
            (9, visa, household_, "Hardware Barn", 23_47, false),
            (10, mastercard, entertainment, "Cinema 8", 14_00, false),
            (11, visa, groceries, "Fresh Market", 91_45, false),
            (12, visa, gas, "Fuel Stop", 39_85, false),
            (13, mastercard, eatingOut, "Pizza Place", 41_20, false),
            (15, visa, personal, "Quick Cuts", 18_00, false),
            (16, visa, groceries, "Corner Grocery", 58_12, false),
            (18, mastercard, eatingOut, "Burger Joint", 27_90, false),
            (19, mastercard, shopping, "Online Retailer", 24_50, false),
            (20, visa, gas, "Fuel Stop", 47_30, false),
            (21, visa, groceries, "Fresh Market", 73_88, false),
            (22, visa, household_, "Hardware Barn", 11_99, false),
            (23, mastercard, eatingOut, "Thai Kitchen", 39_95, false),
            (24, mastercard, entertainment, "Bowling Lanes", 22_50, false),
            (25, checking, null, "Farmers Market Stand", 15_00, false), // left uncategorized on purpose
            (26, visa, groceries, "Corner Grocery", 49_60, false),
            (27, visa, gas, "Fuel Stop", 28_40, false),
        };

        foreach (var p in plan)
        {
            var date = month.Start.AddDays(p.Day);
            if (date > today || date > month.End)
                continue;
            db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, AccountId = p.Account.Id, CategoryId = p.Category?.Id,
                Date = date, AmountCents = p.Cents, Merchant = p.Merchant, IsTransfer = p.Transfer,
                Pending = date >= today.AddDays(-1), Source = DataSource.Seed, CreatedUtc = now,
            });
        }

        db.BudgetMonths.Add(new BudgetMonth
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, StartDate = month.Start, EndDate = month.End,
            IncomeExpectedCents = 1_700_86 + 760_00 + 800_00, CarriedInDeficitCents = DemoCarriedInDeficitCents,
        });

        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded household {HouseholdId} for budget month {Start}..{End} (today {Today})", householdId, month.Start, month.End, today);
    }
}
