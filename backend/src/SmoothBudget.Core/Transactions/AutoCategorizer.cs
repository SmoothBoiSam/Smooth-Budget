using System.Text.RegularExpressions;

namespace SmoothBudget.Core.Transactions;

/// <summary>Everything about a transaction the categorizer looks at.</summary>
/// <param name="BankCategory">The most specific bank label: Plaid's detailed category, or a CSV's category column.</param>
/// <param name="BankPrimary">Plaid's primary category, when known.</param>
/// <param name="BankConfidence">Plaid's confidence_level (VERY_HIGH, HIGH, MEDIUM, LOW, UNKNOWN), when known.</param>
public sealed record CategorizationInput(string Merchant, string? BankCategory, string? BankPrimary = null, string? BankConfidence = null, string? Description = null);

/// <summary>What the user chose before for one merchant: how many times each category.</summary>
public sealed record MerchantHistory(string Merchant, IReadOnlyDictionary<Guid, int> CategoryCounts);

/// <summary>A category the household doesn't have yet but that would fit a group of transactions.</summary>
public sealed record SuggestedCategory(string Name, Budgeting.CategoryType Type, bool IsDiscretionary);

/// <param name="Unsure">True when it's a real guess (ambiguous or low confidence). Every automatic category waits for the
/// user to confirm it; this only tells "likely right" apart from "not sure" in the review list.</param>
/// <param name="Reason">Why, in plain words, shown next to the category.</param>
/// <param name="Suggestion">When nothing fits: a category worth adding for this kind of transaction.</param>
public sealed record CategoryGuess(Guid? CategoryId, CategorySource Source, bool Unsure, string Reason, SuggestedCategory? Suggestion = null)
{
    public static readonly CategoryGuess None = new(null, CategorySource.None, false, "No matching category.");
}

/// <summary>
/// Automatic categorization with a confidence level (follows SPEC 6.1 step 1: bank category, then merchant
/// rules, then what the user corrected). In order:
/// <list type="number">
/// <item><b>Your merchant rules</b>: sure.</item>
/// <item><b>What you chose before for this merchant</b>: sure when you've always picked the same category;
/// a guess (most common choice) when you've picked different ones.</item>
/// <item><b>Merchant patterns</b> (<see cref="SpendingThemes"/>), when the household has that category: sure. This is how
/// EV charging is told apart from gas even though the bank labels both "gas".</item>
/// <item><b>The bank's category</b> through <see cref="BankMap"/>: sure for clear-cut labels (gas, groceries,
/// restaurants); a guess for ambiguous ones (superstores, a generic "food and drink" with no detail) or when
/// Plaid itself reports MEDIUM/LOW confidence.</item>
/// </list>
/// Every automatic category is filed (so Left to Spend counts it) but stays unconfirmed until the user reviews it.
/// When nothing fits, it stays uncategorized and may carry a suggested new category.
/// </summary>
public static partial class AutoCategorizer
{
    /// <param name="Name">The household category name it maps to.</param>
    /// <param name="Sure">False when the label is ambiguous enough that the user should confirm.</param>
    public sealed record BankMapping(string? Name, bool Sure, SuggestedCategory? Suggestion = null);

    private static SuggestedCategory Spend(string name, bool discretionary = false) => new(name, Budgeting.CategoryType.Variable, discretionary);
    private static readonly SuggestedCategory CardInterestBill = new(CardInterest.CategoryName, Budgeting.CategoryType.FixedBill, false);

