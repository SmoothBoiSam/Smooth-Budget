using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Home;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Budget;

/// <summary>A category with the limit that applies in one particular budget month.</summary>
public sealed record MonthCategory(Guid Id, string Name, CategoryType Type, long LimitCents, CategoryPriority Priority, int SortOrder, bool HasMonthOverride)
{
    public BudgetCategory ForCalculator => new(Id, Type, LimitCents, Core.Transactions.CardInterest.IsInterestCategory(Name, Type));
}

/// <summary>Everything about one budget month that the calculators need.</summary>
public sealed record MonthData(
    BudgetMonth Row, BudgetMonthRange Range, IReadOnlyList<MonthCategory> Categories,
    IReadOnlyList<BudgetTransaction> Transactions, long CarriedInDeficitCents,
    // The partner's fixed contributions this month, and what Left to Spend is missing from late or short ones (SPEC 3).
    IReadOnlyList<ContributionStatus>? Contributions = null, long PartnerShortfallCents = 0)
{
    public MonthCloseResult Close() =>
        MonthCloseCalculator.Close(Range, Categories.Select(c => c.ForCalculator), Transactions, CarriedInDeficitCents, PartnerShortfallCents, MoneyInCents);

    /// <summary>
    /// The money this month's plan has to work with (SPEC section 3): expected income plus partner money. With fixed
    /// contributions set up, partner money is exactly those (DECISIONS.md 97).
    /// </summary>
    public long MoneyInCents => MoneyIn(Row, Contributions);

    public static long MoneyIn(BudgetMonth row, IReadOnlyList<ContributionStatus>? contributions) =>
        row.IncomeExpectedCents + (contributions is { Count: > 0 } cs ? cs.Sum(s => s.ExpectedCents) : row.PartnerExpectedCents);
}

/// <summary>An ended month's result: frozen if closed, otherwise worked out live (it may still change).</summary>
public sealed record EndedMonth(BudgetMonth Row, BudgetMonthRange Range, MonthCloseResult Result, bool Closed, DateOnly ClosesOn);

/// <summary>
/// Budget months over time (SPEC section 3, "Rollover and carry-forward"):
/// <list type="bullet">
/// <item>A row exists for every month from the first one on record through the current one; missing ones are created.</item>
/// <item>A month that ended more than <see cref="GraceDays"/> days ago is closed automatically: its result, each
/// category's limit and spending, and its sweep items are frozen. Until then its result is provisional
/// and updates as late CSV or Plaid transactions arrive. "Close now" closes it early.</item>
/// <item>A month's carried-in deficit is the previous month's deficit (frozen or provisional).</item>
/// </list>
/// </summary>
public class BudgetMonthService(AppDbContext db, TimeProvider clock)
{
    public const int GraceDays = 5;

    public async Task<(Household Household, DateOnly Today, BudgetMonthCalendar Calendar)> ContextAsync(Guid householdId, CancellationToken ct)
    {
        var h = await db.Households.SingleAsync(x => x.Id == householdId, ct);
        return (h, HomeSummaryService.TodayIn(h.TimeZoneId, clock), new BudgetMonthCalendar(h.BudgetMonthStartDay));
    }

    /// <summary>Makes sure rows exist up to the current month and closes months past their grace period. Returns the current month's row.</summary>
    /// <remarks>
    /// Screens load at once, so several requests can find the same month to create, freeze or close. That work runs one
    /// request at a time per household, under a lock (DECISIONS.md 260); the usual case, nothing to do, takes no lock.
    /// </remarks>
    public async Task<BudgetMonth> EnsureCurrentAsync(Guid householdId, CancellationToken ct)
    {
        var (_, today, calendar) = await ContextAsync(householdId, ct);
        var current = calendar.MonthContaining(today);
        if (!await NeedsWorkAsync(householdId, current, today, ct))
            return await db.BudgetMonths.SingleAsync(m => m.HouseholdId == householdId && m.StartDate == current.Start, ct);
        BudgetMonth result = null!;
        await InMonthLockAsync(householdId, async () => result = await EnsureCurrentLockedAsync(householdId, today, calendar, current, ct), ct);
        return result;
    }

