using System.Text.RegularExpressions;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Core.Transactions;

/// <summary>
/// A spending pattern recognized from merchant names, independent of the bank's label. It can become a
/// category of its own (EV charging, Gaming, Pets, …) or point at a standard one (Rent, Phone, …).
/// </summary>
/// <param name="MinTransactions">How many matching purchases (over the history looked at) before it's worth its own category.</param>
/// <param name="Custom">True for categories beyond the common set, created only for people who have the pattern.</param>
public sealed record SpendingTheme(string Name, CategoryType Type, bool IsDiscretionary, int MinTransactions, bool Custom, IReadOnlyList<string> Keywords, IReadOnlyList<string>? Exclude = null);

/// <summary>
/// Merchant-name patterns, checked before the bank's own category. This is what tells EV charging apart
/// from gas (charging networks often carry the bank's "gas" label) and spots per-person patterns worth a
/// category of their own. Keywords match whole words in the merchant name and description.
/// </summary>
public static partial class SpendingThemes
{
    public static readonly IReadOnlyList<SpendingTheme> All =
    [
        new("EV charging", CategoryType.Variable, false, 2, true,
            ["CHARGING", "CHARGEPOINT", "EVGO", "ELECTRIFY AMERICA", "SUPERCHARGER", "EV CHARGE", "VOLTA"]),
        new("Rent", CategoryType.FixedBill, false, 2, false,
            ["RENT", "APARTMENTS", "APARTMENT", "APTS", "PROPERTY MANAGEMENT", "PROPERTY MGMT", "RESIDENTIAL"], Exclude: ["RENT-A-CAR", "RENT A CAR", "CAR RENT"]),
        new("Phone", CategoryType.FixedBill, false, 2, false,
            ["VERIZON", "T-MOBILE", "TMOBILE", "AT&T", "MINT MOBILE", "CRICKET WIRELESS", "VISIBLE", "GOOGLE FI", "US CELLULAR", "BOOST MOBILE"]),
        new("Utilities & internet", CategoryType.FixedBill, false, 2, false,
            ["XCEL", "COMCAST", "XFINITY", "SPECTRUM", "CENTURYLINK", "QUANTUM FIBER", "FIBER", "INTERNET", "UTILITIES", "ELECTRIC", "ENERGY", "WATER DEPT", "GAS CO"]),
        new("Car insurance", CategoryType.FixedBill, false, 2, false,
            ["GEICO", "PROGRESSIVE", "STATE FARM", "ALLSTATE", "USAA", "LIBERTY MUTUAL", "FARMERS INS", "AUTO INSURANCE"]),
        new("Subscriptions", CategoryType.FixedBill, false, 2, false,
            ["NETFLIX", "HULU", "SPOTIFY", "DISNEY PLUS", "DISNEYPLUS", "HBO", "MAX COM", "APPLE.COM/BILL", "APPLE COM BILL", "YOUTUBE PREMIUM",
             "YOUTUBE TV", "PARAMOUNT", "PEACOCK", "AUDIBLE", "ICLOUD", "PATREON", "OPENAI", "CHATGPT", "ADOBE", "MICROSOFT 365", "AMAZON PRIME", "CRUNCHYROLL"]),
        new("Gaming", CategoryType.Variable, true, 3, true,
            ["STEAM", "STEAMGAMES", "PLAYSTATION", "XBOX", "NINTENDO", "EPIC GAMES", "BLIZZARD", "RIOT GAMES", "ROBLOX"]),
        new("Coffee", CategoryType.Variable, true, 8, true,
            ["STARBUCKS", "DUTCH BROS", "PEETS", "COFFEE", "ESPRESSO", "DUNKIN"]),
        new("Pets", CategoryType.Variable, false, 2, true,
            ["PETCO", "PETSMART", "CHEWY", "VET", "VETERINARY", "ANIMAL HOSPITAL", "PET SUPPLIES"]),
        new("Fitness", CategoryType.Variable, false, 2, true,
            ["GYM", "FITNESS", "PLANET FITNESS", "CLIMBING", "YOGA", "CROSSFIT", "PELOTON", "BOULDERING"]),
        new("Transit & rideshare", CategoryType.Variable, false, 3, true,
            ["RTD", "LYFT", "UBER", "PARKING", "PARKMOBILE", "TOLL", "TRANSIT", "METRO", "AMTRAK"], Exclude: ["UBER EATS"]),
        // Interest is a bill planned from your card balances, kept out of Left to Spend (DECISIONS.md 159).
        new(CardInterest.CategoryName, CategoryType.FixedBill, false, 1, true,
            ["INTEREST CHARGE", "INTEREST CHARGES", "INTEREST CHARGED", "PURCHASE INTEREST", "CASH ADVANCE INTEREST", "FINANCE CHARGE"]),
        new("Fees & interest", CategoryType.Variable, false, 2, true,
            ["LATE FEE", "OVERDRAFT", "SERVICE FEE", "ATM FEE", "FOREIGN TRANSACTION FEE", "ANNUAL FEE", "MONTHLY FEE"]),
    ];

    private static readonly Dictionary<string, Regex> Patterns = All.ToDictionary(t => t.Name, t => Build(t.Keywords));
    private static readonly Dictionary<string, Regex?> Exclusions = All.ToDictionary(t => t.Name, t => t.Exclude is null ? null : Build(t.Exclude));

    /// <summary>The first theme whose keywords appear as whole words in the merchant or description, or null.</summary>
    public static SpendingTheme? Match(string merchant, string? description = null)
    {
        var text = $"{merchant} {description}".ToUpperInvariant();
        foreach (var t in All)
        {
            if (Patterns[t.Name].IsMatch(text) && Exclusions[t.Name]?.IsMatch(text) != true)
                return t;
        }
        return null;
    }

    public static SpendingTheme? ByName(string name) => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private static Regex Build(IEnumerable<string> words) =>
        new(@"(?<![A-Z0-9])(" + string.Join('|', words.Select(Regex.Escape)) + @")(?![A-Z0-9])", RegexOptions.Compiled);
}
