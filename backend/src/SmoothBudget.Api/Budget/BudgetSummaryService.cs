using System.Globalization;
using SmoothBudget.Api.Home;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Api.Budget;

public record BudgetCategoryLineDto(
    Guid Id,
    string Name,
    string Type,
    CategoryPriority Priority,
    long LimitCents,
    // True when this month's limit differs from the usual one ("this month only").
    bool HasMonthOverride,
    // Spent for spending categories; paid for bills, debt and savings.
    long SpentCents,
    // Limit minus spent. Negative = over.
    long RemainingCents,
    // "onTrack", "low" or "over".
    string Health,
    int TransactionCount,
    // What the partner's fixed contribution covers of this line (Rent: the $800). Your net share is the limit minus this.
    long PartnerOffsetCents = 0,
    // The card payment line holds the full card minimums (DECISIONS.md 254): this much of its limit is the Card interest
    // line's, which the minimums include.
    long InterestIncludedCents = 0,
    // Card interest, listed with the debt payments: its interest is paid by the card payment above it, so the group's
    // totals leave it out (DECISIONS.md 254). Spent is the interest charged so far.
    bool PartOfCardPayments = false);

/// <param name="State">"expected", "received", "differentAmount" or "late".</param>
/// <param name="ShortfallCents">What Left to Spend is missing because of it (late, or short after the window).</param>
/// <param name="FriendId">Who sends it, and <paramref name="FriendName"/> their name (DECISIONS.md 211).</param>
public record ContributionStatusDto(
    Guid ArrangementId, Guid CategoryId, string CategoryName, string State, long ExpectedCents, long ReceivedCents,
    DateOnly WindowStart, DateOnly WindowEnd, long ShortfallCents, Guid? FriendId = null, string? FriendName = null)
{
    public static List<ContributionStatusDto> From(IReadOnlyList<ContributionStatus>? statuses, IReadOnlyList<MonthCategory> categories,
        IReadOnlyDictionary<Guid, (Guid FriendId, string Name)>? senders = null) =>
        (statuses ?? []).Select(s => new ContributionStatusDto(s.ArrangementId, s.CategoryId,
            categories.FirstOrDefault(c => c.Id == s.CategoryId)?.Name ?? "", char.ToLowerInvariant(s.State.ToString()[0]) + s.State.ToString()[1..],
            s.ExpectedCents, s.ReceivedCents, s.WindowStart, s.WindowEnd, s.ShortfallCents,
            senders?.GetValueOrDefault(s.ArrangementId).FriendId, senders?.GetValueOrDefault(s.ArrangementId).Name)).ToList();
}

public record BudgetGroupDto(
    string Type,
    string Label,
    // True only for spending categories: the ones that make up Left to Spend.
    bool CountsTowardLeftToSpend,
    long LimitCents,
    long SpentCents,
    IReadOnlyList<BudgetCategoryLineDto> Categories);

public record SweepItemDto(Guid Id, string Kind, long AmountCents, string Destination, bool Done);

/// <param name="Closed">False while the month is still in its grace period: the result may still change as late transactions arrive.</param>
/// <param name="ClosesOn">When it closes automatically, if not already closed.</param>
/// <param name="Sweeps">Once closed: the sweep items. While provisional: what they would be.</param>
public record LastMonthDto(
    BudgetMonthDto Month, bool Closed, DateOnly ClosesOn,
    long TotalUnspentCents, long TotalOverspentCents, long CarriedInDeficitCents,
    long ClosingNetCents, long SurplusCents, long DeficitCents,
    IReadOnlyList<SweepItemDto> Sweeps,
    long PartnerShortfallCents = 0, long UnfiledSpendingCents = 0, long OverplannedCents = 0);

public record BudgetSummaryDto(
    BudgetMonthDto BudgetMonth,
    DateOnly Today,
    long LeftToSpendCents,
    string Status,
    long TotalVariableLimitCents,
    long TotalVariableSpentCents,
    long CarriedInDeficitCents,
    int DaysRemaining,
    long SafeToSpendPerDayCents,
    int UncategorizedCount,
    // SPEC section 3: money in (your income + partner contributions) = all category limits + an explicit remainder.
    long IncomeExpectedCents,
    long PlannedCents,
    long UnallocatedCents,
    // Your own income vs the partner's contributions, each expected and received so far this month.
    long IncomeReceivedCents,
    long PartnerExpectedCents,
    long PartnerReceivedCents,
    string? PartnerName,
    // Automatic category guesses this month waiting for you to confirm.
    int ReviewCount,
    IReadOnlyList<BudgetGroupDto> Groups,
    LastMonthDto? LastMonth,
    IReadOnlyList<PendingSweepDto> PendingSweeps,
    // The partner's fixed contributions this month (milestone 7), and what Left to Spend is missing from late or short ones.
    IReadOnlyList<ContributionStatusDto>? Contributions = null,
    long PartnerShortfallCents = 0,
    // The automatic budget (DECISIONS.md 135): on or off, its last change, any shortfall, and the limits you set yourself.
    AutoBudgetDto? Auto = null,
    // What else comes off Left to Spend (DECISIONS.md 177-178): spending not filed yet, and a plan beyond the month's money.
    long UnfiledSpendingCents = 0,
    long OverplannedCents = 0,
    // How much the cash check lowered Left to Spend (DECISIONS.md 183).
    long CashShortCents = 0);

