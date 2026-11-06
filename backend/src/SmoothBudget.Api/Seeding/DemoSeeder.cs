using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Home;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Import;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Seeding;

/// <summary>
/// Fills a household with FAKE data: the budget month that contains "today" plus three complete months
/// before it (history for the budget generator and month closing). Every merchant, account and amount is invented.
/// Transactions are laid out by day offset from the budget month start; only those dated on or
/// before today are created, and anything dated today or yesterday is marked pending.
/// </summary>
public class DemoSeeder(AppDbContext db, UserManager<AppUser> users, TimeProvider clock, BudgetMonthService months, ILogger<DemoSeeder> log)
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
        await db.ImportBatches.Where(b => b.HouseholdId == householdId).ExecuteDeleteAsync(ct);
        await db.CategoryRules.Where(r => r.HouseholdId == householdId).ExecuteDeleteAsync(ct);
        await db.BudgetMonths.Where(b => b.HouseholdId == householdId).ExecuteDeleteAsync(ct); // also removes per-month categories and sweeps
        await db.Categories.Where(c => c.HouseholdId == householdId).ExecuteDeleteAsync(ct);
        await db.DebtAccounts.Where(d => d.HouseholdId == householdId).ExecuteDeleteAsync(ct);
        await db.Accounts.Where(a => a.HouseholdId == householdId).ExecuteDeleteAsync(ct);
        await db.PlaidItems.Where(i => i.HouseholdId == householdId).ExecuteDeleteAsync(ct); // demo only; sandbox items are disposable
        household.SweepTarget = SweepTarget.Debt;
        household.SweepSavingsPercent = 50;
        household.PartnerName = "Partner"; // matches the fake "Zelle payment from Partner" rows

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

        // Card details for the Debt tab (invented): tracked since August, so there's progress to show.
        DebtAccount Debt(Account card, int aprBps, long minimum, int dueDay, long limit, long starting) => new()
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, AccountId = card.Id, Name = card.Name, BalanceCents = -card.CurrentBalanceCents,
            AprBps = aprBps, MinimumPaymentCents = minimum, DueDay = dueDay, CreditLimitCents = limit, StartingBalanceCents = starting,
            StartedOn = today.AddMonths(-3), CreatedUtc = now,
        };
        db.DebtAccounts.AddRange(Debt(visa, 2499, 115_00, 3, 7_500_00, 4_600_00), Debt(mastercard, 1999, 45_00, 18, 3_000_00, 1_900_00));
        household.DebtStrategy = SmoothBudget.Core.Debt.DebtStrategy.Avalanche;
        household.ExtraDebtPaymentCents = null;

        var categories = DefaultCategories(householdId);
        db.Categories.AddRange(categories);
        Category C(string name) => categories.Single(c => c.Name == name);
        var (rent, phone, insurance, utilities, subscriptions, cardPayment, rainyDay) =
            (C("Rent"), C("Phone"), C("Car insurance"), C("Utilities & internet"), C("Subscriptions"), C("Credit card payment"), C("Rainy-day fund"));
        var (groceries, eatingOut, gas, shopping, entertainment, household_, personal) =
            (C("Groceries"), C("Eating out"), C("Gas"), C("Shopping"), C("Entertainment"), C("Household"), C("Personal care"));

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

        // Four budget months: three complete ones (history for the budget generator) and the current one.
        //  -3 and -2: the plan with variable spending scaled to 92% and 104%; closed, each with a surplus to sweep
        //             (the older sweep already marked as sent).
        //  -1:        the plan plus a one-off splurge, so it ends exactly DemoCarriedInDeficitCents over: that
        //             deficit shows as "Carried over" this month. Left open; it closes after its grace period.
        //   0:        the plan up to today; today's and yesterday's purchases are pending.
        var calendar = new BudgetMonthCalendar(household.BudgetMonthStartDay);
        var rows = new List<BudgetMonth>();
        for (var back = 3; back >= 0; back--)
        {
            var label = month.Label.AddMonths(-back);
            var m = calendar.MonthOf(label.Year, label.Month);
            var scale = back switch { 3 => 92, 2 => 104, _ => 100 };
            var monthPlan = back == 1
                ? plan.Append((Day: 6, Account: mastercard, Category: (Category?)entertainment, Merchant: "Concert Tickets", Cents: 1_010_00 - 890_97 + DemoCarriedInDeficitCents, Transfer: false)) // 161.53: variable limits minus the plan's spending, plus the deficit
                : plan.AsEnumerable();

            foreach (var p in monthPlan)
            {
                var date = m.Start.AddDays(p.Day);
                if (date > today || date > m.End)
                    continue;
                var scalable = !p.Transfer && p.Category?.Type == CategoryType.Variable && p.Cents > 0;
                var cents = scalable ? (long)Math.Round(p.Cents * scale / 100m, MidpointRounding.AwayFromZero) : p.Cents;
                db.Transactions.Add(new Transaction
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, AccountId = p.Account.Id, CategoryId = p.Category?.Id,
                    Date = date, AmountCents = cents, Merchant = MerchantText.Normalize(p.Merchant), Description = p.Merchant, IsTransfer = p.Transfer,
                    Kind = p.Transfer
                        ? (p.Merchant.Contains("Payment", StringComparison.OrdinalIgnoreCase) ? TransactionKind.CardPayment : TransactionKind.Transfer)
                        : p.Cents < 0
                            ? (p.Merchant.Contains("return", StringComparison.OrdinalIgnoreCase) ? TransactionKind.Refund
                                : p.Merchant.Contains("Zelle payment from Partner", StringComparison.Ordinal) ? TransactionKind.PartnerContribution
                                : TransactionKind.Income)
                            : TransactionKind.Purchase,
                    // Past months count as already reviewed; this month's purchases wait for the user to confirm them.
                    CategorySource = p.Category is null ? CategorySource.None : p.Transfer ? CategorySource.Transfer : back == 0 ? CategorySource.Bank : CategorySource.User,
                    NeedsReview = back == 0 && p.Category is not null && !p.Transfer,
                    CategoryReason = p.Category is null || p.Transfer ? null : back == 0 ? "Demo: filed automatically." : "Demo: reviewed.",
                    Pending = back == 0 && date >= today.AddDays(-1), Source = DataSource.Seed, CreatedUtc = now,
                });
            }

            rows.Add(db.BudgetMonths.Add(new BudgetMonth
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, StartDate = m.Start, EndDate = m.End,
                IncomeExpectedCents = 1_700_86 + 760_00, PartnerExpectedCents = 800_00,
            }).Entity);
        }
        await db.SaveChangesAsync(ct);

        await months.CloseAsync(householdId, rows[0], ct);
        await months.CloseAsync(householdId, rows[1], ct);
        await db.SweepTasks.Where(s => s.BudgetMonthId == rows[0].Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DoneUtc, now), ct);

        log.LogInformation("Seeded household {HouseholdId}: budget months {First}..{Last} (today {Today})", householdId, rows[0].StartDate, month.End, today);
    }

    /// <summary>A starter set of categories with made-up limits. Variable limits total $1,010.00.</summary>
    public static List<Category> DefaultCategories(Guid householdId)
    {
        var order = 0;
        Category New(string name, CategoryType type, long limit, bool discretionary = false) => new()
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, Name = name, Type = type,
            MonthlyLimitCents = limit, IsDiscretionary = discretionary, SortOrder = order++,
        };
        return
        [
            New("Rent", CategoryType.FixedBill, 1_650_00),
            New("Phone", CategoryType.FixedBill, 65_00),
            New("Car insurance", CategoryType.FixedBill, 120_00),
            New("Utilities & internet", CategoryType.FixedBill, 140_00),
            New("Subscriptions", CategoryType.FixedBill, 35_00),
            New("Credit card payment", CategoryType.DebtPayment, 300_00),
            New("Rainy-day fund", CategoryType.SavingsTransfer, 100_00),
            New("Groceries", CategoryType.Variable, 450_00),
            New("Eating out", CategoryType.Variable, 150_00, discretionary: true),
            New("Gas", CategoryType.Variable, 160_00),
            New("Shopping", CategoryType.Variable, 100_00, discretionary: true),
            New("Entertainment", CategoryType.Variable, 60_00, discretionary: true),
            New("Household", CategoryType.Variable, 50_00),
            New("Personal care", CategoryType.Variable, 40_00),
        ];
    }
}
