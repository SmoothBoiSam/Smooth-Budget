namespace SmoothBudget.Core.Transactions;

/// <summary>What a transaction is, independent of its budget category.</summary>
public enum TransactionKind
{
    /// <summary>Money out for goods or services.</summary>
    Purchase = 0,
    /// <summary>Money back from a merchant. Reduces the spent amount of its category.</summary>
    Refund = 1,
    /// <summary>The owner's own paychecks and other deposits. Never spending.</summary>
    Income = 2,
    /// <summary>Bank fees and card interest.</summary>
    Fee = 3,
    /// <summary>Either side of a credit card payment. Never spending.</summary>
    CardPayment = 4,
    /// <summary>Either side of a move between the owner's own accounts. Never spending.</summary>
    Transfer = 5,
    Other = 6,
    /// <summary>Money in from the partner (e.g. her Zelle toward rent). Not the owner's income, never spending.</summary>
    PartnerContribution = 7,
    /// <summary>
    /// Money between the owner and the partner that you haven't labeled yet (DECISIONS.md 100). Counted nowhere until
    /// you say what it was for: then it becomes a refund in a category, her money in, or spending in a category.
    /// </summary>
    PartnerTransfer = 8,
}

/// <summary>
/// How a transaction got its category. A user's choice is never overwritten; automatic sources
/// (Bank, Learned, Rule) are re-evaluated when categories or rules change.
/// </summary>
public enum CategorySource
{
    None = 0,
    /// <summary>Mapped from the bank's own category (Plaid, or a CSV's category column).</summary>
    Bank = 1,
    /// <summary>Matched a merchant rule.</summary>
    Rule = 2,
    /// <summary>Set by transfer detection (debt payment or savings transfer).</summary>
    Transfer = 3,
    /// <summary>Set by the user. Locks both the category and the transfer flag.</summary>
    User = 4,
    /// <summary>Copied from what the user chose before for the same merchant.</summary>
    Learned = 5,
    /// <summary>
    /// You didn't remember, so the app's best guess was kept (or "Other" when it had none) (DECISIONS.md 102). Settled like
    /// your own choice, never re-sorted, but it doesn't teach the categorizer anything.
    /// </summary>
    BestGuess = 6,
}

public static class TransactionRules
{
    /// <summary>
    /// True when a transaction affects spending but has no category yet, so it belongs in the inbox.
    /// Transfers, income and partner money never need one. Refunds do, because they reduce a category's spending.
    /// </summary>
    public static bool NeedsCategory(bool hasKnownCategory, bool isTransfer, TransactionKind kind, long amountCents) =>
        !hasKnownCategory
        && !isTransfer
        && !IsMoneyIn(kind)
        && kind is not (TransactionKind.CardPayment or TransactionKind.Transfer or TransactionKind.PartnerTransfer)
        && (amountCents > 0 || kind == TransactionKind.Refund);

    /// <summary>The owner's income or partner money: never spending, never categorized.</summary>
    public static bool IsMoneyIn(TransactionKind kind) => kind is TransactionKind.Income or TransactionKind.PartnerContribution;

    /// <summary>True for automatic categories that may be recomputed (never the user's own, never transfer detection's).</summary>
    public static bool IsAutomatic(CategorySource source) =>
        source is CategorySource.None or CategorySource.Bank or CategorySource.Learned or CategorySource.Rule;
}
