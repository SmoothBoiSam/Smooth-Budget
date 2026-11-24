using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Savings;
using SmoothBudget.Api.Transactions;

namespace SmoothBudget.Api.Accounts;

/// <summary>One account on More → Accounts (DECISIONS.md 318).</summary>
/// <param name="Label">What it's called everywhere: its nickname, or its name with the last four digits.</param>
/// <param name="BankId">Its bank connection (Plaid), while it has one.</param>
/// <param name="BalanceCents">As on its Accounts card: money in it is positive, owed on a card is negative.</param>
/// <param name="ReplacedBy">For a closed account, the one that took over from it.</param>
/// <param name="Attention">Something it needs from you, in a sentence: its bank wants you to sign in again, or it has no balance yet.</param>
/// <param name="SavingsPercent">A savings account's share of each month's savings, as Payment settings splits it.</param>
/// <param name="Goals">The savings goals kept in it.</param>
/// <param name="AprBps">A card's APR, minimum and due day, from Debt.</param>
public record ManagedAccountDto(Guid Id, string Label, string Name, string? Mask, string? Nickname, string Type, string Source,
    bool BankSynced, Guid? BankId, string? BankName, long BalanceCents, DateOnly? BalanceAsOf, DateOnly? ClosedOn, Guid? ReplacedById,
    string? ReplacedBy, int TransactionCount, DateOnly? FirstDate, DateOnly? LastDate,
    bool IncludedInBudget = true, DateTime? LastSyncedUtc = null, string? Attention = null, int SavingsPercent = 0,
    IReadOnlyList<string>? Goals = null, int? AprBps = null, long? MinimumCents = null, int? DueDay = null);

public record AccountChoiceDto(Guid Id, string Label);

/// <summary>
/// What closing or deleting an account would touch, for the screen that asks first.
/// </summary>
/// <param name="MoveTo">Your other open accounts of the same kind: where its goals, savings share and leftover moves can go.</param>
/// <param name="LastOnBank">It's the last open account on its bank connection, so you might disconnect the bank too.</param>
public record AccountImpactDto(long BalanceCents, int TransactionCount, DateOnly? FirstDate, int ImportCount, IReadOnlyList<string> Goals,
    int SavingsPercent, int OpenSweeps, IReadOnlyList<AccountChoiceDto> MoveTo, Guid? BankId, string? BankName, bool LastOnBank);

public record CloseAccountRequest(Guid? MoveToId);
public record AccountTypeRequest(AccountType Type);
public record CountInBudgetRequest(bool Included);

/// <summary>
/// More → Accounts (DECISIONS.md 318): every account in one list, and closing, reopening or deleting one.
/// Closing keeps everything that happened; deleting takes its transactions (and so its part of past months) with it.
/// Either way what pointed at it (savings goals, its share of each month's savings, a leftover move still to make) moves to
/// the account you pick, so nothing about now is left pointing at an account that's gone.
/// </summary>
public static class AccountManagement
{
    public const long MaxCents = 100_000_000_00;

    public static void MapAccountManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/accounts").RequireAuthorization();

