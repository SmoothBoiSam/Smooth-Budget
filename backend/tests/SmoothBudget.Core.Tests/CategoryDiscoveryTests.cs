using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Tests;

public class SpendingThemesTests
{
    [Theory]
    [InlineData("VOLT CHARGERS CHARGING CO", "EV charging")]
    [InlineData("CHARGEPOINT INC", "EV charging")]
    [InlineData("MAPLE COURT APARTMENTS", "Rent")]
    [InlineData("ENTERPRISE RENT-A-CAR", null)] // not rent
    [InlineData("VERIZON WIRELESS", "Phone")]
    [InlineData("STEAM PURCHASE", "Gaming")]
    [InlineData("STEAMBOAT GRILL", null)] // whole words only
    [InlineData("UBER TRIP", "Transit & rideshare")]
    [InlineData("UBER EATS", null)]
    [InlineData("NETFLIX.COM", "Subscriptions")]
    [InlineData("KING SOOPERS", null)]
    public void Recognizes_patterns_by_whole_words(string merchant, string? expected)
    {
        Assert.Equal(expected, SpendingThemes.Match(merchant)?.Name);
    }

    [Fact]
    public void A_pattern_beats_the_banks_label_when_you_have_that_category()
    {
        var ev = Guid.NewGuid();
        var gas = Guid.NewGuid();
        var g = AutoCategorizer.Guess(new CategorizationInput("VOLT CHARGERS CHARGING CO", "TRANSPORTATION_GAS", "TRANSPORTATION", "HIGH"), [], null,
            new Dictionary<string, Guid> { ["EV charging"] = ev, ["Gas"] = gas });
        Assert.Equal((ev, false), (g.CategoryId, g.Unsure));
    }
}

public class CategoryDiscoveryTests
{
    private static DiscoveryTxn T(int month, int day, long cents, string merchant, string? label = null, string? primary = null,
        TransactionKind kind = TransactionKind.Purchase, bool transfer = false, bool cardPayment = false, bool toSavings = false) =>
        new(new DateOnly(2026, month, day), cents, merchant, null, label, primary, kind, transfer, cardPayment, toSavings);

    /// <summary>Six months for someone with an electric car, no phone bill of their own, and a gaming habit.</summary>
    private static List<DiscoveryTxn> History()
    {
        var t = new List<DiscoveryTxn>();
        for (var m = 5; m <= 10; m++)
        {
            t.Add(T(m, 1, 1_650_00, "MAPLE COURT APARTMENTS", "RENT_AND_UTILITIES_RENT", "RENT_AND_UTILITIES"));
            t.Add(T(m, 3, 84_10, "FRESH MARKET", "FOOD_AND_DRINK_GROCERIES", "FOOD_AND_DRINK"));
            t.Add(T(m, 9, 77_45, "CORNER GROCERY", "FOOD_AND_DRINK_GROCERIES", "FOOD_AND_DRINK"));
            t.Add(T(m, 5, 18_40, "VOLT CHARGERS CHARGING CO", "TRANSPORTATION_GAS", "TRANSPORTATION")); // the bank says "gas"
            t.Add(T(m, 19, 21_10, "VOLT CHARGERS CHARGING CO", "TRANSPORTATION_GAS", "TRANSPORTATION"));
            t.Add(T(m, 12, 31_20, "TAQUERIA UNO", "FOOD_AND_DRINK_RESTAURANT", "FOOD_AND_DRINK"));
            t.Add(T(m, 14, 15_49, "STREAMCO NETFLIX", "ENTERTAINMENT_TV_AND_MOVIES", "ENTERTAINMENT"));
            t.Add(T(m, 20, 300_00, "PAYMENT TO CARD", kind: TransactionKind.CardPayment, transfer: true, cardPayment: true));
            if (m % 2 == 0)
                t.Add(T(m, 22, 24_99, "STEAM PURCHASE", "ENTERTAINMENT_VIDEO_GAMES", "ENTERTAINMENT"));
            t.Add(T(m, 1, -1_700_86, "PAYROLL", kind: TransactionKind.Income));
        }
        t.Add(T(7, 2, 4_50, "STARBUCKS", "FOOD_AND_DRINK_COFFEE", "FOOD_AND_DRINK")); // a single coffee: no Coffee category
        t.Add(T(8, 2, 9_99, "MYSTERY THING")); // no label, no pattern
        return t;
    }

    private static readonly ExistingCategory[] Starter =
    [
        new(Guid.NewGuid(), "Gas", CategoryType.Variable, false, 0),
        new(Guid.NewGuid(), "Phone", CategoryType.FixedBill, false, 0),
        new(Guid.NewGuid(), "Groceries", CategoryType.Variable, false, 0),
        new(Guid.NewGuid(), "Personal care", CategoryType.Variable, false, 2), // you filed two things here yourself: keep
    ];

