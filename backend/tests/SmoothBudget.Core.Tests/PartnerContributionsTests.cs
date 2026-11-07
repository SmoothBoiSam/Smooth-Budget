using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Tests;

/// <summary>Milestone 7: SPEC section 3's rent and grocery examples, and Appendix D's contribution matching. All data invented.</summary>
public class PartnerContributionsTests
{
    private static readonly BudgetMonthRange Nov = new BudgetMonthCalendar().MonthOf(2026, 11); // Nov 6 - Dec 6 (the 7th is a Saturday)
    private static readonly Guid Rent = Guid.NewGuid(), Groceries = Guid.NewGuid();
    private static readonly ContributionArrangement RentFromPartner = new(Guid.NewGuid(), Rent, 800_00);

    private static PartnerDeposit D(int month, int day, long cents, bool contribution = false, bool other = false) =>
        new(Guid.NewGuid(), new DateOnly(2026, month, day), cents, contribution, other);

    private static ContributionStatus Status(DateOnly today, params PartnerDeposit[] deposits) =>
        Assert.Single(PartnerContributions.Match([RentFromPartner], Nov, deposits, today));

    // ---- SPEC section 3, groceries ------------------------------------------------------------------

    [Fact]
    public void Groceries_you_buy_count_60_against_groceries_and_she_owes_60()
    {
        var mine = new SplitPurchase(Guid.NewGuid(), new DateOnly(2026, 11, 10), "FRESH MARKET", 120_00, 0.5m);
        Assert.Equal((60_00L, 60_00L), (mine.OwnerShareCents, mine.PartnerShareCents));
        Assert.Equal(60_00, PartnerMoney.Balance([mine], []).OwedToOwnerCents); // owed by the partner

        var lts = LeftToSpendCalculator.Calculate(Nov, new DateOnly(2026, 11, 12),
            [new BudgetCategory(Groceries, CategoryType.Variable, 450_00)],
            [new BudgetTransaction(Groceries, mine.Date, 120_00, SplitFactor: 0.5m)], 0);
        Assert.Equal(60_00, lts.TotalVariableSpentCents); // $60 against Groceries
    }

    [Fact]
    public void Groceries_she_buys_mean_you_owe_her_60()
    {
        var hers = new SplitPurchase(Guid.NewGuid(), new DateOnly(2026, 11, 10), "FRESH MARKET", 120_00, 0.5m, PaidByPartner: true);
        var b = PartnerMoney.Balance([hers], []);
        Assert.Equal((-60_00L, 60_00L), (b.OwedToOwnerCents, b.OwnerSharesOfHersCents)); // owed to the partner
    }

    [Fact]
    public void Both_grocery_runs_cancel_out()
    {
        var mine = new SplitPurchase(Guid.NewGuid(), new DateOnly(2026, 11, 10), "FRESH MARKET", 120_00, 0.5m);
        var hers = new SplitPurchase(Guid.NewGuid(), new DateOnly(2026, 11, 12), "CORNER GROCERY", 120_00, 0.5m, PaidByPartner: true);
        Assert.Equal(0, PartnerMoney.Balance([mine, hers], []).OwedToOwnerCents);
    }

    // ---- SPEC section 3, rent: the fixed contribution --------------------------------------------------

    [Fact]
    public void Before_the_window_her_800_is_expected_and_nothing_is_missing()
    {
        var s = Status(new DateOnly(2026, 11, 12));
        Assert.Equal((ContributionState.Expected, 0L, 0L), (s.State, s.ReceivedCents, s.ShortfallCents));
        Assert.Equal((new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 24)), (s.WindowStart, s.WindowEnd));
    }

    [Fact]
    public void The_largest_transfer_in_the_window_is_her_contribution()
    {
        var rent = D(11, 21, 800_00);
        var s = Status(new DateOnly(2026, 11, 25), D(11, 19, 42_50), rent, D(11, 26, 900_00) /* after the window */);
        Assert.Equal((ContributionState.Received, 800_00L, rent.Id, 0L), (s.State, s.ReceivedCents, s.MatchedDepositId!.Value, s.ShortfallCents));
    }

    [Fact]
    public void A_different_amount_is_flagged_and_a_short_one_is_missing_from_left_to_spend_after_the_window()
    {
        // Appendix D: $200 one January instead of $800.
        var early = Status(new DateOnly(2026, 11, 22), D(11, 20, 200_00)); // the rest may still come
        Assert.Equal((ContributionState.DifferentAmount, 0L), (early.State, early.ShortfallCents));
        var late = Status(new DateOnly(2026, 11, 25), D(11, 20, 200_00));
        Assert.Equal((ContributionState.DifferentAmount, 600_00L), (late.State, late.ShortfallCents));
        // $900 one December: flagged, but extra money never raises limits.
        var more = Status(new DateOnly(2026, 11, 25), D(11, 22, 900_00));
        Assert.Equal((ContributionState.DifferentAmount, 0L), (more.State, more.ShortfallCents));
    }

    [Fact]
    public void Nothing_by_the_end_of_the_window_is_late_and_left_to_spend_is_recomputed_without_it()
    {
        var s = Status(new DateOnly(2026, 11, 25), D(11, 10, 30_00));
        Assert.Equal((ContributionState.Late, 800_00L), (s.State, s.ShortfallCents));

        var lts = LeftToSpendCalculator.Calculate(Nov, new DateOnly(2026, 11, 25), [new BudgetCategory(Groceries, CategoryType.Variable, 450_00)], [], 0, s.ShortfallCents);
        Assert.Equal((450_00L - 800_00L, 800_00L), (lts.LeftToSpendCents, lts.PartnerShortfallCents));
        var close = MonthCloseCalculator.Close(Nov, [new BudgetCategory(Groceries, CategoryType.Variable, 450_00)], [], 0, s.ShortfallCents);
        Assert.Equal(450_00 - 800_00, close.ClosingNetCents); // and the month closes without it too
    }

    [Fact]
    public void A_transfer_you_tagged_as_her_contribution_counts_even_outside_the_window_and_one_you_tagged_otherwise_doesnt()
    {
        var tagged = D(11, 27, 800_00, contribution: true);
        var a = Status(new DateOnly(2026, 11, 28), tagged);
        Assert.Equal((ContributionState.Received, tagged.Id), (a.State, a.MatchedDepositId!.Value));
        var notIt = D(11, 21, 800_00, other: true);
        Assert.Equal(ContributionState.Late, Status(new DateOnly(2026, 11, 28), notIt).State);
    }

    [Fact]
    public void The_window_sits_inside_a_budget_month_that_starts_on_the_7th()
    {
        var dec = new BudgetMonthCalendar().MonthOf(2026, 12); // Dec 7 - Jan 6
        Assert.Equal((new DateOnly(2026, 12, 18), new DateOnly(2026, 12, 24)), PartnerContributions.Window(dec, 18, 24));
    }
}
