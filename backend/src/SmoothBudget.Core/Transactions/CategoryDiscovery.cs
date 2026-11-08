using System.Globalization;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Core.Transactions;

/// <summary>A transaction as category discovery sees it. Positive cents = money out.</summary>
public sealed record DiscoveryTxn(
    DateOnly Date, long AmountCents, string Merchant, string? Description, string? BankCategory, string? BankPrimary,
    TransactionKind Kind, bool IsTransfer, bool IsCardPaymentFromBank = false, bool IsTransferToSavings = false,
    // The category the user confirmed for this purchase, if any: the strongest evidence there is.
    string? ConfirmedName = null, CategoryType ConfirmedType = CategoryType.Variable, bool ConfirmedDiscretionary = false);

/// <param name="Custom">A category beyond the common set, made because this person has the pattern (EV charging, Gaming, …).</param>
/// <param name="MonthlyCents">Average per month over complete months, from the month it first shows up.</param>
/// <param name="Examples">Up to three merchants, most frequent first, so the user can see why.</param>
/// <param name="MerchantPattern">One bill per line: the merchant this category is for (becomes a merchant rule).</param>
/// <param name="Replaces">The grouped category this bill is split out of (its transactions move over).</param>
public sealed record DiscoveredCategory(
    string Name, CategoryType Type, bool IsDiscretionary, bool Custom,
    int TransactionCount, int MonthsSeen, long MonthlyCents, IReadOnlyList<string> Examples, string Reason,
    string? MerchantPattern = null, string? Replaces = null);

/// <summary>An existing category with nothing in it: worth hiding.</summary>
public sealed record UnusedCategory(Guid Id, string Name, string Reason);

public sealed record ExistingCategory(Guid Id, string Name, CategoryType Type, bool IsHidden, int ConfirmedTransactions);

public sealed record DiscoveryResult(int MonthsAnalyzed, IReadOnlyList<DiscoveredCategory> Proposed, IReadOnlyList<UnusedCategory> Unused, int Unmatched);

/// <summary>
/// Works out a person's categories from their own transactions instead of a fixed list:
/// <list type="bullet">
/// <item>Each purchase gets a theme: the category the user confirmed for it, then a merchant pattern (<see cref="SpendingThemes"/>: EV charging, rent,
/// phone carriers, gaming, …), otherwise the bank's category (<see cref="AutoCategorizer.BankMap"/>).</item>
/// <item>A pattern-only theme that's too rare for its own category (e.g. a couple of coffees) falls back to the
/// bank's category (Eating out).</item>
/// <item>A theme becomes a category when it shows up enough: at least <see cref="MinTransactions"/> purchases
/// in at least <see cref="MinMonths"/> different months, or a bill seen in 2+ months, or $150+ in total.
/// So common categories like Gas or Phone only appear for people who actually spend there.</item>
/// <item>Card payments from a bank account add "Credit card payment"; transfers to savings, or the owner's
/// rainy-day fund goal (SPEC section 1), add "Rainy-day fund".</item>
/// <item>Existing categories with no themes and nothing the user filed there by hand are offered for hiding.</item>
/// </list>
/// </summary>
public static class CategoryDiscovery
{
    public const int MinTransactions = 3;
    public const int MinMonths = 2;
    public const long MinTotalCents = 150_00;
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static DiscoveryResult Discover(IReadOnlyList<DiscoveryTxn> transactions, IReadOnlyList<ExistingCategory> existing, bool includeRainyDayGoal = true)
    {
        var spending = transactions
            .Where(t => !t.IsTransfer && !TransactionRules.IsMoneyIn(t.Kind) && t.Kind is not (TransactionKind.CardPayment or TransactionKind.Transfer)
                        && t.AmountCents > 0)
            .ToList();
        var months = transactions.Count == 0 ? 0 : MonthsBetween(transactions.Min(t => t.Date), transactions.Max(t => t.Date));

        // 1. Pattern themes first; patterns too rare for their own category fall back to the bank's theme.
        var patternCounts = spending
            .Select(t => SpendingThemes.Match(t.Merchant, t.Description))
            .OfType<SpendingTheme>()
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.Count());

        (string Name, CategoryType Type, bool Discretionary, bool Custom)? ThemeFor(DiscoveryTxn t)
        {
            if (t.ConfirmedName is { } confirmed)
                return (confirmed, t.ConfirmedType, t.ConfirmedDiscretionary, false);
            var pattern = SpendingThemes.Match(t.Merchant, t.Description);
            if (pattern is not null && patternCounts[pattern.Name] >= pattern.MinTransactions)
                return (pattern.Name, pattern.Type, pattern.IsDiscretionary, pattern.Custom);
            if (t.Kind == TransactionKind.Fee)
                return ("Fees & interest", CategoryType.Variable, false, true);
            foreach (var label in new[] { t.BankCategory, t.BankPrimary })
            {
                if (label is null || !AutoCategorizer.BankMap.TryGetValue(label.Trim(), out var map))
                    continue;
                if (map.Name is not null)
                    return (map.Name, StandardType(map.Name), StandardDiscretionary(map.Name), false);
                if (map.Suggestion is { } s)
                    return (s.Name, s.Type, s.IsDiscretionary, true);
            }
            return null;
        }

