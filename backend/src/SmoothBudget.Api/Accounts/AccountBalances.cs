using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;

namespace SmoothBudget.Api.Accounts;

/// <param name="BalanceCents">What's in it now, pending charges already taken off: money in an account is positive, owed on
/// a card is negative.</param>
/// <param name="PendingCents">Charges less deposits still pending on it (positive = money going out).</param>
/// <param name="BankSynced">Its balance comes from the bank; otherwise it's tracked from what you gave and its activity since.</param>
public record AccountBalanceDto(Guid Id, string Name, string Type, string? Mask, bool BankSynced, bool IncludedInBudget, long BalanceCents, long PendingCents);

/// <summary>One account on More → Account nicknames (DECISIONS.md 310): what the bank or the import calls it, and your nickname.</summary>
public record AccountNicknameDto(Guid Id, string Name, string? Mask, string Type, string? Nickname);

public record NicknameRequest(string? Nickname);

/// <summary>Debit's Cash card (DECISIONS.md 257): the money in your checking accounts now, and at the end of each day before.</summary>
public record CashDto(long TotalCents, IReadOnlyList<Core.Budgeting.BalancePoint> History);

/// <summary>
/// The Accounts cards on Debit, Debt and Savings (DECISIONS.md 256): each account of that kind with its balance now. A bank's
/// current balance leaves pending activity out (DECISIONS.md 255), so that's taken off here; an account the bank doesn't
/// sync is the balance last given, moved by every transaction since (DECISIONS.md 189), pending ones included.
/// </summary>
public static class AccountBalances
{
    public const int TransactionsShown = 200;
    /// <summary>How far back Debit's cash chart goes: about three months of paydays and bills.</summary>
    public const int CashDays = 90;

    public static AccountType? TypeOf(string? kind) => kind?.ToLowerInvariant() switch
    {
        "checking" or "debit" => AccountType.Checking,
        "savings" => AccountType.Savings,
        "card" or "creditcard" or "debt" => AccountType.CreditCard,
        _ => null,
    };

    public static async Task<List<AccountBalanceDto>> ListAsync(AppDbContext db, Guid householdId, AccountType type, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.HouseholdId == householdId && a.Type == type)
            .OrderByDescending(a => a.IncludedInBudget).ThenBy(a => a.Name).ToListAsync(ct);
        var ids = accounts.Select(a => a.Id).ToList();
        var pending = await db.Transactions.AsNoTracking().Where(t => ids.Contains(t.AccountId) && t.Pending)
            .GroupBy(t => t.AccountId).Select(g => new { g.Key, Cents = g.Sum(t => t.AmountCents) }).ToDictionaryAsync(x => x.Key, x => x.Cents, ct);
        var unlinked = accounts.Where(a => a.PlaidAccountId is null).Select(a => a.Id).ToList();
        var activity = await db.Transactions.AsNoTracking().Where(t => unlinked.Contains(t.AccountId))
            .Select(t => new { t.AccountId, t.Date, t.AmountCents }).ToListAsync(ct);
        return accounts.Select(a =>
        {
            var pend = pending.GetValueOrDefault(a.Id);
            var balance = a.PlaidAccountId is not null
                ? a.CurrentBalanceCents - pend
                : a.CurrentBalanceCents - activity.Where(t => t.AccountId == a.Id && (a.BalanceAsOf is null || t.Date > a.BalanceAsOf)).Sum(t => t.AmountCents);
            return new AccountBalanceDto(a.Id, AccountNames.Short(a), a.Type.ToString(), a.Nickname is null ? a.Mask : null, a.PlaidAccountId is not null, a.IncludedInBudget, balance, pend);
        }).ToList();
    }

    /// <summary>
    /// The cash in every checking account (the same balances as Debit's Accounts card), and each day's closing total for the
    /// last <see cref="CashDays"/> days, worked back from now through each account's transactions.
    /// </summary>
    public static async Task<CashDto> CashAsync(AppDbContext db, Guid householdId, DateOnly today, CancellationToken ct)
    {
        var accounts = await ListAsync(db, householdId, AccountType.Checking, ct);
        var ids = accounts.Select(a => a.Id).ToList();
        var from = today.AddDays(-CashDays);
        var txns = await db.Transactions.AsNoTracking().Where(t => ids.Contains(t.AccountId) && t.Date > from)
            .Select(t => new { t.Date, t.AmountCents }).ToListAsync(ct);
        var total = accounts.Sum(a => a.BalanceCents);
        // Money out is positive, so a day's closing balance is now plus everything that went out after it, less what came in.
        var after = txns.GroupBy(t => t.Date).ToDictionary(g => g.Key, g => g.Sum(t => t.AmountCents));
        var points = new List<Core.Budgeting.BalancePoint>();
        var balance = total;
        for (var d = today; d >= from; d = d.AddDays(-1))
        {
            points.Add(new Core.Budgeting.BalancePoint(d, balance));
            balance += after.GetValueOrDefault(d); // undo that day's activity to get the day before's close
        }
        points.Reverse();
        return new CashDto(total, points);
    }

    public static void MapAccountBalanceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/accounts").RequireAuthorization();

        g.MapGet("/cash", async (HttpContext ctx, AppDbContext db, Budget.BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var (_, today, _) = await months.ContextAsync(householdId, ct);
            return await CashAsync(db, householdId, today, ct);
        });

        // More → Account nicknames (DECISIONS.md 310): every account, with what it's called now.
        g.MapGet("/nicknames", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            return await db.Accounts.AsNoTracking().Where(a => a.HouseholdId == householdId)
                .OrderBy(a => a.Type).ThenByDescending(a => a.IncludedInBudget).ThenBy(a => a.Name)
                .Select(a => new AccountNicknameDto(a.Id, a.Name, a.Mask, a.Type.ToString(), a.Nickname)).ToListAsync(ct);
        });

        // Give an account a nickname, or clear it (empty) to go back to its name from the bank.
        g.MapPut("/{id:guid}/nickname", async (Guid id, NicknameRequest req, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var a = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (a is null) return Results.NotFound();
            var nick = req.Nickname?.Trim();
            if (nick is { Length: > AccountNames.MaxNickname })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["nickname"] = [$"Keep it to {AccountNames.MaxNickname} characters."] });
            a.Nickname = string.IsNullOrEmpty(nick) ? null : nick;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new AccountNicknameDto(a.Id, a.Name, a.Mask, a.Type.ToString(), a.Nickname));
        });

        g.MapGet("/balances", async (string? type, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            TypeOf(type) is { } t
                ? Results.Ok(await ListAsync(db, ctx.User.HouseholdId(), t, ct))
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["Use checking, savings or card."] }));

        // Every transaction on those accounts (or one of them), newest first.
        g.MapGet("/transactions", async (string? type, Guid? accountId, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            if (TypeOf(type) is not { } t)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["Use checking, savings or card."] });
            var householdId = ctx.User.HouseholdId();
            var q = db.Transactions.Where(x => x.HouseholdId == householdId && x.Account.Type == t);
            if (accountId is { } id) q = q.Where(x => x.AccountId == id);
            return Results.Ok(await Transactions.TransactionEndpoints.Project(q).Take(TransactionsShown).ToListAsync(ct));
        });
    }
}
