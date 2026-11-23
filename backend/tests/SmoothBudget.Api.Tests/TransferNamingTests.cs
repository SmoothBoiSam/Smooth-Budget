using SmoothBudget.Api.Data;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Transactions;
using Named = SmoothBudget.Api.Transactions.TransferDetectionService.Named;

namespace SmoothBudget.Api.Tests;

/// <summary>
/// A transfer seen on one side only is filed with the account its description names (DECISIONS.md 302). All names and
/// numbers invented.
/// </summary>
public class TransferNamingTests
{
    private static readonly Guid Checking = Guid.NewGuid(), Savings = Guid.NewGuid(), Fruit = Guid.NewGuid(), Big = Guid.NewGuid();

    private static readonly Named[] Accounts =
    [
        new(Checking, AccountType.Checking, "1111", new HashSet<string> { "demo", "bigbank" }),
        new(Savings, AccountType.Savings, "2222", new HashSet<string> { "bigbank" }),
        new(Fruit, AccountType.CreditCard, "3333", new HashSet<string> { "fruit" }),
        new(Big, AccountType.CreditCard, "4444", new HashSet<string> { "bigbank" }),
    ];

    private static Transaction Row(Guid account, string text, long cents = 60_00) =>
        new() { Id = Guid.NewGuid(), AccountId = account, Description = text, Merchant = "", AmountCents = cents };

    [Fact]
    public void A_card_payment_names_its_card_by_digits_or_a_word_even_inside_a_longer_one()
    {
        Assert.Equal(Fruit, TransferDetectionService.NamedIn(Row(Checking, "FRUITCARD GSBANK PAYMENT"), TransferRole.CardPaymentSent, Accounts));
        Assert.Equal(Big, TransferDetectionService.NamedIn(Row(Checking, "PAYMENT TO CARD ENDING IN 4444"), TransferRole.CardPaymentSent, Accounts));
        Assert.Null(TransferDetectionService.NamedIn(Row(Checking, "CREDIT CARD PAYMENT"), TransferRole.CardPaymentSent, Accounts));
    }

    [Fact]
    public void A_move_between_bank_accounts_names_one_by_its_digits_or_SAV_and_CHK_not_by_the_bank_name()
    {
        Assert.Equal(Savings, TransferDetectionService.NamedIn(Row(Checking, "ONLINE TRANSFER TO SAV XXXXXX2222"), TransferRole.OwnTransferOut, Accounts));
        Assert.Equal(Savings, TransferDetectionService.NamedIn(Row(Checking, "ONLINE TRANSFER TO SAV TRANSACTION 123"), TransferRole.OwnTransferOut, Accounts));
        Assert.Equal(Checking, TransferDetectionService.NamedIn(Row(Savings, "ONLINE TRANSFER FROM CHK", -60_00), TransferRole.OwnTransferIn, Accounts));
        // The bank's name is on every account there, and a payment to someone else can read like a transfer.
        Assert.Null(TransferDetectionService.NamedIn(Row(Checking, "BIGBANK TRANSFER TO A FRIEND"), TransferRole.OwnTransferOut, Accounts));
        // Two savings accounts: "SAV" alone could be either.
        Named[] two = [.. Accounts, new(Guid.NewGuid(), AccountType.Savings, "5555", new HashSet<string>())];
        Assert.Null(TransferDetectionService.NamedIn(Row(Checking, "ONLINE TRANSFER TO SAV"), TransferRole.OwnTransferOut, two));
        // A card's own side isn't guessed here: its pair or your review says where it came from.
        Assert.Null(TransferDetectionService.NamedIn(Row(Fruit, "PAYMENT THANK YOU", -60_00), TransferRole.CardPaymentReceived, Accounts));
    }
}
