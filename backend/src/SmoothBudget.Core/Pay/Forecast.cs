namespace SmoothBudget.Core.Pay;

/// <param name="HasSchedule">False until the month's schedule is imported: no amounts yet.</param>
/// <param name="LowConfidence">Fewer than 2 stubs, or taxable pay outside the range the model was fitted on.</param>
public record ForecastCheck(
    PayPeriod Period, bool HasSchedule, IReadOnlyList<GrossLine> Lines, long GrossCents, long TaxableCents,
    IReadOnlyList<StubAmount> Taxes, IReadOnlyList<StubAmount> Deductions, long NetCents, long LowNetCents, long HighNetCents,
    bool LowConfidence, IReadOnlyList<string> Reasons, IReadOnlyList<string> Notes);

/// <summary>
/// One check's forecast (SPEC 5.5): gross from the schedule plus the fixed lines learned from the stubs, run through the
/// learned withholding model, with a low/likely/high range. A PTO cash-out is never assumed: it comes only when you choose
/// to cash out (DECISIONS.md 193).
/// </summary>
public static class PayForecast
{

    /// <param name="flatNetPercent">With fewer than 2 stubs: net as this percent of gross, the owner's flat estimate.</param>
    /// <param name="typicalMissCents">How far recent forecasts were from what landed (mean absolute), which widens the range.</param>
    public static ForecastCheck Check(PeriodGross gross, WithholdingModel? model, int flatNetPercent, long typicalMissCents)
    {
        var p = gross.Period;
        if (!gross.HasSchedule)
            return new ForecastCheck(p, false, gross.Lines, 0, 0, [], [], 0, 0, 0, true, ["The schedule for this month isn't imported yet."], gross.Notes);

        var lines = gross.Lines.ToList();
        var notes = gross.Notes.ToList();
        if (model is not null)
        {
            foreach (var r in model.RecurringEarnings.Where(r => r.Schedule == CheckSchedule.Every || (r.Schedule == CheckSchedule.SecondHalf) == p.SecondHalf))
                lines.Add(new GrossLine(r.Name, r.Cents, r.Schedule == CheckSchedule.Every ? "On every check (from your stubs)." : "Once a month, on this check (from your stubs)."));
            if (model.LeftOut.Count > 0)
                notes.Add($"Left out as irregular: {string.Join(", ", model.LeftOut)}.");
        }

        var reasons = new List<string>();
        NetResult Net(IReadOnlyList<GrossLine> ls)
        {
            var tuples = ls.Select(l => (l.Name, l.Cents)).ToList();
            if (model is null)
            {
                var g = tuples.Sum(t => t.Cents);
                return new NetResult(g, g, [], [], (long)Math.Round(g * flatNetPercent / 100m, MidpointRounding.AwayFromZero), true);
            }
            return Withholding.Apply(model, tuples, p.SecondHalf);
        }

        var likely = Net(lines);
        if (model is null) reasons.Add($"Fewer than 2 pay stubs: net is a flat {flatNetPercent}% of gross until you add stubs.");
        else if (likely.OutsideFit)
            reasons.Add($"Taxable pay of {PayEngine.Dollars(likely.TaxableCents)} is outside what your stubs cover ({PayEngine.Dollars(model.MinTaxableCents)} to {PayEngine.Dollars(model.MaxTaxableCents)}), so taxes are extrapolated (floored at $0).");

        return new ForecastCheck(p, true, lines, likely.GrossCents, likely.TaxableCents, likely.Taxes, likely.Deductions, likely.NetCents,
            Math.Max(0, likely.NetCents - typicalMissCents), likely.NetCents + typicalMissCents, reasons.Count > 0, reasons, notes);
    }

    /// <summary>The typical miss: mean absolute difference between forecast and actual net over the most recent checks.</summary>
    public static long TypicalMiss(IEnumerable<(long Predicted, long Actual)> history, int take = 6)
    {
        var recent = history.Take(take).ToList();
        return recent.Count == 0 ? 0 : (long)Math.Round(recent.Average(h => (decimal)Math.Abs(h.Actual - h.Predicted)), MidpointRounding.AwayFromZero);
    }
}

