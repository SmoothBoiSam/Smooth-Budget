using System.Globalization;
using System.Text.RegularExpressions;

namespace SmoothBudget.Core.Transactions;

/// <summary>A purchase the owner split with the partner. OwnerShare is the split factor (0.5 = half each).</summary>
public sealed record SplitPurchase(Guid Id, DateOnly Date, string Merchant, long AmountCents, decimal OwnerShare)
{
    /// <summary>What the partner owes for it, in cents (positive for a purchase, negative for a refund).</summary>
    public long PartnerShareCents => AmountCents - (OwnerShare == 1m ? AmountCents : (long)Math.Round(AmountCents * OwnerShare, 0, MidpointRounding.AwayFromZero));
}

/// <summary>A transfer between the owner and the partner. Positive cents = the owner sent it; negative = she did.</summary>
public sealed record PartnerTransfer(Guid Id, DateOnly Date, long AmountCents, TransactionKind Kind);

/// <summary>Whom the partner money is for, as the app guesses it. The user always confirms (DECISIONS.md 86).</summary>
public enum PartnerPurpose
{
    /// <summary>Her regular contribution toward bills (the $800 toward rent). Counts as partner money in the budget.</summary>
    Contribution,
    /// <summary>Paying back: her share of split purchases, or the owner paying back his share of hers.</summary>
    SettleUp,
    /// <summary>Money the owner sent her for his share of something she bought: spending in a category he picks.</summary>
    MyShareOfHers,
}

public sealed record PartnerGuess(PartnerPurpose Purpose, bool Unsure, string Reason, Guid? MatchedPurchaseId = null);

/// <param name="OwedToOwnerCents">Positive: the partner owes the owner. Negative: the owner owes her.</param>
public sealed record SettleUpBalance(long OwedToOwnerCents, long SplitSharesCents, long SettledByHerCents, long SettledByOwnerCents);

/// <summary>
/// Partner money beyond the fixed contribution (SPEC section 3, "Partner money", and Appendix D):
/// <list type="bullet">
/// <item>Split purchases count only the owner's share; her share is owed to him in the settle-up balance.</item>
/// <item>Money in from her is either her contribution toward bills (large, usually monthly) or paying back.</item>
/// <item>Money he sends her is either his share of something she bought (spending, in a category he picks)
/// or paying back what he owes.</item>
/// </list>
/// The app only guesses which; every partner transfer waits in review until the owner confirms it.
/// </summary>
public static partial class PartnerMoney
{
    /// <summary>Incoming partner money at least this large looks like her contribution toward bills, not paying back.</summary>
    public const long ContributionMinCents = 300_00;
    /// <summary>How far back a paying-back transfer is matched against split purchases.</summary>
    public const int MatchDays = 21;
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    /// <summary>True when money going out is to the partner: a Zelle/Venmo-style payment naming her.</summary>
    public static bool IsToPartner(string description, long amountCents, string? partnerName)
    {
        if (amountCents <= 0 || string.IsNullOrWhiteSpace(partnerName))
            return false;
        var m = RecipientPattern().Match(Normalize(description));
        if (!m.Success)
            return false;
        // Outgoing transfers often show a nickname, so the first name alone is enough ("Zelle payment to Jane 123").
        var first = Normalize(partnerName).Split(' ')[0];
        return first.Length >= 2 && m.Groups["rest"].Value.Split(' ').Contains(first);
    }

    /// <summary>The app's guess for one partner transfer (positive = the owner sent it).</summary>
    public static PartnerGuess Guess(PartnerTransfer t, IReadOnlyList<SplitPurchase> splits, string partnerName)
    {
        var first = Title(partnerName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? partnerName);
        if (t.AmountCents < 0)
        {
            var received = -t.AmountCents;
            if (Match(received, t.Date, splits) is { } m)
                return new PartnerGuess(PartnerPurpose.SettleUp, false,
                    $"Matches {first}'s share of {m.Merchant} {Money(m.AmountCents)} on {m.Date.ToString("MMM d", Us)}.", m.Id);
            return received >= ContributionMinCents
                ? new PartnerGuess(PartnerPurpose.Contribution, false, $"Looks like {first}'s regular contribution toward bills.")
                : new PartnerGuess(PartnerPurpose.SettleUp, true, $"A smaller amount from {first}: probably paying you back. Check what it was for.");
        }
        return new PartnerGuess(PartnerPurpose.MyShareOfHers, true, $"Sent to {first}: your share of something {first} bought, or paying back?");
    }

    /// <summary>
    /// Who owes whom: her share of every split purchase, minus what she paid back, plus what the owner paid back.
    /// Only activity after the last "settled" date counts.
    /// </summary>
    public static SettleUpBalance Balance(IReadOnlyList<SplitPurchase> splits, IReadOnlyList<PartnerTransfer> transfers, DateOnly? settledThrough = null)
    {
        bool After(DateOnly d) => settledThrough is not { } s || d > s;
        var shares = splits.Where(s => After(s.Date)).Sum(s => s.PartnerShareCents);
        var settleUps = transfers.Where(t => t.Kind == TransactionKind.PartnerSettleUp && After(t.Date)).ToList();
        var byHer = settleUps.Where(t => t.AmountCents < 0).Sum(t => -t.AmountCents);
        var byOwner = settleUps.Where(t => t.AmountCents > 0).Sum(t => t.AmountCents);
        return new SettleUpBalance(shares - byHer + byOwner, shares, byHer, byOwner);
    }

    /// <summary>The latest split purchase in the last few weeks whose partner share is exactly this amount.</summary>
    private static SplitPurchase? Match(long cents, DateOnly date, IReadOnlyList<SplitPurchase> splits) =>
        splits.Where(s => s.Date <= date && s.Date >= date.AddDays(-MatchDays) && s.PartnerShareCents == cents)
            .OrderByDescending(s => s.Date).FirstOrDefault();

    private static string Normalize(string s) => Whitespace().Replace(s.ToUpperInvariant(), " ").Trim();
    private static string Title(string w) => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
    private static string Money(long cents) => (cents / 100m).ToString("C", Us);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(ZELLE( PAYMENT)? TO|VENMO( PAYMENT)? TO|PAYMENT TO|SENT TO|APPLE CASH( SENT)? TO)\s+(?<rest>.+)$")]
    private static partial Regex RecipientPattern();
}
