using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Tests;

public class AutoCategorizerTests
{
    private static readonly Guid Groceries = Guid.NewGuid(), EatingOut = Guid.NewGuid(), Gas = Guid.NewGuid(), Shopping = Guid.NewGuid(),
        Entertainment = Guid.NewGuid(), Rent = Guid.NewGuid(), Transportation = Guid.NewGuid();

    private static readonly Dictionary<string, Guid> Cats = new()
    {
        ["Groceries"] = Groceries, ["Eating out"] = EatingOut, ["Gas"] = Gas, ["Shopping"] = Shopping,
        ["Entertainment"] = Entertainment, ["Rent"] = Rent,
    };

    private static CategoryGuess G(string merchant, string? label, string? primary = null, string? confidence = null,
        MerchantHistory? history = null, Dictionary<string, Guid>? cats = null) =>
        AutoCategorizer.Guess(new CategorizationInput(merchant, label, primary, confidence), history, cats ?? Cats);

    [Theory]
    [InlineData("FOOD_AND_DRINK_GROCERIES", "FOOD_AND_DRINK", "HIGH")]
    [InlineData("FOOD_AND_DRINK_GROCERIES", "FOOD_AND_DRINK", "VERY_HIGH")]
    [InlineData("FOOD_AND_DRINK_GROCERIES", "FOOD_AND_DRINK", null)]
    public void Clear_bank_categories_are_filed_without_review(string detailed, string primary, string? confidence)
    {
        var g = G("KING SOOPERS", detailed, primary, confidence);
        Assert.Equal((Groceries, CategorySource.Bank, false), (g.CategoryId, g.Source, g.Unsure));
        Assert.Equal("The bank calls it food and drink: groceries.", g.Reason);
    }

    [Fact]
    public void Low_or_medium_bank_confidence_is_filed_as_a_guess()
    {
        var g = G("CORNER SPOT", "FOOD_AND_DRINK_RESTAURANT", "FOOD_AND_DRINK", "MEDIUM");
        Assert.Equal((EatingOut, true), (g.CategoryId, g.Unsure));
        Assert.Contains("isn't sure", g.Reason);
    }

    [Fact]
    public void Ambiguous_labels_are_guesses_even_when_the_bank_is_confident()
    {
        var superstore = G("WAL-MART AURORA CO", "GENERAL_MERCHANDISE_SUPERSTORES", "GENERAL_MERCHANDISE", "VERY_HIGH");
        Assert.Equal((Shopping, true), (superstore.CategoryId, superstore.Unsure)); // could be groceries
        Assert.Contains("more than one thing", superstore.Reason);
    }

    [Fact]
    public void Falling_back_to_the_primary_category_is_a_guess()
    {
        // Detailed label not mapped (or not stored); only "food and drink" is known.
        var g = G("SOME PLACE", "FOOD_AND_DRINK", null, null);
        Assert.Equal((EatingOut, true), (g.CategoryId, g.Unsure));
        var unmappedDetail = G("SOME PLACE", "FOOD_AND_DRINK_SOMETHING_NEW", "FOOD_AND_DRINK", "HIGH");
        Assert.Equal((EatingOut, true), (unmappedDetail.CategoryId, unmappedDetail.Unsure));
    }

    [Fact]
    public void Csv_bank_categories_map_too()
    {
        Assert.Equal((Groceries, false), (G("FRESH MARKET", "Grocery").CategoryId, G("FRESH MARKET", "Grocery").Unsure));
        Assert.Equal((EatingOut, false), (G("TAQUERIA UNO", "Food & Drink").CategoryId, G("TAQUERIA UNO", "Food & Drink").Unsure));
        Assert.True(G("STREAMCO", "Entertainment").Unsure); // often a subscription
    }

    [Fact]
    public void Your_past_choices_for_a_merchant_are_learned()
    {
        var consistent = G("VOLT CHARGERS CO", "TRANSPORTATION_GAS", "TRANSPORTATION", "HIGH",
            history: new MerchantHistory("VOLT CHARGERS CO", new Dictionary<Guid, int> { [Shopping] = 3 }));
        Assert.Equal((Shopping, CategorySource.Learned, false), (consistent.CategoryId, consistent.Source, consistent.Unsure));
        Assert.Equal("You filed 3 earlier purchases here the same way.", consistent.Reason);

        var mixed = G("WAL-MART", null, history: new MerchantHistory("WAL-MART", new Dictionary<Guid, int> { [Groceries] = 4, [Shopping] = 1 }));
        Assert.Equal((Groceries, true), (mixed.CategoryId, mixed.Unsure));
        Assert.Contains("4 of 5", mixed.Reason);
    }

