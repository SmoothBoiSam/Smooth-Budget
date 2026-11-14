using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Budgeting;

/// <summary>A category as the calculator sees it. Money is integer cents.</summary>
/// <param name="CardInterest">The Card interest bill line (DECISIONS.md 159, 172): its interest is part of the card minimums, so it
/// and the debt lines are one pot of money (DECISIONS.md 254).</param>
public sealed record BudgetCategory(Guid Id, CategoryType Type, long MonthlyLimitCents, bool CardInterest = false);

/// <summary>
/// A transaction as the calculator sees it.
/// Sign convention: positive <see cref="AmountCents"/> is money out (a purchase),
/// negative is money in (a refund, paycheck, or transfer in).
/// <see cref="SplitFactor"/> is the owner's share (1.0 = all of it, 0.5 = half).
/// </summary>
public sealed record BudgetTransaction(
    Guid? CategoryId,
    DateOnly Date,
    long AmountCents,
    bool IsTransfer = false,
    bool Pending = false,
    decimal SplitFactor = 1m,
    TransactionKind Kind = TransactionKind.Purchase,
    // Charged to a credit card account (its purchases are what the card payments pay for).
    bool OnCreditCard = false);

public enum LeftToSpendStatus
{
    /// <summary>At least 15% of the variable budget is left.</summary>
    Healthy,
    /// <summary>Under 15% of the variable budget is left, but not overspent.</summary>
    Low,
    /// <summary>Left to Spend is below zero.</summary>
    Overspent,
}

public sealed record CategorySpend(Guid CategoryId, long LimitCents, long SpentCents)
{
    /// <summary>Limit minus spent. Negative means over budget.</summary>
    public long RemainingCents => LimitCents - SpentCents;
    public bool IsOver => SpentCents > LimitCents;
}

public sealed record LeftToSpendResult(
    BudgetMonthRange Month,
    DateOnly Today,
    long LeftToSpendCents,
    long TotalVariableLimitCents,
    long TotalVariableSpentCents,
    long CarriedInDeficitCents,
    int OverBudgetCategoryCount,
    long OverBudgetCents,
    int DaysRemaining,
    long SafeToSpendPerDayCents,
    LeftToSpendStatus Status,
    int UncategorizedSpendingCount,
    IReadOnlyList<CategorySpend> Categories,
    // A late or short partner contribution (SPEC section 3): Left to Spend is recomputed without it.
    long PartnerShortfallCents = 0,
    // Spending that isn't filed in a category yet (and money sent to your partner waiting for a label): already spent,
    // so it comes off Left to Spend until you file it (DECISIONS.md 177).
    long UnfiledSpendingCents = 0,
    // What the month's plan needs beyond the money coming in: bills, card and savings at least what's already paid,
    // plus every spending limit, less expected income and partner money (DECISIONS.md 178).
    long OverplannedCents = 0,
    // What's still more than the bank accounts can cover once this month's bills, card payoffs and savings are set aside
    // (DECISIONS.md 183), and the check itself. Null when the app can't see every checking balance.
    long CashShortCents = 0,
    CashCheck? Cash = null);

/// <summary>
/// The bank side of the month, from the accounts (DECISIONS.md 183). Card charges and payments decide what the cards
/// still need from checking: what you charged since the payday before this month's first one (the payday plan's
/// "Pay off what you charged") plus the planned card payment, less what's been paid to the cards this month.
/// </summary>
/// <param name="CheckingCents">Every checking account's balance from the bank.</param>
/// <param name="IncomeStillComingCents">The month's expected income not landed yet.</param>
/// <param name="PartnerStillComingCents">The partner's money for this month not arrived yet.</param>
/// <param name="HasCards">False with no credit cards: the debt line is then paid like a bill.</param>
/// <param name="CardChargesCents">Purchases less refunds on the cards since the card window started.</param>
/// <param name="CardPaymentsCents">Paid to the cards from checking this month.</param>
/// <param name="CardsOwedCents">What the cards' balances say is owed, when every card's balance is known: the cards
/// never need more than that.</param>
/// <param name="LeftoverToMoveCents">Rollover sweeps from a closed month not sent yet.</param>
public sealed record CashInput(
    long CheckingCents, long IncomeStillComingCents, long PartnerStillComingCents,
    bool HasCards, long CardChargesCents, long CardPaymentsCents, long? CardsOwedCents, long LeftoverToMoveCents);

/// <summary>What the accounts can cover for spending the rest of the month: money there and still coming, less what must still go out.</summary>
public sealed record CashCheck(
    long CheckingCents, long IncomeStillComingCents, long PartnerStillComingCents,
    long BillsStillDueCents, long CardsToPayCents, long SavingsStillToMoveCents, long LeftoverToMoveCents)
{
    public long AvailableCents => CheckingCents + IncomeStillComingCents + PartnerStillComingCents
                                  - BillsStillDueCents - CardsToPayCents - SavingsStillToMoveCents - LeftoverToMoveCents;
}

