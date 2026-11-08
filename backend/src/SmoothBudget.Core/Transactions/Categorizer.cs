namespace SmoothBudget.Core.Transactions;

/// <summary>A merchant rule: any merchant containing <see cref="Pattern"/> goes to <see cref="CategoryId"/>.</summary>
public sealed record MerchantRule(Guid CategoryId, string Pattern, DateTime CreatedUtc);

/// <summary>
/// Merchant-rule matching, the first step of <see cref="AutoCategorizer.Guess"/> (SPEC section 6.1). The bank-category
/// mapping lives in <see cref="AutoCategorizer.BankMap"/>; the older map that sat here was removed (DECISIONS.md 175).
/// </summary>
public static class Categorizer
{
    /// <summary>The most specific (longest) matching pattern wins; ties go to the newest rule.</summary>
    public static MerchantRule? MatchRule(string merchant, IEnumerable<MerchantRule> rules) =>
        rules
            .Where(r => r.Pattern.Length > 0 && merchant.Contains(r.Pattern, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Pattern.Length)
            .ThenByDescending(r => r.CreatedUtc)
            .FirstOrDefault();
}
