namespace SmoothBudget.Core.Budgeting;

/// <summary>Category types from SPEC section 3. Only <see cref="Variable"/> feeds Left to Spend.</summary>
public enum CategoryType
{
    FixedBill = 0,
    Variable = 1,
    DebtPayment = 2,
    SavingsTransfer = 3,
}
