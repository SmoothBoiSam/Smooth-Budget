using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Tests;

public class CategorizerTests
{
    private static readonly Guid Groceries = Guid.NewGuid();
    private static readonly Guid EatingOut = Guid.NewGuid();
    private static readonly Guid Coffee = Guid.NewGuid();
    private static readonly Guid Gas = Guid.NewGuid();

    private static readonly Dictionary<string, Guid> Categories = new()
    {
        ["Groceries"] = Groceries, ["Eating out"] = EatingOut, ["Coffee"] = Coffee, ["Gas"] = Gas,
    };

    private static (Guid?, CategorySource) Guess(string merchant, string? bank)
    {
        var g = AutoCategorizer.Guess(new CategorizationInput(merchant, bank), null, Categories);
        return (g.CategoryId, g.Source);
    }

    [Fact]
    public void Bank_category_maps_to_the_households_category()
    {
        Assert.Equal((Groceries, CategorySource.Bank), Guess("FRESH MARKET", "Grocery"));
        Assert.Equal((EatingOut, CategorySource.Bank), Guess("TAQUERIA UNO", "Food & Drink"));
        Assert.Equal((Gas, CategorySource.Bank), Guess("FUEL STOP", "gas")); // any case
    }

    [Fact]
    public void Unknown_or_missing_bank_category_leaves_it_uncategorized()
    {
        Assert.Equal((null, CategorySource.None), Guess("LAWYER", "Professional Services"));
        Assert.Equal((null, CategorySource.None), Guess("KING SOOPERS DENVER CO", null));
    }

}

public class TransactionRulesTests
{
    [Theory]
    [InlineData(false, false, TransactionKind.Purchase, 10_00, true)]
    [InlineData(true, false, TransactionKind.Purchase, 10_00, false)]   // already categorized
    [InlineData(false, true, TransactionKind.Purchase, 10_00, false)]   // transfer
    [InlineData(false, false, TransactionKind.Income, -800_00, false)]  // partner money, paycheck
    [InlineData(false, false, TransactionKind.CardPayment, 300_00, false)]
    [InlineData(false, false, TransactionKind.Refund, -12_00, true)]    // refunds reduce a category, so they need one
    [InlineData(false, false, TransactionKind.Fee, 95_00, true)]
    [InlineData(false, false, TransactionKind.Purchase, -5_00, false)]  // unexplained money in
    public void NeedsCategory(bool hasCategory, bool isTransfer, TransactionKind kind, long cents, bool expected)
    {
        Assert.Equal(expected, TransactionRules.NeedsCategory(hasCategory, isTransfer, kind, cents));
    }
}

public class TransferDetectorTests
{
    private static readonly Guid Checking = Guid.NewGuid();
    private static readonly Guid Savings = Guid.NewGuid();
    private static readonly Guid ChaseCard = Guid.NewGuid();
    private static readonly Guid AppleCard = Guid.NewGuid();

    private static TransferCandidate T(Guid account, int day, long cents, TransactionKind kind = TransactionKind.Purchase, string desc = "X", bool locked = false) =>
        new(Guid.NewGuid(), account,
            account == ChaseCard || account == AppleCard ? AccountKind.CreditCard : account == Savings ? AccountKind.Savings : AccountKind.Checking,
            new DateOnly(2026, 11, day), cents, kind, desc, null, locked);

    private static TransferDecision? For(IEnumerable<TransferDecision> ds, TransferCandidate t) => ds.SingleOrDefault(d => d.Id == t.Id);

    [Fact]
    public void A_card_payment_and_the_matching_checking_debit_are_paired_and_neither_is_spending()
    {
        var sent = T(Checking, 14, 300_00, TransactionKind.CardPayment, "Payment to Chase card ending in 4321");
        var received = T(ChaseCard, 14, -300_00, TransactionKind.CardPayment, "Payment Thank You-Mobile");

        var ds = TransferDetector.Detect([sent, received]);

        Assert.Equal(TransferRole.CardPaymentSent, For(ds, sent)!.Role);
        Assert.Equal(TransferRole.CardPaymentReceived, For(ds, received)!.Role);
        Assert.NotNull(For(ds, sent)!.GroupId);
        Assert.Equal(For(ds, sent)!.GroupId, For(ds, received)!.GroupId);
        Assert.Equal(AccountKind.CreditCard, For(ds, sent)!.CounterpartAccount);
    }

    [Fact]
    public void A_checking_debit_with_an_unknown_description_is_caught_by_pairing_with_the_cards_payment_row()
    {
        var sent = T(Checking, 18, 75_00, TransactionKind.Purchase, "DEMO CARD SVCS ONLINE PMT");
        var received = T(AppleCard, 19, -75_00, TransactionKind.CardPayment, "ACH DEPOSIT INTERNET TRANSFER");

        var ds = TransferDetector.Detect([sent, received]);

        Assert.Equal(TransferRole.CardPaymentSent, For(ds, sent)!.Role);
        Assert.Equal(TransferRole.CardPaymentReceived, For(ds, received)!.Role);
    }

