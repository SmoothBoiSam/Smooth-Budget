using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Debt;

namespace SmoothBudget.Core.Transactions;

/// <summary>
/// Card interest is a bill, not spending (DECISIONS.md 159): it's charged whether or not you spend, so it has its own
/// bill line, budgeted from your card balances and APRs, and never counts toward Left to Spend.
/// </summary>
public static class CardInterest
{
    public const string CategoryName = "Card interest";

    /// <summary>Bank labels that mean interest charged: Plaid's detailed category, and the CSV label some cards use.</summary>
    private static readonly HashSet<string> Labels = new(StringComparer.OrdinalIgnoreCase) { "BANK_FEES_INTEREST_CHARGE", "Interest" };

    /// <summary>Interest charged (money out), from the merchant text or the bank's label. Interest earned (money in) isn't.</summary>
    public static bool IsCharge(string merchant, string? description, string? bankCategory, long amountCents) =>
        amountCents > 0 && (SpendingThemes.Match(merchant, description)?.Name == CategoryName
                            || bankCategory is not null && Labels.Contains(bankCategory.Trim()));

    public static bool IsInterestCategory(string name, CategoryType type) =>
        type == CategoryType.FixedBill && string.Equals(name.Trim(), CategoryName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// This month's expected interest: each card's balance at its APR ÷ 12, the same math as the payoff plan
    /// (<see cref="DebtPayoff.MonthlyInterest"/>), so the bill line and the Debt tab agree.
    /// </summary>
    public static IReadOnlyList<(DebtInput Card, long Cents)> Expected(IEnumerable<DebtInput> cards) =>
        cards.Where(c => c.BalanceCents > 0 && c.AprBps > 0)
            .Select(c => (c, DebtPayoff.MonthlyInterest(c.BalanceCents, c.AprBps)))
            .OrderByDescending(x => x.Item2).ToList();
}