/// <summary>
/// Computes the headline number (SPEC section 3, "Left to Spend"):
/// <code>Left to Spend = Σ over variable categories (limit − spent) − carried-in deficit − late partner money
///                 − spending not filed yet − plan beyond the month's money</code>
/// The last two (DECISIONS.md 177-178) keep it from ever showing money that's already gone or never coming.
/// Rules applied:
/// <list type="bullet">
/// <item>Only transactions dated inside the budget month count.</item>
/// <item>Pending transactions count immediately.</item>
/// <item>Transfers (card payments, own-account moves, savings transfers) never count as spending.</item>
/// <item>Refunds (negative amounts) reduce a category's spent amount.</item>
/// <item>Only the owner's share (<see cref="BudgetTransaction.SplitFactor"/>) counts.</item>
/// <item>Overspending in one category reduces the shared total.</item>
/// </list>
/// </summary>
public static class LeftToSpendCalculator
{
    /// <summary>Below this share of the variable budget, the status turns from Healthy to Low.</summary>
    public const int LowThresholdPercent = 15;

    public static LeftToSpendResult Calculate(
        BudgetMonthRange month,
        DateOnly today,
        IEnumerable<BudgetCategory> categories,
        IEnumerable<BudgetTransaction> transactions,
        long carriedInDeficitCents,
        long partnerShortfallCents = 0,
        long moneyInCents = 0,
        CashInput? cash = null)
    {
        ArgumentNullException.ThrowIfNull(month);
        ArgumentOutOfRangeException.ThrowIfNegative(partnerShortfallCents);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(transactions);
        var allCategories = categories.ToList();
        var allTransactions = transactions.ToList();
        categories = allCategories;
        transactions = allTransactions;
        if (carriedInDeficitCents < 0)
            throw new ArgumentOutOfRangeException(nameof(carriedInDeficitCents), "A carried-in deficit is a positive amount of cents.");

        var daysRemaining = month.DaysRemaining(today); // throws if today is outside the month

        var variable = categories
            .Where(c => c.Type == CategoryType.Variable)
            .ToDictionary(c => c.Id);
        var knownCategoryIds = categories.Select(c => c.Id).ToHashSet();

        var spentByCategory = variable.Keys.ToDictionary(id => id, _ => 0L);
        var uncategorized = 0;

        foreach (var t in transactions)
        {
            if (!month.Contains(t.Date) || t.IsTransfer)
                continue;

            if (t.CategoryId is null || !knownCategoryIds.Contains(t.CategoryId.Value))
            {
                if (TransactionRules.NeedsCategory(hasKnownCategory: false, t.IsTransfer, t.Kind, t.AmountCents))
                    uncategorized++;
                continue;
            }

            if (spentByCategory.TryGetValue(t.CategoryId.Value, out var spent))
                spentByCategory[t.CategoryId.Value] = spent + OwnerShare(t);
        }

        var lines = variable.Values
            .Select(c => new CategorySpend(c.Id, c.MonthlyLimitCents, FloorAtZero(spentByCategory[c.Id])))
            .ToList();

        var totalLimit = lines.Sum(l => l.LimitCents);
        var totalSpent = lines.Sum(l => l.SpentCents);
        var unfiled = UnfiledSpending(month, allCategories, allTransactions);
        var overplanned = Overplanned(month, allCategories, allTransactions, moneyInCents);
        var leftToSpend = lines.Sum(l => l.RemainingCents) - carriedInDeficitCents - partnerShortfallCents - unfiled - overplanned;
        var check = cash is null ? null : Check(month, allCategories, allTransactions, cash);
        var cashShort = check is null ? 0 : Math.Max(0, leftToSpend - check.AvailableCents);
        leftToSpend -= cashShort;
        var over = lines.Where(l => l.IsOver).ToList();

        return new LeftToSpendResult(
            Month: month,
            Today: today,
            LeftToSpendCents: leftToSpend,
            TotalVariableLimitCents: totalLimit,
            TotalVariableSpentCents: totalSpent,
            CarriedInDeficitCents: carriedInDeficitCents,
            OverBudgetCategoryCount: over.Count,
            OverBudgetCents: over.Sum(l => -l.RemainingCents),
            DaysRemaining: daysRemaining,
            SafeToSpendPerDayCents: leftToSpend <= 0 ? 0 : leftToSpend / daysRemaining, // integer division floors: never suggest more than is safe
            Status: StatusFor(leftToSpend, totalLimit),
            UncategorizedSpendingCount: uncategorized,
            Categories: lines,
            PartnerShortfallCents: partnerShortfallCents,
            UnfiledSpendingCents: unfiled,
            OverplannedCents: overplanned,
            CashShortCents: cashShort,
            Cash: check);
    }