/// <param name="ShortCents">What this month still needs beyond its money after every squeeze.</param>
/// <param name="SetByYou">Categories whose limit you set yourself (from now on, or this month only).</param>
/// <param name="IncomeSetByYou">You typed this month's income, so it isn't the Payday forecast.</param>
public record AutoBudgetDto(bool On, string? Note, DateTime? NoteUtc, long ShortCents, IReadOnlyList<Guid> SetByYou, bool IncomeSetByYou = false,
    long HeldBackCents = 0); // left out because the accounts can't cover it (DECISIONS.md 233)

/// <summary>The Budget screen (SPEC section 4.3): every category with its limit, spent and remaining, grouped by type.</summary>
public class BudgetSummaryService(CurrentMonthLoader loader, BudgetMonthService months, Data.AppDbContext db)
{
    private static readonly (CategoryType Type, string Label)[] GroupOrder =
    [
        (CategoryType.Variable, "Spending Categories"),
        (CategoryType.FixedBill, "Bill Categories"),
        (CategoryType.DebtPayment, "Debt payments"),
        (CategoryType.SavingsTransfer, "Savings"),
    ];

    public async Task<BudgetSummaryDto> GetAsync(Guid householdId, CancellationToken ct = default)
    {
        var data = await loader.LoadAsync(householdId, ct);
        var lts = data.LeftToSpend();
        var lines = CategoryBreakdownCalculator.Calculate(data.Month, data.CalculatorCategories, data.Transactions)
            .ToDictionary(l => l.CategoryId);

        var groups = GroupOrder
            .Select(g =>
            {
                // Card interest is a bill line planned from your card balances (DECISIONS.md 159), but it's the cost of the
                // debt, so it's listed with the debt payments (DECISIONS.md 196). Only where it's listed changes.
                static CategoryType ListedAs(MonthCategory c) =>
                    c.Type == CategoryType.FixedBill && string.Equals(c.Name.Trim(), Core.Transactions.CardInterest.CategoryName, StringComparison.OrdinalIgnoreCase)
                        ? CategoryType.DebtPayment : c.Type;
                var listed = data.Categories.Where(c => ListedAs(c) == g.Type).ToList();
                // The card minimums are one amount to pay (DECISIONS.md 254). The automatic budget plans them as the card
                // payment line plus Card interest (172), so the payment line shows both: what the minimums come to, and
                // what's been paid toward them, counting payments that pay the interest. Card interest stays listed as
                // what's been charged, part of that payment.
                var interest = g.Type == CategoryType.DebtPayment ? listed.FirstOrDefault(c => c.Type == CategoryType.FixedBill) : null;
                var card = interest is null ? null : listed.FirstOrDefault(c => c.Type == CategoryType.DebtPayment);
                var debtLines = listed.Where(c => c.Type == CategoryType.DebtPayment).ToList();
                var paidExtra = card is null ? 0
                    : CategoryBreakdownCalculator.DebtPaid(data.Month, data.CalculatorCategories, data.Transactions) - debtLines.Sum(c => lines[c.Id].SpentCents);
                var items = listed.Select(c =>
                {
                    var l = lines[c.Id];
                    var (limit, spent, included) = c == card
                        ? (l.LimitCents + lines[interest!.Id].LimitCents, l.SpentCents + Math.Max(0, paidExtra), lines[interest.Id].LimitCents)
                        : (l.LimitCents, l.SpentCents, 0L);
                    var status = l with { LimitCents = limit, SpentCents = spent };
                    return new BudgetCategoryLineDto(c.Id, c.Name, c.Type.ToString(), c.Priority, limit, c.HasMonthOverride,
                        spent, status.RemainingCents, HealthName(status.Health), l.TransactionCount,
                        (data.Contributions ?? []).Where(s => s.CategoryId == c.Id).Sum(s => s.ExpectedCents),
                        included, card is not null && c == interest);
                }).ToList();
                var counted = items.Where(i => !i.PartOfCardPayments).ToList();
                return new BudgetGroupDto(g.Type.ToString(), g.Label, g.Type == CategoryType.Variable,
                    counted.Sum(i => i.LimitCents), counted.Sum(i => i.SpentCents), items);
            })
            .Where(g => g.Categories.Count > 0)
            .ToList();

        var planned = data.Categories.Sum(c => c.LimitCents);
        var last = await LastMonthAsync(householdId, data.Row, ct);

        long Received(Core.Transactions.TransactionKind kind) =>
            data.Transactions.Where(t => t.Kind == kind && !t.IsTransfer && t.AmountCents < 0).Sum(t => -t.AmountCents);
        var row = data.Row;
        // With fixed contributions set up, partner money is those contributions, counted once as offsets (DECISIONS.md 97).
        var moneyIn = MonthData.MoneyIn(row, data.Contributions);
        var partnerExpected = moneyIn - row.IncomeExpectedCents;
        // "In so far" for a friend's fixed contributions is what was matched to them (DECISIONS.md 252): one sent a day
        // early, before the month began, counts in the month you moved it to (166), like everywhere else.
        var partnerReceived = data.Contributions is { Count: > 0 } fixedOnes
            ? fixedOnes.Sum(x => x.ReceivedCents)
            : Received(Core.Transactions.TransactionKind.PartnerContribution);
        var partnerName = await Shared.SharedService.TrackedNamesAsync(db, householdId, ct); // everyone whose transfers are tracked (DECISIONS.md 211)
        var reviewCount = db.Transactions.Count(t => t.HouseholdId == householdId && t.NeedsReview && t.CategoryId != null && !t.IsTransfer
                                                     && t.Date >= data.Month.Start && t.Date <= data.Month.End);

        return new BudgetSummaryDto(
            data.MonthDto, data.Today, lts.LeftToSpendCents, CurrentMonthData.StatusName(lts.Status),
            lts.TotalVariableLimitCents, lts.TotalVariableSpentCents, lts.CarriedInDeficitCents,
            lts.DaysRemaining, lts.SafeToSpendPerDayCents, lts.UncategorizedSpendingCount,
            row.IncomeExpectedCents, planned, moneyIn - planned,
            Received(Core.Transactions.TransactionKind.Income), partnerExpected, partnerReceived,
            partnerName, reviewCount,
            groups, last, data.PendingSweeps, ContributionStatusDto.From(data.Contributions, data.Categories, await Shared.SharedService.SendersAsync(db, householdId, ct)), data.PartnerShortfallCents,
            new AutoBudgetDto(db.Households.Where(h => h.Id == householdId).Select(h => h.AutoBudget).Single(), row.AutoNote, row.AutoNoteUtc, row.AutoShortCents,
                db.Categories.Where(c => c.HouseholdId == householdId && c.LimitSetByUser).Select(c => c.Id).AsEnumerable()
                    .Concat(data.Categories.Where(c => c.HasMonthOverride).Select(c => c.Id)).Distinct().ToList(), row.IncomeSetByUser, row.AutoHeldBackCents),
            lts.UnfiledSpendingCents, lts.OverplannedCents, lts.CashShortCents);
    }

