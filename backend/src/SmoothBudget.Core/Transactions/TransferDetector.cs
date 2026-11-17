namespace SmoothBudget.Core.Transactions;

public enum AccountKind { Checking, Savings, CreditCard }

/// <summary>A transaction as the transfer detector sees it. Positive cents = money out of that account.</summary>
public sealed record TransferCandidate(
    Guid Id,
    Guid AccountId,
    AccountKind Account,
    DateOnly Date,
    long AmountCents,
    TransactionKind Kind,
    string Description,
    Guid? TransferGroupId = null,
    bool UserLocked = false,
    bool Waiting = false);

public enum TransferRole
{
    /// <summary>Money leaving a bank account to pay a card. Counts as a debt payment, not spending.</summary>
    CardPaymentSent,
    /// <summary>The payment arriving on the card. Not spending, not a refund.</summary>
    CardPaymentReceived,
    /// <summary>Money leaving one of the owner's accounts for another.</summary>
    OwnTransferOut,
    /// <summary>Money arriving from another of the owner's accounts.</summary>
    OwnTransferIn,
}

/// <param name="GroupId">Shared by the two sides of a matched pair; null when only one side was seen.</param>
/// <param name="CounterpartAccount">The other side's account type, when matched.</param>
/// <param name="CounterpartAccountId">The other side's account, when matched.</param>
public sealed record TransferDecision(Guid Id, TransferRole Role, Guid? GroupId, AccountKind? CounterpartAccount, Guid? CounterpartAccountId = null);

/// <summary>
/// Finds card payments and moves between the owner's own accounts, so they are never counted as
/// spending (SPEC section 3, "Transfers are not spending"). Two passes:
/// <list type="number">
/// <item>Each row on its own: a card's "Payment" row, Chase's LOAN_PMT and ACCT_XFER types, and known
/// description patterns ("Payment to Chase card", "APPLECARD GSBANK", "Online Transfer to").</item>
/// <item>Pairing: money out of one account and the same amount into another of the owner's accounts
/// within <see cref="MatchWindowDays"/> days. At least one side must already look like a payment or
/// transfer, so an ordinary purchase is never mistaken for one. This also catches payments whose
/// bank-side description the patterns don't know.</item>
/// <item>Most likely a transfer (DECISIONS.md 259): two rows still waiting for review on two of your accounts, one in and
/// one out, for the same amount within <see cref="LikelyWindowDays"/> days, even when neither looks like one.</item>
/// </list>
/// Rows the user has locked are never changed.
/// </summary>
public static class TransferDetector
{
    public const int MatchWindowDays = 5;
    /// <summary>How close two same-amount rows have to be to look like a transfer when neither is worded like one.</summary>
    public const int LikelyWindowDays = 3;

    public static readonly string[] CardPaymentPatterns =
    [
        "PAYMENT TO CHASE CARD", "CHASE CREDIT CRD", "CRD AUTOPAY", "CARD AUTOPAY", "APPLECARD GSBANK",
        "APPLE CARD PAYMENT", "GSBANK PAYMENT", "CREDIT CARD PAYMENT",
        // Store cards (e.g. Amazon's "AMZ_STORECRD_PMT"), seen in the owner's real checking export.
        "STORECRD_PMT", "STORECRD PMT", "STORE CRD PMT",
    ];

    public static readonly string[] OwnTransferPatterns =
    [
        "ONLINE TRANSFER TO", "ONLINE TRANSFER FROM", "TRANSFER TO SAV", "TRANSFER FROM SAV",
        "TRANSFER TO CHK", "TRANSFER FROM CHK",
    ];