        var themed = spending.Select(t => (Txn: t, Theme: ThemeFor(t))).ToList();
        var unmatched = themed.Count(x => x.Theme is null);

        // 2. Bills get one line each (DECISIONS.md 84): a bill theme with several merchants, and every subscription,
        //    becomes one category per merchant, named after it, so Netflix and Spotify never share a line.
        var lastDate = transactions.Count == 0 ? DateOnly.MinValue : transactions.Max(t => t.Date);
        var existingNames = existing.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = themed.Where(x => x.Theme is not null)
            .GroupBy(x => x.Theme!.Value.Name)
            .SelectMany(g =>
            {
                var theme = g.First().Theme!.Value;
                var merchants = g.Select(x => MerchantKey(x.Txn.Merchant)).Distinct().Count();
                var split = theme.Type == CategoryType.FixedBill && (merchants > 1 || theme.Name == "Subscriptions");
                if (!split)
                    return [(Name: theme.Name, theme.Type, theme.Discretionary, theme.Custom, Pattern: (string?)null, Replaces: (string?)null, Items: g.Select(x => x.Txn).ToList())];
                return g.GroupBy(x => MerchantKey(x.Txn.Merchant)).Select(m => (
                    Name: BillName(m.Key), theme.Type, theme.Discretionary, Custom: false, Pattern: (string?)m.Key,
                    Replaces: existingNames.Contains(theme.Name) ? theme.Name : null, Items: m.Select(x => x.Txn).ToList()));
            });

        // 3. Keep the ones that show up enough to be worth a category.
        var proposed = groups
            .Select(g =>
            {
                var count = g.Items.Count;
                var monthsSeen = g.Items.Select(t => (t.Date.Year, t.Date.Month)).Distinct().Count();
                var total = g.Items.Sum(t => t.AmountCents);
                var examples = g.Items.GroupBy(t => t.Merchant).OrderByDescending(m => m.Count()).ThenBy(m => m.Key)
                    .Take(3).Select(m => m.Key).ToList();
                var keep = (count >= MinTransactions && monthsSeen >= MinMonths)
                           || (g.Type == CategoryType.FixedBill && monthsSeen >= MinMonths)
                           || (count >= 2 && total >= MinTotalCents);
                var monthly = MonthlyAverage(g.Items, lastDate);
                var noun = g.Type == CategoryType.FixedBill ? "charges" : "purchases";
                var why = $"{count} {noun} in {monthsSeen} {(monthsSeen == 1 ? "month" : "months")}, about {Money(monthly)} a month"
                          + (g.Custom ? " (a pattern in your spending)" : "")
                          + (g.Replaces is not null ? $". Split out of {g.Replaces}" : "") + ".";
                return (keep, item: new DiscoveredCategory(g.Name, g.Type, g.Discretionary, g.Custom, count, monthsSeen, monthly, examples, why,
                    g.Pattern, g.Replaces));
            })
            .Where(x => x.keep)
            .Select(x => x.item)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(d => d.First()) // two merchants cleaning to one name
            .ToList();

        // 3. Debt and savings come from transfers and goals, not purchases.
        var cardPayments = transactions.Count(t => t.IsCardPaymentFromBank);
        if (cardPayments > 0)
            proposed.Add(new DiscoveredCategory("Credit card payment", CategoryType.DebtPayment, false, false, cardPayments,
                transactions.Where(t => t.IsCardPaymentFromBank).Select(t => (t.Date.Year, t.Date.Month)).Distinct().Count(),
                MonthlyAverage(transactions.Where(t => t.IsCardPaymentFromBank).ToList(), lastDate), [],
                $"{cardPayments} card payments from your bank account."));
        var toSavings = transactions.Count(t => t.IsTransferToSavings);
        if (toSavings > 0 || includeRainyDayGoal)
            proposed.Add(new DiscoveredCategory("Rainy-day fund", CategoryType.SavingsTransfer, false, false, toSavings, 0, 0, [],
                toSavings > 0 ? $"{toSavings} transfers to savings." : "Your goal: a rainy-day fund of about $1,000."));

        // 4. Order like the budget: spending, bills, debt, savings; biggest first within each.
        proposed = proposed.OrderBy(p => p.Type switch { CategoryType.Variable => 0, CategoryType.FixedBill => 1, CategoryType.DebtPayment => 2, _ => 3 })
            .ThenByDescending(p => p.MonthlyCents).ThenBy(p => p.Name).ToList();