    /// <summary>
    /// Bank labels (Plaid personal-finance categories, detailed and primary, and Chase/Apple CSV categories)
    /// to the standard category names. Labels without a standard category carry a suggestion instead.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, BankMapping> BankMap = new Dictionary<string, BankMapping>(StringComparer.OrdinalIgnoreCase)
    {
        // Plaid detailed
        ["BANK_FEES_INTEREST_CHARGE"] = new(CardInterest.CategoryName, true, CardInterestBill),
        ["FOOD_AND_DRINK_GROCERIES"] = new("Groceries", true),
        ["FOOD_AND_DRINK_RESTAURANT"] = new("Eating out", true),
        ["FOOD_AND_DRINK_FAST_FOOD"] = new("Eating out", true),
        ["FOOD_AND_DRINK_COFFEE"] = new("Eating out", true),
        ["FOOD_AND_DRINK_BEER_WINE_AND_LIQUOR"] = new("Eating out", false),
        ["FOOD_AND_DRINK_VENDING_MACHINES"] = new("Eating out", true),
        ["FOOD_AND_DRINK_OTHER_FOOD_AND_DRINK"] = new("Eating out", false),
        ["TRANSPORTATION_GAS"] = new("Gas", true),
        ["GENERAL_MERCHANDISE_SUPERSTORES"] = new("Shopping", false), // Walmart/Target: often groceries
        ["GENERAL_MERCHANDISE_ONLINE_MARKETPLACES"] = new("Shopping", true),
        ["GENERAL_MERCHANDISE_CLOTHING_AND_ACCESSORIES"] = new("Shopping", true),
        ["GENERAL_MERCHANDISE_ELECTRONICS"] = new("Shopping", true),
        ["GENERAL_MERCHANDISE_DEPARTMENT_STORES"] = new("Shopping", true),
        ["GENERAL_MERCHANDISE_SPORTING_GOODS"] = new("Shopping", true),
        ["GENERAL_MERCHANDISE_DISCOUNT_STORES"] = new("Shopping", false),
        ["GENERAL_MERCHANDISE_CONVENIENCE_STORES"] = new("Shopping", false),
        ["GENERAL_MERCHANDISE_PET_SUPPLIES"] = new("Shopping", false),
        ["GENERAL_MERCHANDISE_GIFTS_AND_NOVELTIES"] = new("Shopping", true),
        ["GENERAL_MERCHANDISE_OFFICE_SUPPLIES"] = new("Shopping", true),
        ["GENERAL_MERCHANDISE_BOOKSTORES_AND_NEWSSTANDS"] = new("Shopping", true),
        ["GENERAL_MERCHANDISE_TOBACCO_AND_VAPE"] = new("Shopping", false),
        ["GENERAL_MERCHANDISE_OTHER_GENERAL_MERCHANDISE"] = new("Shopping", false),
        ["ENTERTAINMENT_TV_AND_MOVIES"] = new("Entertainment", false), // could be a streaming subscription
        ["ENTERTAINMENT_MUSIC_AND_AUDIO"] = new("Subscriptions", false),
        ["ENTERTAINMENT_VIDEO_GAMES"] = new("Entertainment", true),
        ["ENTERTAINMENT_SPORTING_EVENTS_AMUSEMENT_PARKS_AND_MUSEUMS"] = new("Entertainment", true),
        ["ENTERTAINMENT_CASINOS_AND_GAMBLING"] = new("Entertainment", true),
        ["ENTERTAINMENT_OTHER_ENTERTAINMENT"] = new("Entertainment", false),
        ["HOME_IMPROVEMENT_HARDWARE"] = new("Household", true),
        ["HOME_IMPROVEMENT_FURNITURE"] = new("Household", true),
        ["HOME_IMPROVEMENT_REPAIR_AND_MAINTENANCE"] = new("Household", true),
        ["PERSONAL_CARE_HAIR_AND_BEAUTY"] = new("Personal care", true),
        ["PERSONAL_CARE_GYMS_AND_FITNESS_CENTERS"] = new("Personal care", false),
        ["PERSONAL_CARE_LAUNDRY_AND_DRY_CLEANING"] = new("Personal care", true),
        ["RENT_AND_UTILITIES_RENT"] = new("Rent", true),
        ["RENT_AND_UTILITIES_TELEPHONE"] = new("Phone", true),
        ["RENT_AND_UTILITIES_INTERNET_AND_CABLE"] = new("Utilities & internet", true),
        ["RENT_AND_UTILITIES_GAS_AND_ELECTRICITY"] = new("Utilities & internet", true),
        ["RENT_AND_UTILITIES_WATER"] = new("Utilities & internet", true),
        ["RENT_AND_UTILITIES_SEWAGE_AND_WASTE_MANAGEMENT"] = new("Utilities & internet", true),
        ["GENERAL_SERVICES_INSURANCE"] = new("Car insurance", false), // could be other insurance
        ["GENERAL_SERVICES_AUTOMOTIVE"] = new(null, false, Spend("Car care")),
        ["TRANSPORTATION_PUBLIC_TRANSIT"] = new(null, false, Spend("Transportation")),
        ["TRANSPORTATION_TAXIS_AND_RIDE_SHARES"] = new(null, false, Spend("Transportation")),
        ["TRANSPORTATION_PARKING"] = new(null, false, Spend("Transportation")),
        ["TRANSPORTATION_TOLLS"] = new(null, false, Spend("Transportation")),

        // Plaid primary (used when the detailed category isn't known or isn't listed above)
        ["FOOD_AND_DRINK"] = new("Eating out", false),
        ["GENERAL_MERCHANDISE"] = new("Shopping", false),
        ["ENTERTAINMENT"] = new("Entertainment", false),
        ["HOME_IMPROVEMENT"] = new("Household", false),
        ["PERSONAL_CARE"] = new("Personal care", false),
        ["RENT_AND_UTILITIES"] = new("Utilities & internet", false),
        ["TRANSPORTATION"] = new(null, false, Spend("Transportation")),
        ["MEDICAL"] = new(null, false, Spend("Medical")),
        ["TRAVEL"] = new(null, false, Spend("Travel", discretionary: true)),
        ["GENERAL_SERVICES"] = new(null, false, Spend("Services")),
        ["GOVERNMENT_AND_NON_PROFIT"] = new(null, false, Spend("Taxes & donations")),
        ["BANK_FEES"] = new(null, false, Spend("Fees & interest")),
        ["LOAN_PAYMENTS"] = new(null, false, new SuggestedCategory("Loan payments", Budgeting.CategoryType.DebtPayment, false)),

        // Chase credit and Apple Card CSV categories
        ["Groceries"] = new("Groceries", true),
        ["Grocery"] = new("Groceries", true),
        ["Supermarkets"] = new("Groceries", true),
        ["Food & Drink"] = new("Eating out", true),
        ["Restaurants"] = new("Eating out", true),
        ["Gas"] = new("Gas", true),
        ["Gas Stations"] = new("Gas", true),
        ["Shopping"] = new("Shopping", true),
        ["Entertainment"] = new("Entertainment", false), // often streaming subscriptions
        ["Home"] = new("Household", true),
        ["Personal"] = new("Personal care", false),
        ["Health & Wellness"] = new("Personal care", false),
        ["Bills & Utilities"] = new("Utilities & internet", false),
        ["Utilities"] = new("Utilities & internet", true),
        ["Automotive"] = new(null, false, Spend("Car care")),
        ["Transportation"] = new(null, false, Spend("Transportation")),
        ["Medical"] = new(null, false, Spend("Medical")),
        ["Travel"] = new(null, false, Spend("Travel", discretionary: true)),
        ["Airlines"] = new(null, false, Spend("Travel", discretionary: true)),
        ["Hotels"] = new(null, false, Spend("Travel", discretionary: true)),
        ["Interest"] = new(CardInterest.CategoryName, true, CardInterestBill),
        ["Fees & Adjustments"] = new(null, false, Spend("Fees & interest")),
    };