    private async Task<LastMonthDto?> LastMonthAsync(Guid householdId, Data.BudgetMonth current, CancellationToken ct)
    {
        var ended = await months.LastEndedAsync(householdId, current, ct);
        if (ended is null)
            return null;
        var r = ended.Result;

        IReadOnlyList<SweepItemDto> sweeps;
        if (ended.Closed)
        {
            sweeps = db.SweepTasks.Where(s => s.BudgetMonthId == ended.Row.Id).OrderBy(s => s.Kind).AsEnumerable()
                .Select(s => new SweepItemDto(s.Id, s.Kind.ToString(), s.AmountCents, s.Destination, s.DoneUtc is not null)).ToList();
        }
        else
        {
            var household = db.Households.Single(h => h.Id == householdId);
            var (debt, savings) = await months.SweepDestinationsAsync(householdId, ct);
            sweeps = SweepPlanner.Plan(r.SurplusCents, new SweepPreference(household.SweepTarget, household.SweepSavingsPercent), debt, savings)
                .Select(i => new SweepItemDto(Guid.Empty, i.Kind.ToString(), i.AmountCents, i.Destination, false)).ToList();
        }

        var label = ended.Range.Label.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        return new LastMonthDto(new BudgetMonthDto(ended.Range.Start, ended.Range.End, label), ended.Closed, ended.ClosesOn,
            r.TotalUnspentCents, r.TotalOverspentCents, r.CarriedInDeficitCents, r.ClosingNetCents, r.SurplusCents, r.DeficitCents, sweeps,
            r.PartnerShortfallCents, r.UnfiledSpendingCents, r.OverplannedCents);
    }

    public static string HealthName(CategoryHealth h) => h switch
    {
        CategoryHealth.Over => "over",
        CategoryHealth.Low => "low",
        _ => "onTrack",
    };
}