    public static IReadOnlyList<TransferDecision> Detect(IReadOnlyList<TransferCandidate> txns)
    {
        var roles = new Dictionary<Guid, TransferRole>();
        foreach (var t in txns)
        {
            if (Classify(t) is { } role)
                roles[t.Id] = role;
        }

        var paired = new Dictionary<Guid, (Guid Group, AccountKind Other, Guid OtherId)>();
        foreach (var t in txns.Where(t => t.TransferGroupId is not null))
        {
            var partner = txns.FirstOrDefault(o => o.TransferGroupId == t.TransferGroupId && o.Id != t.Id);
            if (partner is not null)
                paired[t.Id] = (t.TransferGroupId!.Value, partner.Account, partner.AccountId);
        }

        var outflows = txns
            .Where(t => t.AmountCents > 0 && !t.UserLocked && !paired.ContainsKey(t.Id) && t.Account != AccountKind.CreditCard)
            .OrderBy(t => t.Date).ThenBy(t => t.Id)
            .ToList();

        foreach (var o in outflows)
        {
            roles.TryGetValue(o.Id, out var outRole);
            var outKnown = roles.ContainsKey(o.Id);

            var match = txns
                .Where(i => i.AmountCents == -o.AmountCents
                            && i.AccountId != o.AccountId
                            && !i.UserLocked
                            && !paired.ContainsKey(i.Id)
                            && Math.Abs(i.Date.DayNumber - o.Date.DayNumber) <= MatchWindowDays)
                .Select(i => (i, inRole: roles.TryGetValue(i.Id, out var r) ? r : (TransferRole?)null))
                .Where(x => Compatible(o, outKnown ? outRole : null, x.i, x.inRole) || Likely(o, outKnown, x.i, x.inRole))
                .OrderBy(x => Math.Abs(x.i.Date.DayNumber - o.Date.DayNumber))
                .ThenBy(x => x.i.Date)
                .ThenBy(x => x.i.Id)
                .Select(x => x.i)
                .FirstOrDefault();
            if (match is null)
                continue;

            var group = Guid.NewGuid();
            paired[o.Id] = (group, match.Account, match.AccountId);
            paired[match.Id] = (group, o.Account, o.AccountId);
            if (match.Account == AccountKind.CreditCard)
            {
                roles[o.Id] = TransferRole.CardPaymentSent;
                roles[match.Id] = TransferRole.CardPaymentReceived;
            }
            else
            {
                roles[o.Id] = TransferRole.OwnTransferOut;
                roles[match.Id] = TransferRole.OwnTransferIn;
            }
        }

        return roles
            .Where(kv => !txns.First(t => t.Id == kv.Key).UserLocked)
            .Select(kv => paired.TryGetValue(kv.Key, out var p)
                ? new TransferDecision(kv.Key, kv.Value, p.Group, p.Other, p.OtherId)
                : new TransferDecision(kv.Key, kv.Value, null, null))
            .ToList();
    }

    /// <summary>Classifies a single row with no knowledge of other rows. Null = not a transfer by itself.</summary>
    public static TransferRole? Classify(TransferCandidate t)
    {
        var desc = t.Description.ToUpperInvariant();
        var outflow = t.AmountCents > 0;

        if (t.Account == AccountKind.CreditCard)
            return t.Kind == TransactionKind.CardPayment && !outflow ? TransferRole.CardPaymentReceived : null;

        if (outflow && (t.Kind == TransactionKind.CardPayment || CardPaymentPatterns.Any(desc.Contains)))
            return TransferRole.CardPaymentSent;
        if (t.Kind == TransactionKind.Transfer || OwnTransferPatterns.Any(desc.Contains))
            return outflow ? TransferRole.OwnTransferOut : TransferRole.OwnTransferIn;
        return null;
    }

    /// <summary>Same amount, one in and one out, both still waiting for you, a few days apart: most likely a transfer.</summary>
    private static bool Likely(TransferCandidate o, bool outKnown, TransferCandidate i, TransferRole? inRole) =>
        !outKnown && inRole is null && o.Waiting && i.Waiting && Math.Abs(i.Date.DayNumber - o.Date.DayNumber) <= LikelyWindowDays;

    private static bool Compatible(TransferCandidate o, TransferRole? outRole, TransferCandidate i, TransferRole? inRole)
    {
        if (i.Account == AccountKind.CreditCard)
            // A bank-to-card payment: the card side must be the card's own payment row.
            return inRole == TransferRole.CardPaymentReceived
                   && outRole is null or TransferRole.CardPaymentSent;

        // Bank to bank: at least one side must already look like a transfer.
        return (outRole == TransferRole.OwnTransferOut || inRole == TransferRole.OwnTransferIn)
               && outRole is null or TransferRole.OwnTransferOut
               && inRole is null or TransferRole.OwnTransferIn;
    }
}
