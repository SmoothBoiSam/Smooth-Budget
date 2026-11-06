namespace SmoothBudget.Core.Transactions;

/// <summary>What a transaction is, independent of its budget category.</summary>
public enum TransactionKind
{
    /// <summary>Money out for goods or services.</summary>
    Purchase = 0,
    /// <summary>Money back from a merchant. Reduces the spent amount of its category.</summary>
    Refund = 1,
    /// <summary>Paychecks, partner money, other deposits. Never spending.</summary>
    Income = 2,
    /// <summary>Bank fees and card interest.</summary>
    Fee = 3,
    /// <summary>Either side of a credit card payment. Never spending.</summary>
    CardPayment = 4,
    /// <summary>Either side of a move between the owner's own accounts. Never spending.</summary>
    Transfer = 5,
    Other = 6,
}

/// <summary>How a transaction got its category. Higher values win; a user's choice is never overwritten.</summary>
public enum CategorySource
{
    None = 0,
    /// <summary>Mapped from the bank's own category column.</summary>
    Bank = 1,
    /// <summary>Matched a merchant rule.</summary>
    Rule = 2,
    /// <summary>Set by transfer detection (debt payment or savings transfer).</summary>
    Transfer = 3,
    /// <summary>Set by the user. Locks both the category and the transfer flag.</summary>
    User = 4,
}

public static class TransactionRules
{
    /// <summary>
    /// True when a transaction affects spending but has no category yet, so it belongs in the inbox.
    /// Transfers and income never need one. Refunds do, because they reduce a category's spending.
    /// </summary>
    public static bool NeedsCategory(bool hasKnownCategory, bool isTransfer, TransactionKind kind, long amountCents) =>
        !hasKnownCategory
        && !isTransfer
        && kind is not (TransactionKind.Income or TransactionKind.CardPayment or TransactionKind.Transfer)
        && (amountCents > 0 || kind == TransactionKind.Refund);
}
