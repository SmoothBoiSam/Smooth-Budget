namespace SmoothBudget.Core.Transactions;

/// <summary>A merchant rule: any merchant containing <see cref="Pattern"/> goes to <see cref="CategoryId"/>.</summary>
public sealed record MerchantRule(Guid CategoryId, string Pattern, DateTime CreatedUtc);

/// <summary>
/// Picks a category for an imported transaction (SPEC section 6.1, step 1), in rising precedence:
/// the bank's own category, then merchant rules. User corrections are applied elsewhere and are never
/// overwritten. Transfers are categorized by <see cref="TransferDetector"/>, not here.
/// </summary>
public static class Categorizer
{
    /// <summary>
    /// Default mapping from Chase and Apple Card category names to the app's category names.
    /// A bank category that matches one of the household's category names exactly is used as is.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultBankCategoryMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Groceries"] = "Groceries",
            ["Grocery"] = "Groceries",
            ["Supermarkets"] = "Groceries",
            ["Food & Drink"] = "Eating out",
            ["Restaurants"] = "Eating out",
            ["Gas"] = "Gas",
            ["Gas Stations"] = "Gas",
            ["Shopping"] = "Shopping",
            ["Entertainment"] = "Entertainment",
            ["Home"] = "Household",
            ["Personal"] = "Personal care",
            ["Health & Wellness"] = "Personal care",
            ["Bills & Utilities"] = "Utilities & internet",
            ["Utilities"] = "Utilities & internet",

            // Plaid personal-finance categories (detailed first, then primary).
            ["FOOD_AND_DRINK_GROCERIES"] = "Groceries",
            ["FOOD_AND_DRINK"] = "Eating out",
            ["TRANSPORTATION_GAS"] = "Gas",
            ["GENERAL_MERCHANDISE"] = "Shopping",
            ["ENTERTAINMENT"] = "Entertainment",
            ["HOME_IMPROVEMENT"] = "Household",
            ["PERSONAL_CARE"] = "Personal care",
            ["RENT_AND_UTILITIES_RENT"] = "Rent",
            ["RENT_AND_UTILITIES_TELEPHONE"] = "Phone",
            ["RENT_AND_UTILITIES"] = "Utilities & internet",
        };

    public static (Guid? CategoryId, CategorySource Source) Categorize(
        string merchant,
        string? bankCategory,
        IReadOnlyCollection<MerchantRule> rules,
        IReadOnlyDictionary<string, Guid> categoryIdsByName)
    {
        var rule = MatchRule(merchant, rules);
        if (rule is not null)
            return (rule.CategoryId, CategorySource.Rule);

        if (!string.IsNullOrWhiteSpace(bankCategory))
        {
            var name = DefaultBankCategoryMap.GetValueOrDefault(bankCategory.Trim(), bankCategory.Trim());
            var id = categoryIdsByName.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            if (id != Guid.Empty)
                return (id, CategorySource.Bank);
        }
        return (null, CategorySource.None);
    }

    /// <summary>The most specific (longest) matching pattern wins; ties go to the newest rule.</summary>
    public static MerchantRule? MatchRule(string merchant, IEnumerable<MerchantRule> rules) =>
        rules
            .Where(r => r.Pattern.Length > 0 && merchant.Contains(r.Pattern, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Pattern.Length)
            .ThenByDescending(r => r.CreatedUtc)
            .FirstOrDefault();
}