    /// <summary>Whether a month is missing, has ended without its limits frozen, or is past its grace period and still open.</summary>
    private async Task<bool> NeedsWorkAsync(Guid householdId, BudgetMonthRange current, DateOnly today, CancellationToken ct)
    {
        var months = await db.BudgetMonths.AsNoTracking().Where(m => m.HouseholdId == householdId)
            .Select(m => new
            {
                m.StartDate, m.EndDate, m.ClosedUtc,
                Frozen = db.BudgetMonthCategories.Any(x => x.BudgetMonthId == m.Id && x.ClosingLimitCents != null),
            })
            .ToListAsync(ct);
        if (!months.Any(m => m.StartDate == current.Start) || months.Max(m => m.StartDate) < current.Start)
            return true;
        return months.Any(m => (m.EndDate < today && !m.Frozen) || (m.ClosedUtc == null && m.EndDate.DayNumber < today.DayNumber - GraceDays));
    }

    /// <summary>
    /// Runs month work one request at a time for this household: a Postgres lock held until the transaction ends, so a
    /// second request waits, then finds the work done. Inside a transaction already, it takes the lock in that one.
    /// </summary>
    private async Task InMonthLockAsync(Guid householdId, Func<Task> work, CancellationToken ct)
    {
        var key = BitConverter.ToInt64(householdId.ToByteArray(), 0) ^ MonthLockSalt;
        if (db.Database.CurrentTransaction is not null)
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct);
            await work();
            return;
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct);
        await work();
        await tx.CommitAsync(ct);
    }

    /// <summary>Keeps these locks apart from any other advisory lock keyed by a household.</summary>
    private const long MonthLockSalt = 0x4255_444D_4F4E_5448;

    private async Task<BudgetMonth> EnsureCurrentLockedAsync(Guid householdId, DateOnly today, BudgetMonthCalendar calendar, BudgetMonthRange current, CancellationToken ct)
    {
        // Rows this request read before the lock may be stale: another request may just have closed them.
        foreach (var e in db.ChangeTracker.Entries<BudgetMonth>().Where(e => e.State == EntityState.Unchanged && e.Entity.HouseholdId == householdId).ToList())
            await e.ReloadAsync(ct);
        var rows = await db.BudgetMonths.Where(m => m.HouseholdId == householdId).OrderBy(m => m.StartDate).ToListAsync(ct);
        if (rows.Count == 0)
        {
            rows.Add(db.BudgetMonths.Add(NewRow(householdId, current, income: 0, partner: 0)).Entity);
        }
        else
        {
            // Fill any gap between the last month on record and now (the app wasn't opened for a while).
            var last = rows[^1];
            var label = calendar.MonthContaining(last.StartDate).Label;
            while (rows[^1].StartDate < current.Start)
            {
                label = label.AddMonths(1);
                rows.Add(db.BudgetMonths.Add(NewRow(householdId, calendar.MonthOf(label.Year, label.Month), rows[^1].IncomeExpectedCents, rows[^1].PartnerExpectedCents)).Entity);
            }
        }
        if (!await SaveTolerantAsync(ct))
            rows = await db.BudgetMonths.Where(m => m.HouseholdId == householdId).OrderBy(m => m.StartDate).ToListAsync(ct);

        // A month's limits are the ones in effect while it ran: freeze them as soon as it ends, so editing
        // limits "from now on" never rewrites a month that's over (only its spending can still change).
        foreach (var row in rows.Where(r => r.EndDate < today))
            await FreezeLimitsAsync(householdId, row, ct);

        foreach (var row in rows.Where(r => r.ClosedUtc is null && r.EndDate.DayNumber < today.DayNumber - GraceDays))
            await CloseLockedAsync(householdId, row, ct);

        return rows.Single(r => r.StartDate == current.Start);
    }

    /// <summary>Loads one month's categories (with that month's limits), transactions and carried-in deficit.</summary>
    public async Task<MonthData> LoadAsync(Guid householdId, BudgetMonth row, CancellationToken ct, int depth = 0)
    {
        var (_, _, calendar) = await ContextAsync(householdId, ct);
        var range = calendar.MonthContaining(row.StartDate);

        var perMonth = await db.BudgetMonthCategories.AsNoTracking().Where(x => x.BudgetMonthId == row.Id).ToListAsync(ct);
        List<MonthCategory> categories;
        if (perMonth.Any(p => p.ClosingLimitCents is not null))
        {
            // Ended: use the limits frozen when it ended, including categories hidden since.
            var ids = perMonth.Where(p => p.ClosingLimitCents is not null).Select(p => p.CategoryId).ToList();
            var cats = await db.Categories.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
            categories = cats.Select(c => new MonthCategory(c.Id, c.Name, c.Type, perMonth.Single(p => p.CategoryId == c.Id).ClosingLimitCents!.Value,
                c.Priority, c.SortOrder, false)).ToList();
        }
        else
        {
            var cats = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == householdId && !c.IsHidden).ToListAsync(ct);
            categories = cats.Select(c =>
            {
                var o = perMonth.SingleOrDefault(p => p.CategoryId == c.Id)?.LimitOverrideCents;
                return new MonthCategory(c.Id, c.Name, c.Type, o ?? c.MonthlyLimitCents, c.Priority, c.SortOrder, o is not null);
            }).ToList();
        }
        categories = categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToList();

        var transactions = await TransactionsAsync(db, householdId, range.Start, range.End, ct);

        // A month still running: a line that's overspent has its limit raised to what's spent (DECISIONS.md 268), so it
        // isn't over and the money comes out of the plan instead: the automatic budget squeezes the other lines, or
        // Left to Spend loses what the plan now needs beyond the month's money. That covers a limit you set yourself, for
        // this month only. Not the card lines: the payments and Card interest are one pot, checked together. Only with
        // income on record: without it there's no plan to take the money from, and raising would hide the overspending.
        var contributions = await ContributionsAsync(householdId, range, ct);
        if (!perMonth.Any(p => p.ClosingLimitCents is not null) && MonthData.MoneyIn(row, contributions) > 0)
        {
            var forCalculator = categories.Select(c => c.ForCalculator).ToList();
            var pool = CategoryBreakdownCalculator.CardPool(forCalculator);
            var spent = CategoryBreakdownCalculator.Calculate(range, forCalculator, transactions).ToDictionary(l => l.CategoryId, l => l.SpentCents);
            categories = categories.Select(c => !pool.Contains(c.Id) && spent[c.Id] > c.LimitCents ? c with { LimitCents = spent[c.Id] } : c).ToList();
        }

        var carried = await CarriedInAsync(householdId, row, ct, depth);
        return new MonthData(row, range, categories, transactions, carried, contributions, contributions.Sum(c => c.ShortfallCents));
    }

    /// <summary>
    /// One household's transactions dated from..to (inclusive) as the budget math sees them. Every screen's numbers load
    /// them here (DECISIONS.md 251), so no two can disagree about what counts.
    /// </summary>
    public static Task<List<BudgetTransaction>> TransactionsAsync(AppDbContext db, Guid householdId, DateOnly from, DateOnly to, CancellationToken ct) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Date >= from && t.Date <= to && t.Account.IncludedInBudget)
            .Select(t => new BudgetTransaction(t.CategoryId, t.Date, t.AmountCents, t.IsTransfer, t.Pending, t.SplitFactor, t.Kind,
                t.Account.Type == AccountType.CreditCard))
            .ToListAsync(ct);

    /// <summary>
    /// "This month only" limits for the budget month starting on <paramref name="monthStart"/>, by category. Debt, Savings
    /// and the payday plan read a line's limit through this (DECISIONS.md 251), so a one-month change shows everywhere the
    /// Budget tab's does.
    /// </summary>
    public static Task<Dictionary<Guid, long>> MonthOnlyLimitsAsync(AppDbContext db, Guid householdId, DateOnly monthStart, CancellationToken ct) =>
        db.BudgetMonthCategories.AsNoTracking()
            .Where(x => x.LimitOverrideCents != null
                        && db.BudgetMonths.Any(m => m.Id == x.BudgetMonthId && m.HouseholdId == householdId && m.StartDate == monthStart))
            .ToDictionaryAsync(x => x.CategoryId, x => x.LimitOverrideCents!.Value, ct);

    /// <summary>The deficit carried into <paramref name="row"/>: the previous month's, frozen or provisional.</summary>
    public async Task<long> CarriedInAsync(Guid householdId, BudgetMonth row, CancellationToken ct, int depth = 0)
    {
        if (row.ClosedUtc is not null)
            return row.CarriedInDeficitCents;
        var previous = await db.BudgetMonths.Where(m => m.HouseholdId == householdId && m.StartDate < row.StartDate)
            .OrderByDescending(m => m.StartDate).FirstOrDefaultAsync(ct);
        if (previous is null)
            return row.CarriedInDeficitCents; // first month on record: its starting value
        if (previous.ClosedUtc is not null)
            return Math.Max(0, -(previous.ClosingNetCents ?? 0));
        if (depth > 24)
            throw new InvalidOperationException("Too many open months in a row.");
        var prev = await LoadAsync(householdId, previous, ct, depth + 1);
        return prev.Close().DeficitCents;
    }

    /// <summary>
    /// Friends' fixed contributions in one budget month, each matched to that friend's own deposits (DECISIONS.md 97, 211).
    /// Only arrangements that had started by then apply, so setting one up never rewrites earlier months.
    /// </summary>
    public async Task<List<ContributionStatus>> ContributionsAsync(Guid householdId, BudgetMonthRange range, CancellationToken ct)
    {
        var arrangements = await db.PartnerArrangements.AsNoTracking()
            .Where(a => a.HouseholdId == householdId && a.StartsOn <= range.End)
            .OrderBy(a => a.CreatedUtc)
            .Select(a => new { a.FriendId, Arrangement = new ContributionArrangement(a.Id, a.CategoryId, a.AmountCents, a.WindowFromDay, a.WindowToDay) })
            .ToListAsync(ct);
        if (arrangements.Count == 0)
            return [];
        var deposits = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.IsPartnerTransfer && t.AmountCents < 0 && !t.IsTransfer
                        && ((t.Date >= range.Start && t.Date <= range.End) || t.CountsForMonthStart == range.Start))
            .Select(t => new { t.FriendId, Deposit = new PartnerDeposit(t.Id, t.Date, -t.AmountCents,
                t.CategorySource == CategorySource.User && t.Kind == TransactionKind.PartnerContribution,
                t.CategorySource == CategorySource.User && t.Kind != TransactionKind.PartnerContribution, t.CountsForMonthStart) })
            .ToListAsync(ct);
        var (_, today, _) = await ContextAsync(householdId, ct);
        var matched = arrangements.GroupBy(a => a.FriendId).SelectMany(g => PartnerContributions.Match(
            g.Select(a => a.Arrangement).ToList(), range,
            deposits.Where(d => d.FriendId == g.Key).Select(d => d.Deposit).ToList(), today)).ToList();
        // In the order they were set up, whoever sends them.
        return arrangements.Select(a => matched.First(s => s.ArrangementId == a.Arrangement.Id)).ToList();
    }

    /// <summary>The most recent month that has ended, with its result (frozen or provisional).</summary>
    public async Task<EndedMonth?> LastEndedAsync(Guid householdId, BudgetMonth current, CancellationToken ct)
    {
        var previous = await db.BudgetMonths.Where(m => m.HouseholdId == householdId && m.StartDate < current.StartDate)
            .OrderByDescending(m => m.StartDate).FirstOrDefaultAsync(ct);
        if (previous is null)
            return null;
        var data = await LoadAsync(householdId, previous, ct);
        var result = data.Close();
        if (previous.ClosedUtc is not null)
            result = result with { ClosingNetCents = previous.ClosingNetCents ?? result.ClosingNetCents,
                SurplusCents = Math.Max(0, previous.ClosingNetCents ?? 0), DeficitCents = Math.Max(0, -(previous.ClosingNetCents ?? 0)) };
        return new EndedMonth(previous, data.Range, result, previous.ClosedUtc is not null, previous.EndDate.AddDays(GraceDays + 1));
    }

    /// <summary>Records the categories and limits a month ended with (once). Its spending stays live until it closes.</summary>
    private async Task FreezeLimitsAsync(Guid householdId, BudgetMonth row, CancellationToken ct)
    {
        if (await db.BudgetMonthCategories.AnyAsync(x => x.BudgetMonthId == row.Id && x.ClosingLimitCents != null, ct))
            return;
        var data = await LoadAsync(householdId, row, ct);
        var existing = await db.BudgetMonthCategories.Where(x => x.BudgetMonthId == row.Id).ToListAsync(ct);
        foreach (var c in data.Categories)
        {
            var snap = existing.SingleOrDefault(x => x.CategoryId == c.Id);
            if (snap is null)
                db.BudgetMonthCategories.Add(snap = new BudgetMonthCategory { Id = Guid.NewGuid(), BudgetMonthId = row.Id, CategoryId = c.Id });
            snap.ClosingLimitCents = c.LimitCents;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Freezes a month that has ended: its result, each category's limit and spending, and its sweep items.</summary>
    public Task CloseAsync(Guid householdId, BudgetMonth row, CancellationToken ct) =>
        InMonthLockAsync(householdId, async () =>
        {
            if (db.Entry(row).State == EntityState.Unchanged)
                await db.Entry(row).ReloadAsync(ct); // closed by another request while this one waited
            await CloseLockedAsync(householdId, row, ct);
        }, ct);

    private async Task CloseLockedAsync(Guid householdId, BudgetMonth row, CancellationToken ct)
    {
        if (row.ClosedUtc is not null)
            return;
        var (household, today, _) = await ContextAsync(householdId, ct);
        if (row.EndDate >= today)
            throw new InvalidOperationException("A month can only be closed after it ends.");

        await FreezeLimitsAsync(householdId, row, ct);
        var data = await LoadAsync(householdId, row, ct);
        var result = data.Close();

        row.CarriedInDeficitCents = data.CarriedInDeficitCents;
        row.ClosingNetCents = result.ClosingNetCents;
        row.ClosedUtc = clock.GetUtcNow().UtcDateTime;

        var existing = await db.BudgetMonthCategories.Where(x => x.BudgetMonthId == row.Id).ToListAsync(ct);
        foreach (var line in result.Categories)
        {
            var snap = existing.SingleOrDefault(x => x.CategoryId == line.CategoryId);
            if (snap is null)
                db.BudgetMonthCategories.Add(snap = new BudgetMonthCategory { Id = Guid.NewGuid(), BudgetMonthId = row.Id, CategoryId = line.CategoryId });
            snap.ClosingLimitCents = line.LimitCents;
            snap.ClosingSpentCents = line.SpentCents;
        }

        var (debt, savings) = await SweepDestinationsAsync(householdId, ct);
        foreach (var item in SweepPlanner.Plan(result.SurplusCents, new SweepPreference(household.SweepTarget, household.SweepSavingsPercent), debt, savings))
        {
            db.SweepTasks.Add(new SweepTask
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, BudgetMonthId = row.Id,
                Kind = item.Kind, AmountCents = item.AmountCents, Destination = item.Destination,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Debt: the debt with the highest APR that's still owed (Avalanche, the owner's strategy; DECISIONS.md 90), else
    /// the card with the largest balance owed while APRs aren't filled in. Savings: the first savings account, else
    /// the first savings category.
    /// </summary>
    public async Task<(string? Debt, string? Savings)> SweepDestinationsAsync(Guid householdId, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.HouseholdId == householdId && a.IncludedInBudget).ToListAsync(ct);
        static string Label(Account a) => a.Mask is null ? a.Name : $"{a.Name} ••{a.Mask}";
        var highestApr = await db.DebtAccounts.AsNoTracking().Include(d => d.Account)
            .Where(d => d.HouseholdId == householdId && !d.IsHidden && d.AprBps != null
                        && (d.Account == null ? d.BalanceCents > 0 : d.Account.IncludedInBudget && d.Account.CurrentBalanceCents < 0))
            .OrderByDescending(d => d.AprBps).ThenByDescending(d => d.BalanceCents).FirstOrDefaultAsync(ct);
        var card = accounts.Where(a => a.Type == AccountType.CreditCard && a.CurrentBalanceCents < 0).OrderBy(a => a.CurrentBalanceCents).FirstOrDefault()
                   ?? accounts.FirstOrDefault(a => a.Type == AccountType.CreditCard);
        var debt = highestApr is not null ? highestApr.Account is { } a ? Label(a) : highestApr.Name
            : card is null ? null : Label(card);
        var savingsAccount = accounts.FirstOrDefault(a => a.Type == AccountType.Savings);
        var savingsCategory = savingsAccount is null
            ? await db.Categories.AsNoTracking().Where(c => c.HouseholdId == householdId && c.Type == CategoryType.SavingsTransfer && !c.IsHidden)
                .OrderBy(c => c.SortOrder).Select(c => c.Name).FirstOrDefaultAsync(ct)
            : null;
        return (debt, savingsAccount is null ? savingsCategory : Label(savingsAccount));
    }

    private static BudgetMonth NewRow(Guid householdId, BudgetMonthRange range, long income, long partner) => new()
    {
        Id = Guid.NewGuid(), HouseholdId = householdId, StartDate = range.Start, EndDate = range.End,
        IncomeExpectedCents = income, PartnerExpectedCents = partner,
    };

    /// <summary>
    /// Two requests may create the same month at once; the unique index makes one win. Returns false when
    /// this request lost, after dropping its copies so the caller can re-read the winner's rows.
    /// </summary>
    private async Task<bool> SaveTolerantAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            foreach (var entry in db.ChangeTracker.Entries<BudgetMonth>().Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
            return false;
        }
    }
}
