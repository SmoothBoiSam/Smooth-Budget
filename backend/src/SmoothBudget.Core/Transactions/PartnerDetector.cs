using System.Text.RegularExpressions;

namespace SmoothBudget.Core.Transactions;

/// <summary>
/// Tells the partner's transfers apart from the owner's own income (SPEC Appendix D: her contributions
/// arrive as "Zelle payment from &lt;PARTNER NAME&gt; &lt;reference&gt;"). The name comes from Settings and is
/// never hard-coded.
/// </summary>
public static partial class PartnerDetector
{
    /// <summary>True when money coming in is from the partner, matched by the name as it appears on transfers.</summary>
    public static bool IsFromPartner(string description, long amountCents, string? partnerName)
    {
        if (amountCents >= 0 || string.IsNullOrWhiteSpace(partnerName))
            return false;
        var name = Normalize(partnerName);
        return name.Length >= 2 && Normalize(description).Contains(name, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sender's name in an incoming peer-to-peer transfer description ("Zelle payment from JANE DOE 1234"
    /// -> "JANE DOE"), or null when it isn't one. Used to suggest the partner's name in Settings.
    /// </summary>
    public static string? SenderName(string description)
    {
        var m = SenderPattern().Match(description.ToUpperInvariant());
        if (!m.Success)
            return null;
        var words = m.Groups["rest"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(w => !w.Any(char.IsDigit) && !w.Contains('•') && w is not ("ON" or "REF" or "CONF" or "ID:" or "ID"))
            .Take(4)
            .ToList();
        return words.Count == 0 ? null : string.Join(' ', words);
    }

    private static string Normalize(string s) => Whitespace().Replace(s.ToUpperInvariant(), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(ZELLE( PAYMENT)? FROM|VENMO( PAYMENT)? FROM|PAYMENT FROM|TRANSFER FROM|CASH APP\*?|APPLE CASH( SENT)? FROM)\s+(?<rest>.+)$")]
    private static partial Regex SenderPattern();
}