    [Fact]
    public void When_nothing_fits_it_suggests_a_category_to_add()
    {
        var g = G("RTD DENVER", "TRANSPORTATION_PUBLIC_TRANSIT", "TRANSPORTATION", "HIGH");
        Assert.Null(g.CategoryId);
        Assert.Equal(("Transportation", CategoryType.Variable), (g.Suggestion!.Name, g.Suggestion.Type));

        var medical = G("CLINIC", "MEDICAL", null);
        Assert.Equal("Medical", medical.Suggestion!.Name);
        Assert.Equal(CategoryType.DebtPayment, G("CAR LOAN CO", "LOAN_PAYMENTS").Suggestion!.Type);
    }

    [Fact]
    public void Once_the_suggested_category_exists_it_is_used()
    {
        var withTransport = new Dictionary<string, Guid>(Cats) { ["Transportation"] = Transportation };
        var g = G("RTD DENVER", "TRANSPORTATION_PUBLIC_TRANSIT", "TRANSPORTATION", "HIGH", cats: withTransport);
        Assert.Equal(Transportation, g.CategoryId);
        Assert.True(g.Unsure); // suggestion-only mappings are always confirmed once
    }

    [Fact]
    public void Without_categories_nothing_can_be_filed()
    {
        var g = G("KING SOOPERS", "FOOD_AND_DRINK_GROCERIES", "FOOD_AND_DRINK", "HIGH", cats: []);
        Assert.Null(g.CategoryId);
    }

    [Fact]
    public void A_csv_label_that_matches_one_of_your_category_names_is_used()
    {
        var withPets = new Dictionary<string, Guid>(Cats) { ["Pets"] = Guid.NewGuid() };
        Assert.Equal(withPets["Pets"], G("PET STORE", "Pets", cats: withPets).CategoryId);
    }
}

public class PartnerDetectorTests
{
    [Theory]
    [InlineData("Zelle payment from PARTNER DEMO 24681357", -800_00, "Partner Demo", true)]
    [InlineData("Zelle payment from  partner   demo", -50_00, "PARTNER DEMO", true)]
    [InlineData("Zelle payment to PARTNER DEMO 1234", 50_00, "PARTNER DEMO", false)] // money out is never a contribution
    [InlineData("PAYROLL DEMO AIR", -1_700_86, "PARTNER DEMO", false)]
    [InlineData("Zelle payment from PARTNER DEMO", -800_00, null, false)]
    [InlineData("Zelle payment from PARTNER DEMO", -800_00, "  ", false)]
    public void Matches_incoming_transfers_by_the_name_in_settings(string description, long cents, string? name, bool expected)
    {
        Assert.Equal(expected, PartnerDetector.IsFromPartner(description, cents, name));
    }

    [Theory]
    [InlineData("Zelle payment from PARTNER DEMO 24681357", "PARTNER DEMO")]
    [InlineData("ZELLE PAYMENT FROM JANE Q PUBLIC ON 11/20 REF ••4567", "JANE Q PUBLIC")]
    [InlineData("VENMO PAYMENT FROM SAM SAMPLE", "SAM SAMPLE")]
    [InlineData("PAYROLL DEMO AIR", null)]
    [InlineData("Zelle payment to SOMEONE ELSE", null)]
    public void Finds_the_sender_name_to_suggest(string description, string? expected)
    {
        Assert.Equal(expected, PartnerDetector.SenderName(description));
    }
}

public class GeneratorPartnerTests
{
    [Fact]
    public void Partner_contributions_are_kept_apart_from_your_income()
    {
        var cal = new BudgetMonthCalendar();
        BudgetMonthRange[] months = [cal.MonthOf(2026, 9), cal.MonthOf(2026, 10)];
        var rent = Guid.NewGuid();
        var t = months.SelectMany(m => new[]
        {
            new BudgetTransaction(null, m.Start.AddDays(1), -1_700_86, Kind: TransactionKind.Income),
            new BudgetTransaction(null, m.Start.AddDays(15), -760_00, Kind: TransactionKind.Income),
            new BudgetTransaction(null, m.Start.AddDays(14), -800_00, Kind: TransactionKind.PartnerContribution),
            new BudgetTransaction(rent, m.Start.AddDays(1), 1_650_00),
        }).ToList();

        var p = BudgetGenerator.Propose(months, [new GeneratorCategory(rent, "Rent", CategoryType.FixedBill, CategoryPriority.Medium, 1_650_00)], t, false);

        Assert.Equal(1_700_86 + 760_00, p.IncomeCents);
        Assert.Equal(800_00, p.PartnerCents);
        Assert.Equal([800_00, 800_00], p.MonthlyPartnerCents!);
        Assert.Equal(1_700_86 + 760_00 + 800_00 - 1_650_00, p.UnallocatedCents);
        Assert.DoesNotContain("partner", p.IncomeExplanation);
    }

    [Fact]
    public void Partner_money_never_needs_a_category()
    {
        Assert.False(TransactionRules.NeedsCategory(false, false, TransactionKind.PartnerContribution, -800_00));
    }
}
