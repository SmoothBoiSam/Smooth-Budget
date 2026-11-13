using SmoothBudget.Api.Debt;

namespace SmoothBudget.Api.Tests;

/// <summary>Which card a payment was for (DECISIONS.md 244). All names and numbers invented.</summary>
public class CardPaymentsTests
{
    private static readonly Guid Synced = Guid.NewGuid(), FromCsv = Guid.NewGuid(), Checking = Guid.NewGuid();
    private static readonly Guid FruitDebt = Guid.NewGuid();

    private static readonly CardPayments.Card[] Cards =
    [
        new(Synced, "9031", new HashSet<string> { "demo", "bigbank" }, []),
        new(FromCsv, "3141", new HashSet<string> { "fruit" }, [FruitDebt]),
    ];

    private static CardPayments.Row Sent(string merchant, long cents, int day, Guid? debt = null, Guid? group = null) =>
        new(Guid.NewGuid(), new DateOnly(2026, 11, day), merchant, "", cents, Checking, false, group, debt);

    private static CardPayments.Row Received(Guid card, long cents, int day, Guid? group = null) =>
        new(Guid.NewGuid(), new DateOnly(2026, 11, day), "PAYMENT THANK YOU", "", -cents, card, true, group, null);

    [Fact]
    public void A_payment_from_checking_counts_for_the_card_it_names_until_the_card_shows_it()
    {
        // The unsynced card's statement isn't in yet: the checking side counts, tied by a word from the card's name.
        var paid = CardPayments.Attribute([Received(Synced, 74_00, 9), Sent("Fruit Card", 63_00, 10)], Cards);
        Assert.Equal([(FromCsv, 63_00L, false), (Synced, 74_00L, true)], paid.Select(p => (p.CardAccountId, p.Cents, p.FromCardSide)));

        // Once its statement shows the same payment (within 5 days), it counts once, from the card's side.
        paid = CardPayments.Attribute([Sent("Fruit Card", 63_00, 10), Received(FromCsv, 63_00, 12)], Cards);
        Assert.Equal([(FromCsv, 63_00L, true)], paid.Select(p => (p.CardAccountId, p.Cents, p.FromCardSide)));

        // Paired as a transfer: the card's side only, whatever the amounts say.
        var group = Guid.NewGuid();
        paid = CardPayments.Attribute([Sent("BIGBANK AUTOPAY", 100_00, 1, group: group), Received(Synced, 100_00, 9, group)], Cards);
        Assert.Single(paid);
    }

    [Fact]
    public void The_card_comes_from_what_you_said_then_its_last_four_then_a_word_only_one_card_has()
    {
        Assert.Equal(FromCsv, CardPayments.CardFor(Sent("ONLINE PAYMENT", 1, 1, debt: FruitDebt), Cards));
        Assert.Equal(Synced, CardPayments.CardFor(Sent("PAYMENT TO CARD ENDING IN 9031", 1, 1), Cards));
        Assert.Equal(Synced, CardPayments.CardFor(Sent("BIGBANK CREDIT CRD AUTOPAY", 1, 1), Cards));
        Assert.Null(CardPayments.CardFor(Sent("CREDIT CARD PAYMENT", 1, 1), Cards)); // says no card
        Assert.Null(CardPayments.CardFor(Sent("FRUIT BIGBANK PAYMENT", 1, 1), Cards)); // names two
    }
}