    /// <summary>
    /// Checks the month against the bank (DECISIONS.md 183): checking now, plus income and partner money still coming, less
    /// each bill and savings line not paid yet, what the cards still need, and leftover still to move. Card purchases
    /// already count in their categories, but the money for them is still in checking until the card is paid, so it's set
    /// aside here; without this, last month's card charges looked like money to spend.
    /// </summary>
    public static CashCheck Check(BudgetMonthRange month, IReadOnlyCollection<BudgetCategory> categories, IEnumerable<BudgetTransaction> transactions, CashInput cash)
    {
        var lines = CategoryBreakdownCalculator.Calculate(month, categories, transactions);
        long Unpaid(CategoryType type) => lines.Where(l => l.Type == type).Sum(l => Math.Max(0, l.LimitCents - l.SpentCents));
        var cards = Unpaid(CategoryType.DebtPayment);
        if (cash.HasCards)
        {
            cards = Math.Max(0, lines.Where(l => l.Type == CategoryType.DebtPayment).Sum(l => l.LimitCents) + cash.CardChargesCents - cash.CardPaymentsCents);
            if (cash.CardsOwedCents is { } owed)
                cards = Math.Min(cards, Math.Max(0, owed));
        }
        return new CashCheck(cash.CheckingCents, cash.IncomeStillComingCents, cash.PartnerStillComingCents,
            Unpaid(CategoryType.FixedBill), cards, Unpaid(CategoryType.SavingsTransfer), cash.LeftoverToMoveCents);
    }

    /// <summary>
    /// Spending this month with no category (or one that isn't in the budget, like a hidden one), plus money you sent your
    /// partner that's waiting for a label (DECISIONS.md 177). It's gone either way, so it counts until it's filed; never
    /// below zero, so an unfiled refund can't add money.
    /// </summary>
    public static long UnfiledSpending(BudgetMonthRange month, IReadOnlyCollection<BudgetCategory> categories, IEnumerable<BudgetTransaction> transactions)
    {
        var known = categories.Select(c => c.Id).ToHashSet();
        var total = transactions
            .Where(t => month.Contains(t.Date) && !t.IsTransfer && (t.CategoryId is null || !known.Contains(t.CategoryId.Value))
                        && (TransactionRules.NeedsCategory(false, t.IsTransfer, t.Kind, t.AmountCents)
                            || (t.Kind == TransactionKind.PartnerTransfer && t.AmountCents > 0)))
            .Sum(OwnerShare);
        return Math.Max(0, total);
    }

    /// <summary>
    /// How far the month's plan goes beyond the money coming in (DECISIONS.md 178): every spending limit, plus each bill,
    /// card and savings line at its limit or what's already been paid on it if more, less expected income and partner
    /// money. Zero when the plan fits, or when no income is on record (<paramref name="moneyInCents"/> 0) to compare with.
    /// </summary>
    public static long Overplanned(BudgetMonthRange month, IReadOnlyCollection<BudgetCategory> categories, IEnumerable<BudgetTransaction> transactions, long moneyInCents)
    {
        if (moneyInCents <= 0)
            return 0;
        var lines = CategoryBreakdownCalculator.Calculate(month, categories, transactions);
        // The debt lines and Card interest are one pot (DECISIONS.md 254): a payment made before the month's interest posts
        // sits on the payment line, and counting each line at its own "limit or paid" counted that interest twice.
        var pool = CategoryBreakdownCalculator.CardPool(categories);
        var cards = lines.Where(l => pool.Contains(l.CategoryId)).ToList();
        var planned = lines.Where(l => !pool.Contains(l.CategoryId))
                          .Sum(l => l.Type == CategoryType.Variable ? l.LimitCents : Math.Max(l.LimitCents, l.SpentCents))
                      + Math.Max(cards.Sum(l => l.LimitCents), cards.Sum(l => l.SpentCents));
        return Math.Max(0, planned - moneyInCents);
    }

    /// <summary>
    /// Refunds can bring a category's spending down to zero but never below: a large or mislabeled credit
    /// must not create extra money to spend beyond the category's limit (DECISIONS.md 47).
    /// </summary>
    public static long FloorAtZero(long spentCents) => Math.Max(0, spentCents);

    /// <summary>The owner's share of a transaction, rounded half away from zero to the cent.</summary>
    public static long OwnerShare(BudgetTransaction t)
    {
        if (t.SplitFactor is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(t), t.SplitFactor, "Split factor must be between 0 and 1.");
        return t.SplitFactor == 1m
            ? t.AmountCents
            : (long)Math.Round(t.AmountCents * t.SplitFactor, 0, MidpointRounding.AwayFromZero);
    }

    public static LeftToSpendStatus StatusFor(long leftToSpendCents, long totalVariableLimitCents)
    {
        if (leftToSpendCents < 0)
            return LeftToSpendStatus.Overspent;
        // left / total < 15%  <=>  left * 100 < 15 * total   (integer math, no rounding).
        // Exactly zero left is always Low, even when there is no variable budget at all.
        return leftToSpendCents == 0 || leftToSpendCents * 100 < LowThresholdPercent * totalVariableLimitCents
            ? LeftToSpendStatus.Low
            : LeftToSpendStatus.Healthy;
    }
}
