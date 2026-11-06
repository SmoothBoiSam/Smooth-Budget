using System.Text.RegularExpressions;

namespace SmoothBudget.Core.Import;

public static partial class MerchantText
{
    /// <summary>
    /// Cleans a bank description for storage: trims, collapses whitespace, and masks any run of
    /// 8 or more digits (which could be an account or card number) down to its last four.
    /// </summary>
    public static string Sanitize(string description)
    {
        var s = Whitespace().Replace(description ?? "", " ").Trim();
        return LongDigits().Replace(s, m => "••" + m.Value[^4..]);
    }

    /// <summary>
    /// A stable, readable merchant key for rules and display: upper case, with card-purchase
    /// boilerplate, payment-processor prefixes, store numbers and reference numbers removed.
    /// "Card Purchase 11/09 King Soopers #0012 Denver CO Card 1234" becomes "KING SOOPERS DENVER CO".
    /// </summary>
    public static string Normalize(string description)
    {
        var s = Whitespace().Replace(description ?? "", " ").Trim().ToUpperInvariant();
        s = ChaseCardPurchasePrefix().Replace(s, "");
        s = TrailingCardDigits().Replace(s, "");
        s = ProcessorPrefix().Replace(s, "");
        s = AchIdSuffix().Replace(s, "");

        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Any(char.IsDigit) && !t.StartsWith('#') && !t.StartsWith('•'))
            .Select(t => t.Trim('*', '-', ',', '.', ':', ';'))
            .Where(t => t.Length > 0);
        var result = string.Join(' ', tokens);
        return result.Length > 0 ? result : Whitespace().Replace(description ?? "", " ").Trim().ToUpperInvariant();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\d{8,}")]
    private static partial Regex LongDigits();

    [GeneratedRegex(@"^(RECURRING )?CARD PURCHASE( WITH PIN| RETURN)? \d{1,2}/\d{1,2} |^(POS DEBIT|DEBIT CARD PURCHASE|CHECKCARD) (\d{4} )?")]
    private static partial Regex ChaseCardPurchasePrefix();

    [GeneratedRegex(@" CARD \d{4}$")]
    private static partial Regex TrailingCardDigits();

    [GeneratedRegex(@"^(SQ ?\*|TST ?\*|PY ?\*|SP ?\*|DD ?\*|PAYPAL ?\*|GOOGLE ?\*|IC ?\*)\s*")]
    private static partial Regex ProcessorPrefix();

    /// <summary>ACH boilerplate such as "WEB ID: 9999999999" or "PPD ID: …" and everything after it.</summary>
    [GeneratedRegex(@"\s+(WEB|PPD|CCD|TEL|ARC|IAT) ID\b.*$")]
    private static partial Regex AchIdSuffix();
}
