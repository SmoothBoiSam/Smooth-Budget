using System.Globalization;
using System.Text.RegularExpressions;

namespace SmoothBudget.Core.Transactions;

/// <summary>A recent purchase of yours, to recognize "half of the groceries" when she pays you back.</summary>
public sealed record RecentPurchase(DateOnly Date, string Merchant, long AmountCents, Guid CategoryId);

/// <summary>What the app thinks a transfer between you was for. Nothing counts until you say (DECISIONS.md 100).</summary>
public sealed record PartnerSuggestion(Guid? CategoryId, string Reason);

/// <summary>
/// Money between the owner and the partner, beyond her fixed contribution (DECISIONS.md 100). There's no running
/// tally: when you split something, the money that moves between you <i>is</i> the split. Money from her that pays
/// you back lands in the category it was for (like a refund); money you send her for your share of something lands
/// in its category as spending; anything else from her is her money in. Each one waits, counted nowhere, until you
/// label it.
/// </summary>
public static partial class PartnerMoney
{
    /// <summary>How far back a pay-back is matched against your purchases.</summary>
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

    /// <summary>
    /// A suggestion for a transfer between you (positive cents = you sent it). Money from her that's exactly half of a
    /// recent purchase of yours suggests that purchase's category; otherwise it just asks.
    /// </summary>
    public static PartnerSuggestion Suggest(DateOnly date, long amountCents, IReadOnlyList<RecentPurchase> recent, string partnerName)
    {
        var first = Title(partnerName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? partnerName);
        if (amountCents < 0)
        {
            var received = -amountCents;
            var half = recent
                .Where(p => p.Date <= date && p.Date >= date.AddDays(-MatchDays) && Math.Abs(p.AmountCents - 2 * received) <= 1)
                .OrderByDescending(p => p.Date).FirstOrDefault();
            return half is not null
                ? new PartnerSuggestion(half.CategoryId, $"Looks like half of {half.Merchant} {Money(half.AmountCents)} on {half.Date.ToString("MMM d", Us)}.")
                : new PartnerSuggestion(null, $"From {first}: what was it for?");
        }
        return new PartnerSuggestion(null, $"Sent to {first}: what was it for?");
    }

    private static string Normalize(string s) => Whitespace().Replace(s.ToUpperInvariant(), " ").Trim();
    private static string Title(string w) => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
    private static string Money(long cents) => (cents / 100m).ToString("C", Us);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(ZELLE( PAYMENT)? TO|VENMO( PAYMENT)? TO|PAYMENT TO|SENT TO|APPLE CASH( SENT)? TO)\s+(?<rest>.+)$")]
    private static partial Regex RecipientPattern();
}