    public static CategoryGuess Guess(
        CategorizationInput input,
        IReadOnlyCollection<MerchantRule> rules,
        MerchantHistory? history,
        IReadOnlyDictionary<string, Guid> categoryIdsByName)
    {
        // 1. The user's own merchant rules.
        var rule = Categorizer.MatchRule(input.Merchant, rules);
        if (rule is not null)
            return new CategoryGuess(rule.CategoryId, CategorySource.Rule, false, $"Your rule for \"{rule.Pattern}\".");

        // 2. What the user picked before for this merchant.
        if (history is { CategoryCounts.Count: > 0 })
        {
            var ranked = history.CategoryCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
            var consistent = ranked.Count == 1;
            return new CategoryGuess(ranked[0].Key, CategorySource.Learned, !consistent,
                consistent
                    ? $"You filed {Plural(ranked[0].Value, "earlier purchase")} here the same way."
                    : $"Your most common choice for this merchant ({ranked[0].Value} of {history.CategoryCounts.Values.Sum()}).");
        }

        // 3. Merchant patterns (EV charging, rent, phone carriers, streaming, …) that map to one of the household's categories.
        var theme = SpendingThemes.Match(input.Merchant, input.Description);
        if (theme is not null && FindCategory(theme.Name, categoryIdsByName) is { } themed)
            return new CategoryGuess(themed, CategorySource.Bank, false, $"Looks like {theme.Name.ToLowerInvariant()} from the merchant name.");

        // 4. The bank's category: the detailed label first, then the primary.
        foreach (var label in new[] { input.BankCategory, input.BankPrimary })
        {
            if (string.IsNullOrWhiteSpace(label))
                continue;
            if (!BankMap.TryGetValue(label.Trim(), out var map))
            {
                // A CSV label that exactly matches one of the household's own categories.
                var exact = categoryIdsByName.FirstOrDefault(kv => string.Equals(kv.Key, label.Trim(), StringComparison.OrdinalIgnoreCase));
                if (exact.Value != Guid.Empty)
                    return new CategoryGuess(exact.Value, CategorySource.Bank, false, $"The bank calls it \"{label.Trim()}\".");
                continue;
            }

            var target = map.Name is null ? null : FindCategory(map.Name, categoryIdsByName);
            var suggestionName = map.Suggestion?.Name;
            if (target is null && suggestionName is not null)
                target = FindCategory(suggestionName, categoryIdsByName); // the user already added the suggested category

            if (target is null)
                return CategoryGuess.None with { Reason = $"The bank calls it {Describe(label)}, and no category fits.", Suggestion = map.Suggestion };

            var lowConfidence = input.BankConfidence is { } c && c.ToUpperInvariant() is "MEDIUM" or "LOW" or "UNKNOWN";
            var usedPrimary = label == input.BankPrimary && input.BankCategory is not null && input.BankCategory != input.BankPrimary;
            var sure = map.Sure && !lowConfidence && !usedPrimary;
            return new CategoryGuess(target, CategorySource.Bank, !sure,
                sure
                    ? $"The bank calls it {Describe(label)}."
                    : lowConfidence
                        ? $"The bank thinks it's {Describe(label)}, but isn't sure."
                        : $"The bank calls it {Describe(label)}, which could mean more than one thing.");
        }

        return CategoryGuess.None;
    }

    private static Guid? FindCategory(string name, IReadOnlyDictionary<string, Guid> ids)
    {
        var hit = ids.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase));
        return hit.Value == Guid.Empty ? null : hit.Value;
    }

    /// <summary>"FOOD_AND_DRINK_GROCERIES" -> "food and drink: groceries"; CSV labels are quoted as they are.</summary>
    public static string Describe(string label)
    {
        if (!PlaidLabel().IsMatch(label))
            return $"\"{label}\"";
        var primary = BankMap.Keys.Where(k => PlaidLabel().IsMatch(k) && !k.Contains("__") && label.StartsWith(k + "_", StringComparison.Ordinal))
            .OrderByDescending(k => k.Length).FirstOrDefault();
        string Words(string s) => s.Replace('_', ' ').ToLowerInvariant();
        return primary is null ? Words(label) : $"{Words(primary)}: {Words(label[(primary.Length + 1)..])}";
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    [GeneratedRegex("^[A-Z_]+$")]
    private static partial Regex PlaidLabel();
}
