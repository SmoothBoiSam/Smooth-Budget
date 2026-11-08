using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Core.Transactions;

/// <summary>A purchase as recurring detection sees it. Positive cents = money out.</summary>
public sealed record RecurringTxn(
    Guid Id, DateOnly Date, long AmountCents, string Merchant, Guid AccountId, Guid? CategoryId, CategoryType? CategoryType);

public enum Frequency { Weekly, Biweekly, Monthly, Quarterly, Yearly }

public enum RecurringKind { Bill, Subscription }

public enum RecurringStatus
{
    Active,
    /// <summary>The latest charge differs from the steady price before it.</summary>
    PriceChanged,
    /// <summary>The next charge is overdue by more than the grace period.</summary>
    PossiblyCanceled,
}

/// <param name="Key">Stable identity: the merchant key, plus the amount for one of several subscriptions at one merchant.</param>
/// <param name="AmountCents">The latest charge: the current price.</param>
/// <param name="PreviousAmountCents">The steady price before a price change (null when there's no change).</param>
/// <param name="MonthlyCents">What it costs per month at the current price (weekly × 52 ÷ 12, yearly ÷ 12, …).</param>
/// <param name="PossibleDuplicate">Another active series from the same merchant on the same schedule.</param>
public sealed record RecurringSeries(
    string Key, string Name, string MerchantKey, Frequency Frequency, RecurringKind Kind, RecurringStatus Status,
    long AmountCents, long? PreviousAmountCents, DateOnly? PriceChangedOn, long MonthlyCents,
    DateOnly FirstDate, DateOnly LastDate, DateOnly NextExpected, int Occurrences,
    Guid AccountId, Guid? CategoryId, IReadOnlyList<Guid> TransactionIds, bool PossibleDuplicate = false);

/// <summary>
/// Finds bills and subscriptions in the transactions (SPEC 4.7): the same merchant, a similar amount, at a regular
/// interval. Steps:
/// <list type="number">
/// <item>Purchases are grouped by merchant (<see cref="CategoryDiscovery.MerchantKey"/>, so store numbers don't split them).</item>
/// <item>A group is recurring when at least two-thirds of the gaps between charges fit one schedule (weekly 6-8 days,
/// every two weeks 13-16, monthly 26-35, quarterly 84-98, yearly 350-380), with at least 3 charges (2 for yearly).</item>
/// <item>Amounts must be steady: at least two-thirds of charges within ±35% of the median (±10% for weekly, so
/// grocery runs aren't mistaken for a bill). A price change is allowed: steady before, different after.</item>
/// <item>A merchant that isn't recurring as a whole is tried again per exact amount, which finds several
/// subscriptions billed by one merchant (e.g. two app subscriptions).</item>
/// </list>
/// Status: <b>price changed</b> when the latest charge differs from the steady price before it by at least 2% and 50¢;
/// <b>possibly canceled</b> when the next charge is overdue beyond a grace period (a third of the interval, at least
/// 5 days); otherwise active. The kind is a guess (bill-like themes such as rent, phone and utilities are bills;
/// exact repeating amounts are subscriptions), which the user can change.
/// </summary>
public static class RecurringDetector
{
    public const double RegularShare = 2.0 / 3.0;
    public const decimal SteadyTolerance = 0.35m;
    public const decimal WeeklyTolerance = 0.10m;

    private static readonly (Frequency F, int Min, int Max)[] Bands =
    [
        (Frequency.Weekly, 6, 8), (Frequency.Biweekly, 13, 16), (Frequency.Monthly, 26, 35),
        (Frequency.Quarterly, 84, 98), (Frequency.Yearly, 350, 380),
    ];

