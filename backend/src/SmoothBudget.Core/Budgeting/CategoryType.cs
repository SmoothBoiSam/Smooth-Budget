namespace SmoothBudget.Core.Budgeting;

/// <summary>Category types from SPEC section 3. Only <see cref="Variable"/> feeds Left to Spend.</summary>
public enum CategoryType
{
    FixedBill = 0,
    Variable = 1,
    DebtPayment = 2,
    SavingsTransfer = 3,
}

/// <summary>
/// How much a spending category matters when the month's money runs short (DECISIONS.md 182): the automatic budget
/// squeezes <see cref="Low"/> first, then <see cref="Medium"/>, then <see cref="High"/>. Replaces "nice-to-have".
/// </summary>
public enum CategoryPriority
{
    /// <summary>Nice to have (eating out, shopping): squeezed first, as far as what's already spent.</summary>
    Low = 0,
    /// <summary>Important: squeezed next, never below 80% of usual.</summary>
    Medium = 1,
    /// <summary>Must have (groceries, gas): squeezed last, never below 80% of usual.</summary>
    High = 2,
}
