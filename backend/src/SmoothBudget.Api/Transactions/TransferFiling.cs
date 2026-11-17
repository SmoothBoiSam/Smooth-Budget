using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Transactions;

/// <summary>
/// Files a transfer between your accounts (DECISIONS.md 259), whoever decided it: you in Review, transfer detection, or the
/// app finding the other half of one you confirmed. Money out to a card or a loan you track is a payment toward that debt
/// (its debt line); money out to savings is a savings transfer (the line for its goal); anything else, and every side
/// money arrives on, counts nowhere. Loaded once per household and reused for many rows.
/// </summary>
public sealed class TransferFiling(AppDbContext db, Guid householdId)
{
    /// <summary>How long after (or before) a transfer you confirmed its other half can land and still be matched to it.</summary>
    public const int OtherHalfDays = 10;

    private Dictionary<Guid, Account>? accounts;
    private List<DebtAccount>? debts;
    private List<SavingsGoal>? goals;
    private List<Category>? lines;

    private async Task LoadAsync(CancellationToken ct)
    {
        if (accounts is not null) return;
        accounts = await db.Accounts.Where(a => a.HouseholdId == householdId).ToDictionaryAsync(a => a.Id, ct);
        debts = await db.DebtAccounts.Where(d => d.HouseholdId == householdId).ToListAsync(ct);
        goals = await db.SavingsGoals.Where(g => g.HouseholdId == householdId).OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToListAsync(ct);
        lines = await db.Categories.Where(c => c.HouseholdId == householdId && (c.Type == CategoryType.DebtPayment || c.Type == CategoryType.SavingsTransfer))
            .OrderBy(c => c.IsHidden).ThenBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<Account?> AccountAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(ct);
        return accounts!.GetValueOrDefault(id);
    }

    /// <summary>"Chase Checking ••1234", or what a loan or an outside account is called.</summary>
    public static string Label(string name, string? mask) => mask is null ? name : $"{name} ••{mask}";

    /// <summary>
    /// Makes <paramref name="t"/> a transfer with <paramref name="other"/> (null: an account not in the app, or the hand-tracked
    /// loan <paramref name="loanId"/>). <paramref name="goalId"/> picks the savings goal for money out to savings. With
    /// <paramref name="create"/>, a missing debt or savings line is made; detection never makes one. Returns what the other side is called.
    /// </summary>
    public async Task<string> FileAsync(Transaction t, Guid? other, Guid? loanId, Guid? goalId, bool create, CancellationToken ct)
    {
        await LoadAsync(ct);
        var to = other is { } id ? accounts!.GetValueOrDefault(id) : null;
        var own = accounts!.GetValueOrDefault(t.AccountId);
        var loan = loanId is { } l ? debts!.FirstOrDefault(d => d.Id == l) : null;
        (t.IsTransfer, t.TransferAccountId, t.IncomeType, t.FriendId, t.IsPartnerTransfer, t.SuggestedCategoryId, t.CountsForMonthStart) =
            (true, to?.Id, null, null, false, null, null);
        (t.CategoryId, t.DebtAccountId) = (null, null);
        var name = to is not null ? Label(to.Name, to.Mask) : loan?.Name ?? "an account not in Smooth Budget";

        if (t.AmountCents > 0)
        {
            var debt = loan ?? (to is { Type: AccountType.CreditCard } ? debts!.FirstOrDefault(d => d.AccountId == to.Id) : null);
            if (debt is not null || to is { Type: AccountType.CreditCard })
            {
                t.Kind = TransactionKind.CardPayment;
                t.DebtAccountId = debt?.Id;
                t.CategoryId = (await LineAsync(CategoryType.DebtPayment, debt is null ? [] : [debt.Name], create, ct))?.Id;
                return name;
            }
            t.Kind = TransactionKind.Transfer;
            if (to is { Type: AccountType.Savings })
            {
                var ofAccount = goals!.Where(g => g.AccountId == to.Id).ToList();
                var goal = goalId is { } gid ? goals!.FirstOrDefault(g => g.Id == gid) : ofAccount.Count == 1 ? ofAccount[0] : null;
                // A goal tied to that account counts as that account's (DECISIONS.md 228).
                List<string> names = goal is not null ? [goal.Name] : [to.Name, .. ofAccount.Select(g => g.Name)];
                t.CategoryId = (await LineAsync(CategoryType.SavingsTransfer, names, create, ct))?.Id;
            }
            return name;
        }
        t.Kind = own is { Type: AccountType.CreditCard } ? TransactionKind.CardPayment : TransactionKind.Transfer;
        return name;
    }