    [Theory]
    [InlineData("APPLECARD GSBANK PAYMENT ••8901 WEB ID: ••9999")]
    [InlineData("CHASE CREDIT CRD AUTOPAY PPD ID: ••3456")]
    [InlineData("Payment to Chase card ending in 4321 11/14")]
    [InlineData("AMZ_STORECRD_PMT PAYMENT ••4321 WEB ID: ••1234")] // a store card that isn't imported
    public void Known_card_payment_descriptions_are_transfers_even_when_the_card_file_is_not_imported(string desc)
    {
        var sent = T(Checking, 15, 150_00, TransactionKind.Purchase, desc);
        Assert.Equal(TransferRole.CardPaymentSent, For(TransferDetector.Detect([sent]), sent)!.Role);
    }

    [Fact]
    public void A_cards_payment_row_is_a_transfer_even_when_the_bank_file_is_not_imported()
    {
        var received = T(AppleCard, 15, -150_00, TransactionKind.CardPayment);
        var d = For(TransferDetector.Detect([received]), received)!;
        Assert.Equal(TransferRole.CardPaymentReceived, d.Role);
        Assert.Null(d.GroupId);
    }

    [Fact]
    public void Utility_autopay_is_not_mistaken_for_a_card_payment()
    {
        var bill = T(Checking, 3, 68_40, TransactionKind.Purchase, "CITY POWER AUTOPAY");
        Assert.Empty(TransferDetector.Detect([bill]));
    }

    [Fact]
    public void An_ordinary_purchase_is_never_paired_with_a_refund_of_the_same_amount()
    {
        var purchase = T(Checking, 10, 50_00, TransactionKind.Purchase, "BIG BOX STORE");
        var refund = T(ChaseCard, 11, -50_00, TransactionKind.Refund, "BIG BOX STORE RETURN");
        Assert.Empty(TransferDetector.Detect([purchase, refund]));
    }

    [Fact]
    public void Own_account_transfers_pair_between_checking_and_savings()
    {
        var outflow = T(Checking, 21, 100_00, TransactionKind.Transfer, "Online Transfer to SAV ...9876");
        var inflow = T(Savings, 21, -100_00, TransactionKind.Transfer, "Online Transfer from CHK ...1111");

        var ds = TransferDetector.Detect([outflow, inflow]);

        Assert.Equal(TransferRole.OwnTransferOut, For(ds, outflow)!.Role);
        Assert.Equal(TransferRole.OwnTransferIn, For(ds, inflow)!.Role);
        Assert.Equal(AccountKind.Savings, For(ds, outflow)!.CounterpartAccount);
    }

    [Fact]
    public void Pairs_further_apart_than_the_window_are_not_matched()
    {
        var sent = T(Checking, 1, 75_00, TransactionKind.Purchase, "DEMO CARD SVCS ONLINE PMT");
        var received = T(AppleCard, 1 + TransferDetector.MatchWindowDays + 1, -75_00, TransactionKind.CardPayment);

        var ds = TransferDetector.Detect([sent, received]);

        Assert.Null(For(ds, sent)); // stays a purchase; the user will see it in the inbox
        Assert.Null(For(ds, received)!.GroupId);
    }

    [Fact]
    public void Two_equal_payments_pair_with_the_closest_dates()
    {
        var sentA = T(Checking, 1, 200_00, TransactionKind.CardPayment, "Payment to Chase card");
        var sentB = T(Checking, 9, 200_00, TransactionKind.CardPayment, "Payment to Chase card");
        var recvB = T(ChaseCard, 10, -200_00, TransactionKind.CardPayment);
        var recvA = T(ChaseCard, 2, -200_00, TransactionKind.CardPayment);

        var ds = TransferDetector.Detect([sentB, recvB, sentA, recvA]);

        Assert.Equal(For(ds, sentA)!.GroupId, For(ds, recvA)!.GroupId);
        Assert.Equal(For(ds, sentB)!.GroupId, For(ds, recvB)!.GroupId);
        Assert.NotEqual(For(ds, sentA)!.GroupId, For(ds, sentB)!.GroupId);
    }

    [Fact]
    public void Rows_the_user_locked_are_never_changed_or_paired()
    {
        var sent = T(Checking, 14, 300_00, TransactionKind.CardPayment, "Payment to Chase card", locked: true);
        var received = T(ChaseCard, 14, -300_00, TransactionKind.CardPayment);

        var ds = TransferDetector.Detect([sent, received]);

        Assert.Null(For(ds, sent));
        Assert.Null(For(ds, received)!.GroupId);
    }

    [Fact]
    public void Existing_pairs_are_kept_and_not_reused()
    {
        var group = Guid.NewGuid();
        var sent = T(Checking, 14, 300_00, TransactionKind.CardPayment, "Payment to Chase card") with { TransferGroupId = group };
        var received = T(ChaseCard, 14, -300_00, TransactionKind.CardPayment) with { TransferGroupId = group };
        var another = T(ChaseCard, 15, -300_00, TransactionKind.CardPayment);

        var ds = TransferDetector.Detect([sent, received, another]);

        Assert.Equal(group, For(ds, sent)!.GroupId);
        Assert.Equal(group, For(ds, received)!.GroupId);
        Assert.Null(For(ds, another)!.GroupId);
    }

    [Fact]
    public void Income_into_checking_is_not_a_transfer()
    {
        var zelle = T(Checking, 20, -800_00, TransactionKind.Income, "Zelle payment from PARTNER DEMO");
        Assert.Empty(TransferDetector.Detect([zelle]));
    }
}