    [Fact]
    public void Proposes_only_categories_you_actually_spend_in_plus_your_own_patterns()
    {
        var r = CategoryDiscovery.Discover(History(), Starter);
        var names = r.Proposed.Select(p => p.Name).ToList();

        Assert.Contains("Groceries", names);
        Assert.Contains("Eating out", names);
        Assert.Contains("Rent", names);
        Assert.Contains("Streamco Netflix", names);      // each subscription is its own line
        Assert.DoesNotContain("Subscriptions", names);
        Assert.Contains("EV charging", names);
        Assert.Contains("Gaming", names);
        Assert.Contains("Credit card payment", names);
        Assert.Contains("Rainy-day fund", names);
        Assert.DoesNotContain("Gas", names);   // charging only, even though the bank labels it gas
        Assert.DoesNotContain("Phone", names); // no phone bill
        Assert.DoesNotContain("Coffee", names); // one coffee isn't a pattern
        Assert.Equal(6, r.MonthsAnalyzed);
        Assert.Equal(1, r.Unmatched);
    }

    [Fact]
    public void Each_proposal_explains_itself()
    {
        var ev = CategoryDiscovery.Discover(History(), []).Proposed.Single(p => p.Name == "EV charging");
        Assert.True(ev.Custom);
        Assert.Equal((12, 6, CategoryType.Variable), (ev.TransactionCount, ev.MonthsSeen, ev.Type));
        Assert.Equal(["VOLT CHARGERS CHARGING CO"], ev.Examples);
        Assert.Equal(39_50, ev.MonthlyCents); // (18.40 + 21.10) a month
        Assert.Equal("12 purchases in 6 months, about $40 a month (a pattern in your spending).", ev.Reason);

        var rent = CategoryDiscovery.Discover(History(), []).Proposed.Single(p => p.Name == "Rent");
        Assert.Equal(CategoryType.FixedBill, rent.Type);
    }

    [Fact]
    public void Unused_existing_categories_are_offered_for_hiding_but_ones_you_use_are_kept()
    {
        var r = CategoryDiscovery.Discover(History(), Starter);
        Assert.Equal(["Gas", "Phone"], r.Unused.Select(u => u.Name).Order());
        Assert.DoesNotContain(r.Unused, u => u.Name == "Groceries");
        Assert.DoesNotContain(r.Unused, u => u.Name == "Personal care");
    }

    [Fact]
    public void A_frequent_coffee_habit_becomes_its_own_category()
    {
        var t = History();
        for (var i = 0; i < 10; i++)
            t.Add(T(5 + i % 6, 10 + i % 15, 5_75, "STARBUCKS", "FOOD_AND_DRINK_COFFEE", "FOOD_AND_DRINK"));
        Assert.Contains(CategoryDiscovery.Discover(t, []).Proposed, p => p.Name == "Coffee" && p.IsDiscretionary);
    }

    [Fact]
    public void One_off_purchases_do_not_create_categories()
    {
        var t = new List<DiscoveryTxn> { T(6, 1, 40_00, "CLINIC", "MEDICAL") };
        Assert.DoesNotContain(CategoryDiscovery.Discover(t, [], includeRainyDayGoal: false).Proposed, p => p.Name == "Medical");
        t.Add(T(9, 1, 200_00, "HOSPITAL", "MEDICAL")); // two visits, $240 total
        Assert.Contains(CategoryDiscovery.Discover(t, [], includeRainyDayGoal: false).Proposed, p => p.Name == "Medical");
    }

    [Fact]
    public void Ordered_like_the_budget_spending_first()
    {
        var types = CategoryDiscovery.Discover(History(), []).Proposed.Select(p => p.Type).ToList();
        Assert.Equal(types.OrderBy(t => t switch { CategoryType.Variable => 0, CategoryType.FixedBill => 1, CategoryType.DebtPayment => 2, _ => 3 }), types);
    }

    [Fact]
    public void What_you_confirmed_beats_patterns_and_bank_labels()
    {
        // Walmart with no bank label, confirmed as Household by hand three months running; and a bank-labeled "Gas"
        // charger the user confirmed as EV charging.
        var txns = new List<DiscoveryTxn>();
        foreach (var m in new[] { 8, 9, 10 })
        {
            txns.Add(T(m, 5, 60_00, "WAL-MART #1234") with { ConfirmedName = "Household", ConfirmedDiscretionary = true });
            txns.Add(T(m, 9, 30_00, "CITY GARAGE CHARGER", label: "Gas") with { ConfirmedName = "EV charging" });
        }

        var r = CategoryDiscovery.Discover(txns, []);
        Assert.Equal(["Household", "EV charging"], r.Proposed.Where(p => p.Name != "Rainy-day fund").Select(p => p.Name));
        Assert.True(r.Proposed[0].IsDiscretionary);
        Assert.Equal(0, r.Unmatched);
    }