/// <param name="Account">Where it's charged: a checking bill needs the cash left there.</param>
/// <param name="Cents">The whole bill: what has to be in the account when it's charged.</param>
/// <param name="FromOthersCents">The part someone else sends into that account first (your partner's fixed contribution toward rent).</param>
/// <param name="ArrivedCents">How much of that part has already arrived, so it's sitting in the account now (DECISIONS.md 253).</param>
public record PlanBill(string Key, string Name, long Cents, DateOnly Due, string Account, string? Note = null, long FromOthersCents = 0, long ArrivedCents = 0);
/// <param name="FromEarlierCents">The part paid from money already in the account rather than from this check: what you
/// charged to a card before payday, paid with the spending money you didn't spend from checking (DECISIONS.md 179).</param>
public record PlanTransfer(string Key, string Label, long Cents, string Destination, string Why, long FromEarlierCents = 0);

/// <param name="Kind">"keep" (bills to leave in an account), "hold" (money to leave there for the next check's bills,
/// DECISIONS.md 253), "debt", "cardspend" (pay off what you charged), "sweep" or "savings".</param>
/// <param name="Bills">For "keep": the bills it covers.</param>
/// <param name="FromOthersCents">The part that doesn't come out of this check: for "keep", what arrives from someone else;
/// for "cardspend", what you charged before payday, paid from spending money already in checking.</param>
public record PlanItem(string Key, string Kind, string Label, long Cents, string Destination, string Why, IReadOnlyList<PlanBill>? Bills = null,
    long FromOthersCents = 0);

public record PaydayPlanResult(IReadOnlyList<PlanItem> Items, long ShortCents);

/// <param name="MinimumCents">This check's share of the card's minimum payment.</param>
public record CardMinimum(string Key, string Destination, long MinimumCents);
/// <param name="ExtraCents">The part beyond the minimum (only the strategy's target card gets any).</param>
public record CardShare(CardMinimum Card, long Cents, long ExtraCents);

/// <summary>
/// The payday plan (SPEC 4.6): each expected check allocated across the bills due before the next payday, debt
/// payments, the rollover sweep and savings, each with where it goes. What's left is spending money, which Home shows
/// as Left to Spend, so the plan doesn't list it (DECISIONS.md 154).
/// </summary>
public static class PaydayPlan
{
    public static PaydayPlanResult Build(long netCents, IReadOnlyList<PlanBill> bills, IReadOnlyList<PlanTransfer> transfers)
    {
        // One "keep" line per account for every bill due from it before the next payday (DECISIONS.md 138). It's the whole
        // of each bill, since that's what the account needs when it's charged, even where your partner sends part of it
        // into the account first (DECISIONS.md 165). Only this check's own part counts against the check.
        var items = bills.GroupBy(b => b.Account).Select(g =>
        {
            var list = g.OrderBy(b => b.Due).ToList();
            var when = list.Count == 1 ? $"due {list[0].Due:MMM d}" : $"due {list[0].Due:MMM d} to {list[^1].Due:MMM d}";
            return new PlanItem($"keep:{g.Key}", "keep", $"{list.Count} bill{(list.Count == 1 ? "" : "s")} {when}", list.Sum(b => b.Cents), g.Key,
                "Bills paid from this account before your next payday." + string.Concat(list.Where(b => b.Note is not null).Select(b => " " + b.Note)), list,
                list.Sum(b => Math.Min(b.FromOthersCents, b.Cents)));
        }).ToList();
        items.AddRange(transfers.Where(t => t.Cents > 0).Select(t => new PlanItem(t.Key, t.Key.Split(':')[0], t.Label, t.Cents, t.Destination, t.Why,
            FromOthersCents: Math.Min(t.Cents, Math.Max(0, t.FromEarlierCents)))));
        return new PaydayPlanResult(items, Math.Max(0, items.Sum(i => i.Cents - i.FromOthersCents) - netCents));
    }

    /// <summary>A monthly amount split evenly over the checks paid in that budget month, the last check taking any odd cent.</summary>
    public static long Share(long monthlyCents, int checksInMonth, int index) =>
        checksInMonth <= 1 ? monthlyCents : monthlyCents / checksInMonth + (index == checksInMonth - 1 ? monthlyCents % checksInMonth : 0);

    /// <summary>
    /// Splits one check's card payment (DECISIONS.md 155): every card gets its minimum first, and only what's left over
    /// (the extra) goes to the card the strategy picks. If the payment doesn't cover every minimum, the minimums are still
    /// listed in full, since missing one costs a fee and hurts your credit; the plan then shows the check as short.
    /// </summary>
    public static IReadOnlyList<CardShare> SplitCardPayment(long cents, IReadOnlyList<CardMinimum> cards, string targetKey)
    {
        var extra = Math.Max(0, cents - cards.Sum(c => c.MinimumCents));
        return cards.Select(c => new CardShare(c, c.MinimumCents + (c.Key == targetKey ? extra : 0), c.Key == targetKey ? extra : 0)).ToList();
    }
}