    /// <summary>The debt or savings line named after it, else your first one, else (when allowed) a new one.</summary>
    private async Task<Category?> LineAsync(CategoryType type, IReadOnlyList<string> names, bool create, CancellationToken ct)
    {
        var ofType = lines!.Where(c => c.Type == type).ToList();
        var cat = names.Select(n => ofType.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(c => c is not null)
                  ?? ofType.FirstOrDefault(c => !c.IsHidden);
        if (cat is null && create)
        {
            var baseName = type == CategoryType.DebtPayment ? "Debt payments" : "Savings";
            var all = await db.Categories.Where(c => c.HouseholdId == householdId).Select(c => new { c.Name, c.SortOrder }).ToListAsync(ct);
            var name = baseName;
            for (var n = 2; all.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
                name = $"{baseName} ({n})";
            db.Categories.Add(cat = new Category { Id = Guid.NewGuid(), HouseholdId = householdId, Name = name, Type = type, SortOrder = all.Count == 0 ? 0 : all.Max(c => c.SortOrder) + 1 });
            lines!.Add(cat);
        }
        if (cat is not null && create) cat.IsHidden = false;
        return cat;
    }

    /// <summary>
    /// It's no longer a transfer with what it was paired with: the other half goes back to Review, unless you confirmed it
    /// yourself, so nothing is left counted as half of a transfer that isn't one.
    /// </summary>
    public static async Task UnpairAsync(AppDbContext db, Transaction t, CancellationToken ct)
    {
        if (t.TransferGroupId is not { } group) return;
        var others = await db.Transactions.Where(x => x.HouseholdId == t.HouseholdId && x.TransferGroupId == group && x.Id != t.Id).ToListAsync(ct);
        foreach (var o in others)
        {
            o.TransferGroupId = null;
            if (o.CategorySource is CategorySource.User or CategorySource.BestGuess) continue;
            (o.NeedsReview, o.CategorySource, o.CategoryUnsure) = (true, CategorySource.None, true);
            o.CategoryReason = "Its other half isn't a transfer with it anymore.";
        }
        t.TransferGroupId = null;
    }

    /// <summary>
    /// The other half of every transfer you confirmed (DECISIONS.md 259): the same amount the other way, on the account you
    /// said, within <see cref="OtherHalfDays"/> days. Found, it's filed as the other half and skips Review. Not here yet, the
    /// app keeps looking each time new transactions come in. Never takes a row you settled yourself.
    /// Returns how many it filed.
    /// </summary>
    public static async Task<int> CompleteAsync(AppDbContext db, Guid householdId, CancellationToken ct, Guid? only = null)
    {
        var confirmed = await db.Transactions
            .Where(t => t.HouseholdId == householdId && t.IsTransfer && t.CategorySource == CategorySource.User && t.TransferAccountId != null
                        && (only == null || t.Id == only)
                        && !db.Transactions.Any(o => o.TransferGroupId != null && o.TransferGroupId == t.TransferGroupId && o.Id != t.Id
                                                     && o.AccountId == t.TransferAccountId && !o.NeedsReview))
            .ToListAsync(ct);
        if (confirmed.Count == 0) return 0;
        var filing = new TransferFiling(db, householdId);
        var taken = new HashSet<Guid>();
        var done = 0;
        foreach (var c in confirmed.OrderBy(c => c.Date))
        {
            var (from, to) = (c.Date.AddDays(-OtherHalfDays), c.Date.AddDays(OtherHalfDays));
            var group = c.TransferGroupId;
            // Not one that's already the other half of a different transfer you confirmed.
            var candidates = await db.Transactions
                .Where(o => o.HouseholdId == householdId && o.AccountId == c.TransferAccountId && o.AmountCents == -c.AmountCents
                            && o.Date >= from && o.Date <= to
                            && o.CategorySource != CategorySource.User && o.CategorySource != CategorySource.BestGuess
                            && !(o.TransferGroupId != null && o.TransferGroupId != group
                                 && db.Transactions.Any(x => x.TransferGroupId == o.TransferGroupId && x.Id != o.Id && x.CategorySource == CategorySource.User)))
                .ToListAsync(ct);
            var pick = candidates.Where(o => !taken.Contains(o.Id))
                .OrderBy(o => c.TransferGroupId != null && o.TransferGroupId == c.TransferGroupId ? 0 : 1)
                .ThenBy(o => Math.Abs(o.Date.DayNumber - c.Date.DayNumber)).ThenBy(o => o.Date).FirstOrDefault();
            if (pick is null) continue;
            if (pick.TransferGroupId is { } oldGroup && oldGroup != c.TransferGroupId)
                await UnpairAsync(db, pick, ct); // it was paired with something else: that one goes back to Review
            var own = await filing.AccountAsync(c.AccountId, ct);
            await filing.FileAsync(pick, c.AccountId, null, null, create: false, ct);
            c.TransferGroupId ??= Guid.NewGuid();
            pick.TransferGroupId = c.TransferGroupId;
            (pick.CategorySource, pick.NeedsReview, pick.CategoryUnsure) = (CategorySource.Transfer, false, false);
            var where = own is null ? "" : $" {(pick.AmountCents > 0 ? "to" : "from")} {Label(own.Name, own.Mask)}";
            pick.CategoryReason = $"The other half of your transfer{where} on {c.Date:MMM d}.";
            taken.Add(pick.Id);
            done++;
        }
        await db.SaveChangesAsync(ct);
        return done;
    }
}