        // 5. Existing categories nobody uses.
        var proposedNames = proposed.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replaced = proposed.Select(p => p.Replaces).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unused = existing
            .Where(e => !e.IsHidden && !proposedNames.Contains(e.Name) && (e.ConfirmedTransactions == 0 || replaced.Contains(e.Name)))
            .Select(e => new UnusedCategory(e.Id, e.Name, replaced.Contains(e.Name)
                ? "Replaced by one line per bill above."
                : months == 0
                    ? "Nothing filed here yet."
                    : $"No purchases that fit it in the last {months} {(months == 1 ? "month" : "months")}."))
            .ToList();

        return new DiscoveryResult(months, proposed, unused, unmatched);
    }

    private static CategoryType StandardType(string name) => name switch
    {
        "Rent" or "Phone" or "Utilities & internet" or "Car insurance" or "Subscriptions" or CardInterest.CategoryName => CategoryType.FixedBill,
        _ => CategoryType.Variable,
    };

    private static bool StandardDiscretionary(string name) => name is "Eating out" or "Shopping" or "Entertainment";

    private static int MonthsBetween(DateOnly first, DateOnly last) => (last.Year - first.Year) * 12 + last.Month - first.Month + 1;

    /// <summary>
    /// Average per month over complete months only (the month of the latest transaction is still going), counted
    /// from the month the category first shows up, so a subscription that started in September isn't averaged over
    /// July and August. With nothing but the current month, that month's total.
    /// </summary>
    public static long MonthlyAverage(IReadOnlyList<DiscoveryTxn> items, DateOnly lastDate)
    {
        if (items.Count == 0)
            return 0;
        var current = new DateOnly(lastDate.Year, lastDate.Month, 1);
        var complete = items.Where(t => t.Date < current).ToList();
        if (complete.Count == 0)
            return items.Sum(t => t.AmountCents);
        var months = MonthsBetween(complete.Min(t => t.Date), current.AddDays(-1));
        return complete.Sum(t => t.AmountCents) / months;
    }

    /// <summary>The stable part of a merchant name, used as a merchant rule: up to the first store number or "*" code.</summary>
    public static string MerchantKey(string merchant)
    {
        var upper = merchant.Trim().ToUpperInvariant();
        var cut = upper.IndexOfAny(['#', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9']);
        var key = (cut > 0 ? upper[..cut] : upper).Trim().TrimEnd('-', '.', ',').Trim();
        // A trailing state code is where the card was charged, not the merchant ("TUNEWAVE NY" is "TUNEWAVE").
        var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && StateCodes.Contains(words[^1]))
            words.RemoveAt(words.Count - 1);
        key = string.Join(' ', words);
        return key.Length >= 3 ? key : upper;
    }

    private static readonly HashSet<string> StateCodes =
    [
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "DC", "FL", "GA", "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME", "MD",
        "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC", "SD",
        "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY",
    ];

    /// <summary>A readable category name for one bill: "NETFLIX.COM" → "Netflix", "VERIZON WIRELESS" → "Verizon Wireless".</summary>
    public static string BillName(string merchantKey)
    {
        var kept = new List<string>();
        foreach (var w in merchantKey.Replace("WWW.", "").Replace(".COM/BILL", "").Replace(".COM", "").Replace(".NET", "").Replace(".CO ", " ")
                     .Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Banks repeat the merchant ("BEACON FIBER BEACON FI", "BRIGHTPATH ENERGY BRIGHTPATHENERGY"): drop a word that repeats,
            // abbreviates or glues together what came before.
            if (w.EndsWith(".CO", StringComparison.Ordinal) && w.Length > 3 && kept.Count > 0 && kept[^1].StartsWith(w[..^3], StringComparison.Ordinal))
                continue; // "LEARNLY LEARNLY.CO"
            var letters = new string(w.Where(char.IsLetter).ToArray());
            var sofar = string.Concat(kept.Select(k => new string(k.Where(char.IsLetter).ToArray())));
            if (kept.Count > 0 && letters.Length > 0
                && (kept.Any(k => k.StartsWith(letters, StringComparison.Ordinal)) || sofar.StartsWith(letters, StringComparison.Ordinal)))
                continue;
            kept.Add(w);
        }
        var words = kept
            .Where(w => w is not ("INC" or "LLC" or "CO" or "CORP" or "USA" or "US" or "PAYMENT" or "PMT" or "PMTS" or "BILL" or "AUTOPAY" or "WEB"))
            .Select((w, i) => w.Contains('&') || (i == 0 && w.Length <= 3 && w.All(char.IsLetter) && w is not ("THE" or "CAR" or "GAS"))
                ? w // brand initials up front (HBO, AT&T) stay as they are
                : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant())
            .ToList();
        return words.Count == 0 ? merchantKey : string.Join(' ', words);
    }

    private static string Money(long cents) => (cents / 100m).ToString("C0", Us);
}
