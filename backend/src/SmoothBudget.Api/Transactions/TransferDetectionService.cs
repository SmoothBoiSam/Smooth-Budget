using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Debt;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Transactions;

/// <summary>
/// Runs <see cref="TransferDetector"/> over a date window of one household's transactions and saves the result. A newly
/// found transfer is only the most likely answer (DECISIONS.md 259): it waits in Review like everything else, filed as a
/// transfer with the account its other half is on, until you confirm one half. Then the other half follows on its own
/// (<see cref="TransferFiling.CompleteAsync"/>), which this runs at the end too. A transfer with only its own side here is
/// filed with the account its description names, when it names one (DECISIONS.md 302).
/// </summary>
public partial class TransferDetectionService(AppDbContext db)
{
    /// <summary>Looks this far around imported dates so a payment can pair with a row from an earlier import.</summary>
    public const int PaddingDays = TransferDetector.MatchWindowDays + 2;

    public async Task RunAsync(Guid householdId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var start = from.AddDays(-PaddingDays);
        var end = to.AddDays(PaddingDays);
        var txns = await db.Transactions
            .Include(t => t.Account)
            .Where(t => t.HouseholdId == householdId && t.Date >= start && t.Date <= end)
            .ToListAsync(ct);
        if (txns.Count == 0)
            return;

        // Settled by you (or your best guess) is never changed. Still waiting is what can look like a transfer by amount alone.
        static bool Locked(Transaction t) => t.CategorySource is CategorySource.User or CategorySource.BestGuess;
        var candidates = txns.Select(t => new TransferCandidate(
            t.Id, t.AccountId, ToKind(t.Account.Type), t.Date, t.AmountCents, t.Kind,
            $"{t.Description} {t.Merchant}", t.TransferGroupId, Locked(t),
            Waiting: !Locked(t) && !t.IsTransfer && (t.NeedsReview || TransactionRules.NeedsCategory(t.CategoryId is not null, t.IsTransfer, t.Kind, t.AmountCents)))).ToList();

        var filing = new TransferFiling(db, householdId);
        var byId = txns.ToDictionary(t => t.Id);
        List<Named>? accounts = null;
        foreach (var d in TransferDetector.Detect(candidates))
        {
            var t = byId[d.Id];
            var wasTransfer = t.IsTransfer;
            var waiting = t.NeedsReview;
            var other = d.CounterpartAccountId;
            if (other is null)
                other = NamedIn(t, d.Role, accounts ??= await AccountsAsync(householdId, ct));
            if (wasTransfer && !waiting)
            {
                // Settled already: its filing stays as it is, it only learns which account its other half is on.
                t.TransferAccountId ??= other;
                t.TransferGroupId = d.GroupId ?? t.TransferGroupId;
                continue;
            }
            var name = await filing.FileAsync(t, other, null, null, create: false, ct);
            if (d.Role is TransferRole.CardPaymentSent or TransferRole.CardPaymentReceived)
                t.Kind = TransactionKind.CardPayment;
            t.TransferGroupId = d.GroupId ?? t.TransferGroupId;
            t.CategorySource = t.CategoryId is null ? CategorySource.None : CategorySource.Transfer;
            t.CategoryUnsure = false;
            // New: the most likely answer, waiting for you. Already a transfer before: left as it was settled.
            t.NeedsReview = wasTransfer ? waiting : true;
            if (!wasTransfer || t.CategoryReason is null)
                t.CategoryReason = other is null
                    ? "Looks like a transfer between your accounts."
                    : d.CounterpartAccountId is null
                        ? $"Looks like a transfer {(t.AmountCents > 0 ? "to" : "from")} {name}: its description names it."
                        : $"Looks like a transfer {(t.AmountCents > 0 ? "to" : "from")} {name}: the same amount {(t.AmountCents > 0 ? "landed there" : "left there")}.";
        }
        await db.SaveChangesAsync(ct);
        await TransferFiling.CompleteAsync(db, householdId, ct);
    }

    /// <summary>One of your accounts, with the words it goes by (its name, its bank's name, its debt's name).</summary>
    public sealed record Named(Guid Id, AccountType Type, string? Mask, IReadOnlySet<string> Words);

    private async Task<List<Named>> AccountsAsync(Guid householdId, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.HouseholdId == householdId)
            .Select(a => new { a.Id, a.Type, a.Name, a.Mask, a.PlaidItemId }).ToListAsync(ct);
        var banks = await db.PlaidItems.AsNoTracking().Where(i => i.HouseholdId == householdId).ToDictionaryAsync(i => i.Id, i => i.InstitutionName, ct);
        var debts = await db.DebtAccounts.AsNoTracking().Where(d => d.HouseholdId == householdId && d.AccountId != null)
            .Select(d => new { d.AccountId, d.Name }).ToListAsync(ct);
        return accounts.Select(a => new Named(a.Id, a.Type, a.Mask, CardPayments.Words(
            [a.Name, a.PlaidItemId is { } p ? banks.GetValueOrDefault(p) : null, .. debts.Where(d => d.AccountId == a.Id).Select(d => d.Name)]))).ToList();
    }

    private static readonly HashSet<string> NoWords = [];

    /// <summary>
    /// The other account a transfer seen on one side only names (DECISIONS.md 302), so the payday tasks and the search for its
    /// other half know where it went: for a card payment, the card its last four digits or a word from its names point to; for
    /// a move between your bank accounts, the account its last four digits point to, else "SAV" or "CHK" when you have just one
    /// savings or checking account besides this one. Null when it doesn't say, or could mean more than one.
    /// </summary>
    public static Guid? NamedIn(Transaction t, TransferRole role, IReadOnlyList<Named> accounts)
    {
        var own = accounts.FirstOrDefault(a => a.Id == t.AccountId);
        if (own is null || own.Type == AccountType.CreditCard) return null;
        var text = $"{t.Description} {t.Merchant}";
        var others = accounts.Where(a => a.Id != t.AccountId).ToList();
        List<(Guid, string?, IReadOnlySet<string>)> Of(IEnumerable<Named> list) => list.Select(a => (a.Id, a.Mask, a.Words)).ToList();
        if (role == TransferRole.CardPaymentSent)
            return CardPayments.Named(text, Of(others.Where(a => a.Type == AccountType.CreditCard)));
        if (role is not (TransferRole.OwnTransferOut or TransferRole.OwnTransferIn)) return null;
        // Between bank accounts, only digits or "SAV"/"CHK": names are shared too widely (every account at one bank has its
        // name), and a payment to someone else can be worded like a transfer.
        var banksOnly = others.Where(a => a.Type != AccountType.CreditCard).ToList();
        if (CardPayments.Named(text, banksOnly.Select(a => (a.Id, a.Mask, (IReadOnlySet<string>)NoWords)).ToList()) is { } named) return named;
        var type = SavingsWord().IsMatch(text) ? AccountType.Savings : CheckingWord().IsMatch(text) ? AccountType.Checking : (AccountType?)null;
        var ofType = banksOnly.Where(a => a.Type == type).ToList();
        return type is not null && type != own.Type && ofType.Count == 1 ? ofType[0].Id : null;
    }

    [GeneratedRegex(@"\bSAV(INGS?)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex SavingsWord();

    [GeneratedRegex(@"\b(CHK|CHECKING)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CheckingWord();

    public static AccountKind ToKind(AccountType type) => type switch
    {
        AccountType.CreditCard => AccountKind.CreditCard,
        AccountType.Savings => AccountKind.Savings,
        _ => AccountKind.Checking,
    };
}
