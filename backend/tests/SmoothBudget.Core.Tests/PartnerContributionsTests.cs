using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Tests;

/// <summary>Milestone 7: SPEC section 3's rent example (the fixed contribution) and Appendix D's matching. All data invented.</summary>
public class PartnerContributionsTests
{
    private static readonly BudgetMonthRange Nov = new BudgetMonthCalendar().MonthOf(2026, 11); // Nov 6 - Dec 6 (the 7th is a Saturday)
    private static readonly Guid Rent = Guid.NewGuid(), Groceries = Guid.NewGuid();
    private static readonly ContributionArrangement RentFromPartner = new(Guid.NewGuid(), Rent, 800_00);

    private static PartnerDeposit D(int month, int day, long cents, bool contribution = false, bool other = false) =>
        new(Guid.NewGuid(), new DateOnly(2026, month, day), cents, contribution, other);

    private static ContributionStatus Status(DateOnly today, params PartnerDeposit[] deposits) =>
        Assert.Single(PartnerContributions.Match([RentFromPartner], Nov, deposits, today));

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

    // ---- Sent early (DECISIONS.md 166) ------------------------------------------------------------------

    [Fact]
    public void Rent_sent_early_counts_in_the_month_you_moved_it_to_and_not_where_it_landed()
    {
        var early = D(11, 5, 800_00) with { CountsForMonthStart = Nov.Start }; // she's paid before you, so it came Nov 5
        var s = Status(new DateOnly(2026, 11, 25), early);
        Assert.Equal((ContributionState.Received, early.Id, 0L), (s.State, s.MatchedDepositId!.Value, s.ShortfallCents));

        var oct = new BudgetMonthCalendar().MonthOf(2026, 10);
        Assert.True(oct.Contains(early.Date));
        var octRent = D(10, 20, 800_00);
        var inOct = Assert.Single(PartnerContributions.Match([RentFromPartner], oct, [octRent, early], new DateOnly(2026, 11, 5)));
        Assert.Equal(octRent.Id, inOct.MatchedDepositId); // October keeps its own rent
        var onlyEarly = Assert.Single(PartnerContributions.Match([RentFromPartner], oct, [early], new DateOnly(2026, 11, 5)));
        Assert.Null(onlyEarly.MatchedDepositId); // and never claims the moved one
    }

    [Fact]
    public void Early_candidates_are_her_recent_transfers_before_the_month_that_were_not_their_own_months_rent()
    {
        var octRent = D(10, 30, 800_00);
        var early = D(11, 5, 800_00);
        var labeledOther = D(11, 4, 50_00, other: true);
        var tooOld = D(10, 20, 800_00);
        var moved = D(11, 3, 800_00) with { CountsForMonthStart = Nov.Start };
        var inMonth = D(11, 8, 800_00);
        var list = PartnerContributions.EarlyCandidates(Nov, [octRent, early, labeledOther, tooOld, moved, inMonth], new HashSet<Guid> { octRent.Id });
        Assert.Equal([early.Id], list.Select(d => d.Id));
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