    [Fact]
    public void Each_bill_and_subscription_gets_its_own_line_split_out_of_a_grouped_one()
    {
        var txns = new List<DiscoveryTxn>();
        foreach (var m in new[] { 8, 9, 10 })
        {
            txns.Add(T(m, 3, 15_49, "NETFLIX.COM"));
            txns.Add(T(m, 5, 11_99, "SPOTIFY USA"));
            txns.Add(T(m, 7, 80_00, "BRIGHTPATH ENERGY 5550001 PMT"));
            txns.Add(T(m, 9, 60_00, "COMCAST CABLE"));
        }
        txns.Add(T(11, 3, 15_49, "NETFLIX.COM"));
        var grouped = new ExistingCategory(Guid.NewGuid(), "Subscriptions", CategoryType.FixedBill, false, 5);

        var r = CategoryDiscovery.Discover(txns, [grouped], includeRainyDayGoal: false);

        Assert.Equal(["Brightpath Energy", "Comcast Cable", "Netflix", "Spotify"], r.Proposed.Select(p => p.Name));
        Assert.All(r.Proposed, p => Assert.Equal(CategoryType.FixedBill, p.Type));
        var netflix = r.Proposed.Single(p => p.Name == "Netflix");
        Assert.Equal(("NETFLIX.COM", "Subscriptions", 15_49L), (netflix.MerchantPattern, netflix.Replaces, netflix.MonthlyCents));
        Assert.Equal("BRIGHTPATH ENERGY", r.Proposed[0].MerchantPattern);
        Assert.Null(r.Proposed[0].Replaces); // you had no "Utilities & internet" to split
        // Your grouped Subscriptions is offered for hiding even though you filed things there yourself.
        Assert.Equal("Replaced by one line per bill above.", Assert.Single(r.Unused).Reason);
    }

    [Fact]
    public void A_single_bill_keeps_its_plain_name()
    {
        var txns = new[] { 8, 9, 10 }.Select(m => T(m, 1, 1650_00, "MAPLE COURT APARTMENTS")).ToList();
        var rent = CategoryDiscovery.Discover(txns, [], includeRainyDayGoal: false).Proposed.Single();
        Assert.Equal(("Rent", (string?)null), (rent.Name, rent.MerchantPattern));
    }

    [Fact]
    public void Monthly_averages_use_complete_months_from_when_it_started()
    {
        // Rent in July..October, and today is early November: 4 complete months, not 5.
        var rent = new[] { 7, 8, 9, 10 }.Select(m => T(m, 1, 1650_00, "MAPLE COURT")).Append(T(11, 2, 20_00, "OTHER")).ToList();
        Assert.Equal(1650_00, CategoryDiscovery.MonthlyAverage(rent.Take(4).ToList(), new DateOnly(2026, 11, 2)));
        // A subscription that started in September isn't averaged over July and August.
        var sub = new[] { 9, 10 }.Select(m => T(m, 1, 10_00, "NEWSUB")).ToList();
        Assert.Equal(10_00, CategoryDiscovery.MonthlyAverage(sub, new DateOnly(2026, 11, 2)));
        // Only the current month so far: its total.
        Assert.Equal(20_00, CategoryDiscovery.MonthlyAverage([T(11, 2, 20_00, "X")], new DateOnly(2026, 11, 2)));
    }

    [Theory]
    [InlineData("WAL-MART #1234", "WAL-MART")]
    [InlineData("BRIGHTPATH ENERGY 5550001 PMT", "BRIGHTPATH ENERGY")]
    [InlineData("APPLE.COM/BILL", "APPLE.COM/BILL")]
    [InlineData("SQ *CORNER CAFE", "SQ *CORNER CAFE")] // too short a prefix: keep it whole
    [InlineData("TUNEWAVE NY", "TUNEWAVE")] // a trailing state code is where it was charged
    [InlineData("CITY OF DENVER CO", "CITY OF DENVER")]
    public void Merchant_keys_drop_store_numbers(string merchant, string key) => Assert.Equal(key, CategoryDiscovery.MerchantKey(merchant));

    [Theory]
    [InlineData("NETFLIX.COM", "Netflix")]
    [InlineData("VERIZON WIRELESS", "Verizon Wireless")]
    [InlineData("AT&T", "AT&T")]
    [InlineData("HBO MAX", "HBO Max")]
    [InlineData("BEACON FIBER BEACON FI", "Beacon Fiber")]
    [InlineData("BRIGHTPATH ENERGY BRIGHTPATHENERGY", "Brightpath Energy")]
    [InlineData("LEARNLY LEARNLY.CO", "Learnly")]
    [InlineData("APPSTORE.COM/BILL", "Appstore")]
    public void Bill_names_read_well(string key, string name) => Assert.Equal(name, CategoryDiscovery.BillName(key));
}
