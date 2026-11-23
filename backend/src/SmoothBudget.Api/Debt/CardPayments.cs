using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Debt;

/// <summary>One payment a card received: from the card's own account, or (FromCardSide false) as it left checking.</summary>
public record CardPayment(Guid Id, DateOnly Date, string Merchant, long Cents, Guid CardAccountId, bool FromCardSide);

/// <summary>
/// Payments to each card (DECISIONS.md 244). The card's own account shows a payment as a credit. A card that isn't synced
/// (an Apple Card from CSVs, say) shows it only once you upload its next statement, so a payment leaving checking counts too.
/// It's tied to the card it names: the card you said it was toward (its debt, or the account you picked in Review, DECISIONS.md
/// 302), the card's last four digits, or a word from the card's name, its debt's name or its bank's name, also inside a longer
/// word ("FRUITCARD" names Fruit; only when exactly one card matches). It's skipped when the card's own side already shows the
/// same payment: paired with it as a transfer, or the same amount within 5 days.
/// </summary>
public static partial class CardPayments
{
    private const int SameDays = 5;

    // Words in most payment descriptions, too common to say which card.
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "card", "cards", "credit", "crd", "cr", "payment", "payments", "pmt", "pymt", "autopay", "auto", "epay", "online", "web", "mobile",
        "transfer", "xfer", "thank", "you", "visa", "mastercard", "amex", "discover", "account", "acct", "bank", "rewards", "signature",
        "platinum", "preferred", "checking", "savings", "ach", "deposit", "internet", "from", "the", "and", "for", "debit", "bill", "pay",
        "ending", "inc", "com", "www", "usa", "llc", "corp", "loan", "des", "ppd", "web", "id", "indn", "co", "entry",
    };

    public sealed record Card(Guid AccountId, string? Mask, IReadOnlySet<string> Words, IReadOnlyCollection<Guid> DebtIds);
    public sealed record Row(Guid Id, DateOnly Date, string Merchant, string Description, long AmountCents, Guid AccountId, bool OnCard,
        Guid? TransferGroupId, Guid? DebtAccountId, Guid? TransferAccountId = null);

    /// <summary>Payments to each card dated from..to (inclusive; either open), newest first.</summary>
    public static async Task<List<CardPayment>> LoadAsync(AppDbContext db, Guid householdId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.HouseholdId == householdId && a.Type == AccountType.CreditCard)
            .Select(a => new { a.Id, a.Name, a.Mask, a.PlaidItemId }).ToListAsync(ct);
        if (accounts.Count == 0) return [];
        var itemIds = accounts.Where(a => a.PlaidItemId != null).Select(a => a.PlaidItemId!.Value).ToList();
        var banks = await db.PlaidItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).Select(i => new { i.Id, i.InstitutionName }).ToListAsync(ct);
        var debts = await db.DebtAccounts.AsNoTracking().Where(d => d.HouseholdId == householdId && d.AccountId != null)
            .Select(d => new { d.Id, d.Name, d.AccountId }).ToListAsync(ct);
        var cards = accounts.Select(a => new Card(a.Id, a.Mask,
            Words([a.Name, banks.FirstOrDefault(b => b.Id == a.PlaidItemId)?.InstitutionName, .. debts.Where(d => d.AccountId == a.Id).Select(d => d.Name)]),
            debts.Where(d => d.AccountId == a.Id).Select(d => d.Id).ToList())).ToList();

        // A little either side, so the card's own side of a payment near the edge is still seen.
        var lo = from?.AddDays(-SameDays) ?? DateOnly.MinValue;
        var hi = to?.AddDays(SameDays) ?? DateOnly.MaxValue;
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Date >= lo && t.Date <= hi && (
                (t.Account.Type == AccountType.CreditCard && t.AmountCents < 0 && (t.Kind == TransactionKind.CardPayment || t.IsTransfer)) ||
                (t.Account.Type != AccountType.CreditCard && t.AmountCents > 0 && t.Kind == TransactionKind.CardPayment)))
            .Select(t => new Row(t.Id, t.Date, t.Merchant, t.Description, t.AmountCents, t.AccountId, t.Account.Type == AccountType.CreditCard,
                t.TransferGroupId, t.DebtAccountId, t.TransferAccountId))
            .ToListAsync(ct);
        return Attribute(rows, cards).Where(p => (from is null || p.Date >= from) && (to is null || p.Date <= to)).ToList();
    }

    public static List<CardPayment> Attribute(IReadOnlyList<Row> rows, IReadOnlyList<Card> cards)
    {
        var cardIds = cards.Select(c => c.AccountId).ToHashSet();
        var onCards = rows.Where(r => r.OnCard && cardIds.Contains(r.AccountId)).ToList();
        var result = onCards.Select(r => new CardPayment(r.Id, r.Date, r.Merchant, -r.AmountCents, r.AccountId, true)).ToList();
        var groups = onCards.Where(r => r.TransferGroupId != null).Select(r => r.TransferGroupId!.Value).ToHashSet();
        var used = new HashSet<Guid>();
        foreach (var r in rows.Where(r => !r.OnCard).OrderBy(r => r.Date))
        {
            if (r.TransferGroupId is { } g && groups.Contains(g)) continue; // the card's side is already counted
            if (CardFor(r, cards) is not { } card) continue;
            var twin = onCards.FirstOrDefault(c => c.AccountId == card && !used.Contains(c.Id) && -c.AmountCents == r.AmountCents
                                                   && Math.Abs(c.Date.DayNumber - r.Date.DayNumber) <= SameDays);
            if (twin is not null)
            {
                used.Add(twin.Id);
                continue;
            }
            result.Add(new CardPayment(r.Id, r.Date, r.Merchant, r.AmountCents, card, false));
        }
        return result.OrderByDescending(p => p.Date).ThenBy(p => p.Merchant).ToList();
    }

    /// <summary>The card a payment from checking was for, or null when it can't tell.</summary>
    public static Guid? CardFor(Row r, IReadOnlyList<Card> cards)
    {
        if (r.DebtAccountId is { } debt && cards.FirstOrDefault(c => c.DebtIds.Contains(debt)) is { } chosen) return chosen.AccountId;
        if (r.TransferAccountId is { } to && cards.Any(c => c.AccountId == to)) return to;
        return Named($"{r.Merchant} {r.Description}", cards.Select(c => (c.AccountId, c.Mask, c.Words)).ToList());
    }

    /// <summary>
    /// The one account a payment's words name (DECISIONS.md 244, 302): its last four digits, else a word from its names,
    /// whole or inside a longer word of at least as many letters. Null when none or more than one does.
    /// </summary>
    public static Guid? Named(string text, IReadOnlyList<(Guid Id, string? Mask, IReadOnlySet<string> Words)> accounts)
    {
        var digits = Digits().Replace(text, " ");
        var byMask = accounts.Where(c => c.Mask is { Length: >= 4 } m && digits.Contains(m, StringComparison.Ordinal)).ToList();
        if (byMask.Count == 1) return byMask[0].Id;
        var words = Words([text]);
        var byWord = accounts.Where(c => c.Words.Any(w => words.Any(x => x == w || (w.Length >= 4 && x.Contains(w, StringComparison.Ordinal))))).ToList();
        return byWord.Count == 1 ? byWord[0].Id : null;
    }

    /// <summary>The words an account goes by: from its name, its bank's name and its debt's name, lowercased, minus common ones.</summary>
    public static HashSet<string> Words(IEnumerable<string?> texts) =>
        texts.Where(t => !string.IsNullOrWhiteSpace(t))
            .SelectMany(t => NotLetters().Split(t!.ToLowerInvariant()))
            .Where(w => w.Length >= 3 && !Generic.Contains(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("[^0-9]+")]
    private static partial Regex Digits();

    [GeneratedRegex("[^a-z]+")]
    private static partial Regex NotLetters();
}