    public static IReadOnlyList<RecurringSeries> Detect(IReadOnlyList<RecurringTxn> transactions, DateOnly today)
    {
        var found = new List<RecurringSeries>();
        foreach (var merchant in transactions.Where(t => t.AmountCents > 0).GroupBy(t => CategoryDiscovery.MerchantKey(t.Merchant)))
        {
            // Card interest and bank fees repeat monthly but aren't bills; they belong to the debt.
            if (SpendingThemes.Match(merchant.First().Merchant)?.Name is "Fees & interest" or CardInterest.CategoryName)
                continue;
            var all = merchant.OrderBy(t => t.Date).ThenBy(t => t.Id).ToList();
            if (Series(merchant.Key, merchant.Key, all, today) is { } whole)
            {
                // Billed on more than one account, and each is regular on its own (two monthly charges on two cards
                // can interleave into what looks like every two weeks): keep them apart.
                var perAccount = all.GroupBy(t => t.AccountId).ToList();
                var split = perAccount.Count > 1
                    ? perAccount.Select(g => Series($"{merchant.Key}#{g.Key:N}", merchant.Key, g.ToList(), today)).ToList()
                    : [];
                if (split.Count > 1 && split.All(s => s is not null))
                    found.AddRange(split!);
                else
                    found.Add(whole);
                continue;
            }
            // Several subscriptions at one merchant: try each exact amount on its own, then each amount per account
            // (the same subscription billed on two cards).
            foreach (var byAmount in all.GroupBy(t => t.AmountCents).Where(g => g.Count() >= 2))
            {
                if (Series($"{merchant.Key}@{byAmount.Key}", merchant.Key, byAmount.ToList(), today) is { } s)
                {
                    found.Add(s);
                    continue;
                }
                foreach (var byAccount in byAmount.GroupBy(t => t.AccountId).Where(g => g.Count() >= 2))
                {
                    if (Series($"{merchant.Key}@{byAmount.Key}#{byAccount.Key:N}", merchant.Key, byAccount.ToList(), today) is { } a)
                        found.Add(a);
                }
            }
        }

        // Possible duplicates: two active series from one merchant on the same schedule at the same price.
        var active = found.Where(s => s.Status != RecurringStatus.PossiblyCanceled).ToList();
        var dupes = active.Where(s => active.Any(o => o.Key != s.Key && o.MerchantKey == s.MerchantKey && o.Frequency == s.Frequency
                                                      && Math.Abs(o.AmountCents - s.AmountCents) <= s.AmountCents / 100))
            .Select(s => s.Key).ToHashSet();
        return found.Select(s => dupes.Contains(s.Key) ? s with { PossibleDuplicate = true } : s)
            .OrderBy(s => s.NextExpected).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    private static RecurringSeries? Series(string key, string merchantKey, List<RecurringTxn> charges, DateOnly today)
    {
        // Two charges on the same day (a split order, a retry) count once for timing.
        var dates = charges.Select(c => c.Date).Distinct().ToList();
        if (dates.Count < 2) return null;
        var gaps = dates.Zip(dates.Skip(1), (a, b) => b.DayNumber - a.DayNumber).ToList();
        var band = Bands.Select(b => (b, Fits: gaps.Count(g => g >= b.Min && g <= b.Max)))
            .OrderByDescending(x => x.Fits).First();
        var needed = band.b.F == Frequency.Yearly ? 2 : 3;
        if (dates.Count < needed || band.Fits < Math.Ceiling(gaps.Count * RegularShare)) return null;
        // Too many charges for the schedule (e.g. a weekly store visit that happens to include monthly gaps).
        if (charges.Count > dates.Count * 1.5) return null;

        var amounts = charges.Select(c => c.AmountCents).ToList();
        var median = Median(amounts);
        var tolerance = band.b.F == Frequency.Weekly ? WeeklyTolerance : SteadyTolerance;
        bool Near(long a, long to, decimal tol) => Math.Abs(a - to) <= to * tol;
        var last = charges[^1];
        // A price change: the latest differs, but everything before it was steady around the old price.
        var before = charges.Take(charges.Count - 1).Select(c => c.AmountCents).ToList();
        var oldPrice = before.Count >= 2 ? Median(before) : (long?)null;
        var steadyBefore = oldPrice is { } op && before.TakeLast(3).All(a => Near(a, op, 0.01m));
        var changed = steadyBefore && Math.Abs(last.AmountCents - oldPrice!.Value) >= Math.Max(50, oldPrice.Value * 2 / 100);
        var steady = amounts.Count(a => Near(a, median, tolerance)) >= Math.Ceiling(amounts.Count * RegularShare) || (changed && Near(last.AmountCents, oldPrice!.Value, 0.5m));
        if (!steady) return null;

        // An amount that varies is only a bill when it looks like one (rent, phone, utilities, insurance, or filed in a bill
        // category); anything else must repeat at nearly the same price. Otherwise a regular restaurant or store visit
        // would read as a "bill".
        var theme = SpendingThemes.Match(last.Merchant);
        var billLike = BillLike(theme, last.Merchant) || last.CategoryType == CategoryType.FixedBill;
        var recent = charges.TakeLast(3).Select(c => c.AmountCents).ToList();
        var nearExact = recent.All(a => Near(a, recent[^1], 0.03m)) || changed;
        if (!billLike && !nearExact) return null;

        var next = Next(last.Date, band.b.F);
        var interval = band.b.Max;
        var grace = Math.Max(5, interval / 3);
        var status = today.DayNumber > next.DayNumber + grace ? RecurringStatus.PossiblyCanceled
            : changed ? RecurringStatus.PriceChanged
            : RecurringStatus.Active;
        var exact = before.Count >= 1 && before.TakeLast(3).All(a => a == (changed ? oldPrice : last.AmountCents));
        // Rent, phone, utilities and insurance are bills; an exact repeating price is a subscription; anything else, a bill.
        var kind = BillLike(theme, last.Merchant)
            ? RecurringKind.Bill
            : theme?.Name is "Subscriptions" or "Gaming" || exact ? RecurringKind.Subscription : RecurringKind.Bill;

        return new RecurringSeries(
            key, CategoryDiscovery.BillName(merchantKey), merchantKey, band.b.F, kind, status,
            last.AmountCents, changed ? oldPrice : null, changed ? last.Date : null, MonthlyCents(last.AmountCents, band.b.F),
            charges[0].Date, last.Date, next, charges.Count, last.AccountId, last.CategoryId, charges.Select(c => c.Id).ToList());
    }

    /// <summary>Rent, phone, utilities and insurance, by theme or by the words bills use ("INS PREM", "WIRELESS", "POWER &amp; LIGHT").</summary>
    private static bool BillLike(SpendingTheme? theme, string merchant) =>
        theme is { Name: "Rent" or "Phone" or "Utilities & internet" or "Car insurance" }
        || System.Text.RegularExpressions.Regex.IsMatch(merchant.ToUpperInvariant(), @"(?<![A-Z])(INSURANCE|INS|PREM|PREMIUM|WIRELESS|MOBILE|CELLULAR|POWER|LIGHT|ELECTRIC|WATER|SEWER|TRASH|WASTE|UTILITY|UTILITIES|MORTGAGE|HOA)(?![A-Z])");

    /// <summary>The next expected charge: same day of the month for monthly and longer schedules, else a fixed step.</summary>
    public static DateOnly Next(DateOnly last, Frequency f) => f switch
    {
        Frequency.Weekly => last.AddDays(7),
        Frequency.Biweekly => last.AddDays(14),
        Frequency.Monthly => last.AddMonths(1),
        Frequency.Quarterly => last.AddMonths(3),
        _ => last.AddYears(1),
    };

    /// <summary>What it costs per month: weekly × 52 ÷ 12, every two weeks × 26 ÷ 12, quarterly ÷ 3, yearly ÷ 12, rounded to the cent.</summary>
    public static long MonthlyCents(long amountCents, Frequency f) => f switch
    {
        Frequency.Weekly => Div(amountCents * 52, 12),
        Frequency.Biweekly => Div(amountCents * 26, 12),
        Frequency.Monthly => amountCents,
        Frequency.Quarterly => Div(amountCents, 3),
        _ => Div(amountCents, 12),
    };

    private static long Div(long a, long b) => (long)Math.Round((decimal)a / b, MidpointRounding.AwayFromZero);

    private static long Median(IReadOnlyList<long> values)
    {
        var s = values.Order().ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}