        g.MapGet("/manage", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var accounts = await db.Accounts.AsNoTracking().Include(a => a.PlaidItem).Where(a => a.HouseholdId == householdId).ToListAsync(ct);
            var balances = await AccountBalances.BalancesAsync(db, accounts, ct);
            var ids = accounts.Select(a => a.Id).ToList();
            var activity = await db.Transactions.AsNoTracking().Where(t => ids.Contains(t.AccountId)).GroupBy(t => t.AccountId)
                .Select(x => new { x.Key, Count = x.Count(), First = x.Min(t => t.Date), Last = x.Max(t => t.Date) }).ToDictionaryAsync(x => x.Key, ct);
            var goals = await db.SavingsGoals.AsNoTracking().Where(x => x.HouseholdId == householdId && x.AccountId != null)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => new { AccountId = x.AccountId!.Value, x.Name }).ToListAsync(ct);
            var debts = await db.DebtAccounts.AsNoTracking().Where(d => d.HouseholdId == householdId && d.AccountId != null)
                .Select(d => new { AccountId = d.AccountId!.Value, d.AprBps, d.MinimumPaymentCents, d.DueDay, Known = d.TypedOn != null || d.BalanceCents != 0 }).ToListAsync(ct);
            var shares = SavingsSplit.Shares(accounts).ToDictionary(s => s.Account.Id, s => s.Percent);
            // What it needs from you (DECISIONS.md 319): only for an account that's open.
            string? Attention(Account a)
            {
                if (a.ClosedOn is not null) return null;
                if (a.PlaidItem is { Status: PlaidItemStatus.LoginRequired or PlaidItemStatus.Error } item)
                    return $"{item.InstitutionName ?? "The bank"} needs you to sign in again.";
                if (a.PlaidAccountId is not null) return null;
                // Known from a file (its newest line), from you, or (a card) from what you entered on Debt.
                if (a.BalanceAsOf is not null || a.CurrentBalanceCents != 0) return null;
                return a.Type == AccountType.CreditCard
                    ? debts.Any(d => d.AccountId == a.Id && d.Known) ? null : "No balance yet: enter what it owes on Debt."
                    : "No balance yet: add one so the pay period tasks can check themselves off.";
            }
            return accounts
                .OrderBy(a => a.ClosedOn is null ? 0 : 1).ThenBy(a => a.Type).ThenBy(a => a.CreatedUtc)
                .Select(a =>
                {
                    var act = activity.GetValueOrDefault(a.Id);
                    var into = a.ClosedOn is null ? null : AccountNames.Successor(accounts, a);
                    return new ManagedAccountDto(a.Id, AccountNames.Label(a), a.Name, a.Mask, a.Nickname, a.Type.ToString(), a.Source.ToString(),
                        a.PlaidAccountId is not null, a.PlaidItemId, a.PlaidItem?.InstitutionName, balances[a.Id].Balance, a.BalanceAsOf, a.ClosedOn,
                        a.ReplacedById, into is not null && into.Id != a.Id ? AccountNames.Label(into) : null,
                        act?.Count ?? 0, act?.First, act?.Last,
                        a.IncludedInBudget, a.PlaidItem?.LastSyncedUtc, Attention(a), shares.GetValueOrDefault(a.Id),
                        goals.Where(x => x.AccountId == a.Id).Select(x => x.Name).ToList(),
                        debts.FirstOrDefault(d => d.AccountId == a.Id)?.AprBps, debts.FirstOrDefault(d => d.AccountId == a.Id)?.MinimumPaymentCents,
                        debts.FirstOrDefault(d => d.AccountId == a.Id)?.DueDay);
                }).ToList();
        });

        g.MapGet("/{id:guid}/impact", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var accounts = await db.Accounts.AsNoTracking().Include(a => a.PlaidItem).Where(a => a.HouseholdId == householdId).ToListAsync(ct);
            if (accounts.FirstOrDefault(a => a.Id == id) is not { } a) return Results.NotFound();
            var balance = (await AccountBalances.BalancesAsync(db, [a], ct))[a.Id].Balance;
            var txns = await db.Transactions.AsNoTracking().Where(t => t.AccountId == id)
                .GroupBy(t => 1).Select(x => new { Count = x.Count(), First = x.Min(t => t.Date) }).FirstOrDefaultAsync(ct);
            var imports = await db.ImportBatches.CountAsync(b => b.AccountId == id, ct);
            var goals = await db.SavingsGoals.AsNoTracking().Where(x => x.AccountId == id).OrderBy(x => x.SortOrder).Select(x => x.Name).ToListAsync(ct);
            var sweeps = (await OpenSweepsAsync(db, householdId, ct)).Count(s => Names(a, s.Destination));
            var moveTo = accounts.Where(x => x.Id != id && x.ClosedOn is null && x.Type == a.Type).OrderBy(x => x.CreatedUtc)
                .Select(x => new AccountChoiceDto(x.Id, AccountNames.Label(x))).ToList();
            var lastOnBank = a.PlaidItemId is { } bank && !accounts.Any(x => x.Id != id && x.PlaidItemId == bank && x.ClosedOn is null);
            return Results.Ok(new AccountImpactDto(balance, txns?.Count ?? 0, txns?.First, imports, goals, a.SavingsPercent ?? 0, sweeps, moveTo,
                a.PlaidItemId, a.PlaidItem?.InstitutionName, lastOnBank));
        });

        g.MapPost("/{id:guid}/close", async (Guid id, CloseAccountRequest req, HttpContext ctx, AppDbContext db, BudgetMonthService months,
            SavingsService savings, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var a = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (a is null) return Results.NotFound();
            if (a.ClosedOn is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["id"] = ["It's closed already."] });
            var (problem, into) = await ReplacementAsync(db, a, req.MoveToId, ct);
            if (problem is not null) return problem;
            await CloseAsync(db, months, savings, a, into, ct);
            return Results.NoContent();
        });

        g.MapPost("/{id:guid}/reopen", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var a = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == ctx.User.HouseholdId(), ct);
            if (a is null) return Results.NotFound();
            (a.ClosedOn, a.ReplacedById) = (null, null);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Its transactions (and its uploads) go with it: past months lose what it spent and earned. Closing keeps them.
        g.MapDelete("/{id:guid}", async (Guid id, Guid? moveToId, HttpContext ctx, AppDbContext db, BudgetMonthService months,
            SavingsService savings, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var a = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (a is null) return Results.NotFound();
            var (problem, into) = await ReplacementAsync(db, a, moveToId, ct);
            if (problem is not null) return problem;
            await DeleteAsync(db, months, savings, a, into, ct);
            return Results.NoContent();
        });

        // Its kind, when the bank or the import guessed it wrong (DECISIONS.md 319). Checking and savings can trade places: the
        // transfers to and from it are filed again, since money into savings is saving and money into checking isn't. A card
        // can't become a bank account or the other way round: its amounts read the other way round.
        g.MapPut("/{id:guid}/type", async (Guid id, AccountTypeRequest req, HttpContext ctx, AppDbContext db, SavingsService savings, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var a = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (a is null) return Results.NotFound();
            if (!Enum.IsDefined(req.Type))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["Unknown account type."] });
            if (req.Type == a.Type) return Results.NoContent();
            if (a.Type == AccountType.CreditCard || req.Type == AccountType.CreditCard)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] =
                    ["A card's amounts read the other way round from a bank account's, so one can't become the other. Add the right account and delete this one."] });
            // Savings no more: its goals keep what they've saved, tracked by hand, and its share goes to your other savings.
            if (a.Type == AccountType.Savings) await MoveDependentsAsync(db, savings, a, null, ct);
            a.Type = req.Type;
            await db.SaveChangesAsync(ct);
            await RefileTransfersAsync(db, householdId, a.Id, ct);
            return Results.NoContent();
        });

        // "Count in budget" (DECISIONS.md 319): left out, it's an account you see but don't budget with. Every month leaves it
        // out, past ones too, and so do the payday tasks, Debt and Savings.
        g.MapPut("/{id:guid}/budget", async (Guid id, CountInBudgetRequest req, HttpContext ctx, AppDbContext db, SavingsService savings, CancellationToken ct) =>
        {
            var a = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == ctx.User.HouseholdId(), ct);
            if (a is null) return Results.NotFound();
            if (a.IncludedInBudget == req.Included) return Results.NoContent();
            // Savings that stops counting: its goals keep what they've saved, tracked by hand, and its share goes to your other savings.
            if (!req.Included && a.Type == AccountType.Savings) await MoveDependentsAsync(db, savings, a, null, ct);
            a.IncludedInBudget = req.Included;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // The balance of an account the bank doesn't sync, as of today (DECISIONS.md 189): only later activity moves it.
        g.MapPut("/{id:guid}/balance", async (Guid id, BalanceRequest req, HttpContext ctx, AppDbContext db, BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var a = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (a is null) return Results.NotFound();
            if (a.PlaidAccountId is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["balanceCents"] = ["This balance comes from your bank."] });
            if (a.Type == AccountType.CreditCard)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["balanceCents"] = ["Enter what a card owes on Debt."] });
            if (req.BalanceCents is < -MaxCents or > MaxCents)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["balanceCents"] = ["Enter the balance in dollars."] });
            var (_, today, _) = await months.ContextAsync(householdId, ct);
            (a.CurrentBalanceCents, a.BalanceAsOf) = (req.BalanceCents, today);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    /// <summary>Closes an account, moving what pointed at it to <paramref name="into"/> (see <see cref="MoveDependentsAsync"/>).</summary>
    public static async Task CloseAsync(AppDbContext db, BudgetMonthService months, SavingsService savings, Account a, Account? into, CancellationToken ct)
    {
        var (_, today, _) = await months.ContextAsync(a.HouseholdId, ct);
        var sweeps = await MoveDependentsAsync(db, savings, a, into, ct);
        (a.ClosedOn, a.ReplacedById) = (today, into?.Id);
        await db.SaveChangesAsync(ct);
        await RedirectSweepsAsync(db, months, a.HouseholdId, sweeps, into, ct);
    }

    /// <summary>Deletes an account and its transactions, moving what pointed at it to <paramref name="into"/> first.</summary>
    public static async Task DeleteAsync(AppDbContext db, BudgetMonthService months, SavingsService savings, Account a, Account? into, CancellationToken ct)
    {
        var sweeps = await MoveDependentsAsync(db, savings, a, into, ct);
        // A closed account this one had taken over from now follows what takes over from this one.
        await db.Accounts.Where(x => x.HouseholdId == a.HouseholdId && x.ReplacedById == a.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReplacedById, into?.Id), ct);
        db.Accounts.Remove(a);
        await db.SaveChangesAsync(ct);
        await RedirectSweepsAsync(db, months, a.HouseholdId, sweeps, into, ct);
    }

    /// <summary>
    /// Disconnecting a bank (DECISIONS.md 319): its accounts kept as closed history, or deleted with their transactions.
    /// </summary>
    public static async Task EndAllAsync(AppDbContext db, BudgetMonthService months, SavingsService savings, IReadOnlyList<Guid> ids, bool delete, CancellationToken ct)
    {
        foreach (var id in ids)
        {
            if (await db.Accounts.SingleOrDefaultAsync(x => x.Id == id, ct) is not { } a) continue;
            if (delete) await DeleteAsync(db, months, savings, a, null, ct);
            else if (a.ClosedOn is null) await CloseAsync(db, months, savings, a, null, ct);
        }
    }

    /// <summary>Files again every transfer to or from this account, as its kind now says (savings or not).</summary>
    private static async Task RefileTransfersAsync(AppDbContext db, Guid householdId, Guid id, CancellationToken ct)
    {
        var touching = await db.Transactions
            .Where(t => t.HouseholdId == householdId && t.IsTransfer && t.TransferAccountId != null && (t.TransferAccountId == id || t.AccountId == id))
            .ToListAsync(ct);
        var filing = new TransferFiling(db, householdId);
        foreach (var t in touching)
            await filing.FileAsync(t, t.TransferAccountId, null, null, create: false, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The account picked to take over: one of yours, open, of the same kind, and not this one.</summary>
    private static async Task<(IResult? Problem, Account? Into)> ReplacementAsync(AppDbContext db, Account a, Guid? moveToId, CancellationToken ct)
    {
        if (moveToId is not { } to) return (null, null);
        var into = await db.Accounts.SingleOrDefaultAsync(x => x.Id == to && x.HouseholdId == a.HouseholdId, ct);
        if (into is null || into.Id == a.Id || into.ClosedOn is not null || into.Type != a.Type)
            return (Results.ValidationProblem(new Dictionary<string, string[]> { ["moveToId"] = ["Pick another open account of the same kind."] }), null);
        return (null, into);
    }

    /// <summary>
    /// Moves what points at an account that's closing or being deleted: its savings goals and its share of each month's
    /// savings go to <paramref name="into"/> (goals with nowhere to go keep what they had saved, tracked by hand; the share
    /// goes to your oldest other savings account). Returns the leftover moves still to make that name it, for
    /// <see cref="RedirectSweepsAsync"/> once the account is gone from the choices.
    /// </summary>
    private static async Task<List<SweepTask>> MoveDependentsAsync(AppDbContext db, SavingsService savings, Account a, Account? into, CancellationToken ct)
    {
        var goals = await db.SavingsGoals.Where(x => x.AccountId == a.Id).ToListAsync(ct);
        if (goals.Count > 0 && into is null)
        {
            var saved = (await savings.SummaryAsync(a.HouseholdId, ct)).Goals.ToDictionary(x => x.Id, x => x.SavedCents);
            foreach (var goal in goals)
                (goal.AccountId, goal.ManualSavedCents) = (null, saved.GetValueOrDefault(goal.Id));
        }
        foreach (var goal in goals.Where(_ => into is not null))
            goal.AccountId = into!.Id;

        if (a.SavingsPercent is > 0 and var pct)
        {
            var to = into ?? await db.Accounts.Where(x => x.HouseholdId == a.HouseholdId && x.Id != a.Id && x.Type == AccountType.Savings
                                                          && x.IncludedInBudget && x.ClosedOn == null)
                .OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
            if (to is not null) to.SavingsPercent = (to.SavingsPercent ?? 0) + pct;
            a.SavingsPercent = 0;
        }
        return (await OpenSweepsAsync(db, a.HouseholdId, ct)).Where(s => Names(a, s.Destination)).ToList();
    }

    /// <summary>A leftover move still to make goes to the account that took over, or to where a new one would go now.</summary>
    private static async Task RedirectSweepsAsync(AppDbContext db, BudgetMonthService months, Guid householdId, List<SweepTask> sweeps, Account? into, CancellationToken ct)
    {
        if (sweeps.Count == 0) return;
        var (debt, saving) = into is null ? await months.SweepDestinationsAsync(householdId, ct) : (null, null);
        foreach (var s in sweeps)
            if ((into is not null ? AccountNames.Imported(into) : s.Kind == Core.Budgeting.SweepKind.Debt ? debt : saving) is { } to)
                s.Destination = to;
        await db.SaveChangesAsync(ct);
    }

    private static Task<List<SweepTask>> OpenSweepsAsync(AppDbContext db, Guid householdId, CancellationToken ct) =>
        db.SweepTasks.Where(s => s.HouseholdId == householdId && s.DoneUtc == null).ToListAsync(ct);

    /// <summary>A label saved earlier names this account (as <see cref="AccountNames.Current"/> matches them).</summary>
    private static bool Names(Account a, string saved) => AccountNames.Imported(a) == saved || AccountNames.Label(a) == saved || a.Name == saved;
}
