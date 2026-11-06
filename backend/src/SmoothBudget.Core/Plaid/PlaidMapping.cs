using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Plaid;

/// <summary>Maps Plaid fields onto the app's transaction kinds and bank-category labels.</summary>
public static class PlaidMapping
{
    /// <summary>
    /// The kind of a Plaid transaction, from its personal-finance category and direction.
    /// Transfer detection still runs afterwards, exactly as for CSV imports.
    /// </summary>
    public static TransactionKind KindFor(bool accountIsCard, long amountCents, string? primary, string? detailed)
    {
        var p = primary?.ToUpperInvariant();
        var d = detailed?.ToUpperInvariant();

        if (d == "LOAN_PAYMENTS_CREDIT_CARD_PAYMENT")
            return TransactionKind.CardPayment;
        if (d is "TRANSFER_IN_ACCOUNT_TRANSFER" or "TRANSFER_OUT_ACCOUNT_TRANSFER" or "TRANSFER_IN_SAVINGS" or "TRANSFER_OUT_SAVINGS")
            return TransactionKind.Transfer;
        if (p == "BANK_FEES")
            return TransactionKind.Fee;
        // Money into a card is a merchant credit (refund). Money into a bank account is treated as income,
        // whatever category Plaid guessed: in testing, Plaid's sandbox labeled a recurring $810 deposit as
        // FOOD_AND_DRINK, which as a "refund" would have wiped out the Eating out budget.
        if (amountCents < 0)
            return accountIsCard ? TransactionKind.Refund : TransactionKind.Income;
        return TransactionKind.Purchase;
    }

    /// <summary>
    /// The label to feed the categorizer: the detailed category when the app knows it (so groceries
    /// aren't lumped in with restaurants under FOOD_AND_DRINK), otherwise the primary one.
    /// </summary>
    public static string? BankCategoryFor(string? primary, string? detailed) =>
        detailed is not null && Categorizer.DefaultBankCategoryMap.ContainsKey(detailed) ? detailed : primary;
}
