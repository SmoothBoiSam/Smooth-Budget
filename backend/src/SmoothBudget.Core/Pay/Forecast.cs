namespace SmoothBudget.Core.Pay;

/// <param name="HasSchedule">False until the month's schedule is imported: no amounts yet.</param>
/// <param name="LowConfidence">Fewer than 2 stubs, or taxable pay outside the range the model was fitted on.</param>
public record ForecastCheck(
    PayPeriod Period, bool HasSchedule, IReadOnlyList<GrossLine> Lines, long GrossCents, long TaxableCents,
    IReadOnlyList<StubAmount> Taxes, IReadOnlyList<StubAmount> Deductions, long NetCents, long LowNetCents, long HighNetCents,
    bool LowConfidence, IReadOnlyList<string> Reasons, IReadOnlyList<string> Notes);

/// <summary>
/// One check's forecast (SPEC 5.5): gross from the schedule plus the fixed lines and PTO cash-out learned from the stubs,
/// run through the learned withholding model, with a low/likely/high range.
/// </summary>
public static class PayForecast
{
    public const string PtoLine = "PTO cash-out";

    /// <param name="flatNetPercent">With fewer than 2 stubs: net as this percent of gross, the owner's flat estimate.</param>
    /// <param name="typicalMissCents">How far recent forecasts were from what landed (mean absolute), which widens the range.</param>
    public static ForecastCheck Check(PeriodGross gross, WithholdingModel? model, long rateCentsPerHour, int flatNetPercent, long typicalMissCents)
    {
        var p = gross.Period;
        if (!gross.HasSchedule)
            return new ForecastCheck(p, false, gross.Lines, 0, 0, [], [], 0, 0, 0, true, ["The schedule for this month isn't imported yet."], gross.Notes);

        var lines = gross.Lines.ToList();
        var notes = gross.Notes.ToList();
        GrossLine? pto = null;
        if (model is not null)
        {
            foreach (var r in model.RecurringEarnings.Where(r => r.Schedule == CheckSchedule.Every || (r.Schedule == CheckSchedule.SecondHalf) == p.SecondHalf))
                lines.Add(new GrossLine(r.Name, r.Cents, r.Schedule == CheckSchedule.Every ? "On every check (from your stubs)." : "Once a month, on this check (from your stubs)."));
            if (model.PtoHoursPerCheck > 0)
                lines.Add(pto = new GrossLine(PtoLine, Hm.Pay((long)Math.Round(model.PtoHoursPerCheck * 60), rateCentsPerHour),
                    $"About {model.PtoHoursPerCheck:0.##} user and vacation hours earned per check, cashed out at {PayEngine.Dollars(rateCentsPerHour)}/hour. They land on alternating checks, so this is the average."));
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
        var withoutPto = pto is null ? likely : Net(lines.Where(l => l != pto).ToList());
        var doublePto = pto is null ? likely : Net(lines.Append(pto).ToList());
        if (model is null) reasons.Add($"Fewer than 2 pay stubs: net is a flat {flatNetPercent}% of gross until you add stubs.");
        else if (likely.OutsideFit)
            reasons.Add($"Taxable pay of {PayEngine.Dollars(likely.TaxableCents)} is outside what your stubs cover ({PayEngine.Dollars(model.MinTaxableCents)} to {PayEngine.Dollars(model.MaxTaxableCents)}), so taxes are extrapolated (floored at $0).");

        return new ForecastCheck(p, true, lines, likely.GrossCents, likely.TaxableCents, likely.Taxes, likely.Deductions, likely.NetCents,
            Math.Max(0, withoutPto.NetCents - typicalMissCents), doublePto.NetCents + typicalMissCents, reasons.Count > 0, reasons, notes);
    }

    /// <summary>The typical miss: mean absolute difference between forecast and actual net over the most recent checks.</summary>
    public static long TypicalMiss(IEnumerable<(long Predicted, long Actual)> history, int take = 6)
    {
        var recent = history.Take(take).ToList();
        return recent.Count == 0 ? 0 : (long)Math.Round(recent.Average(h => (decimal)Math.Abs(h.Actual - h.Predicted)), MidpointRounding.AwayFromZero);
    }
}

/// <param name="Account">Where it's charged: a checking bill needs the cash left there.</param>
public record PlanBill(string Key, string Name, long Cents, DateOnly Due, string Account, string? Note = null);
public record PlanTransfer(string Key, string Label, long Cents, string Destination, string Why);

/// <param name="Kind">"keep" (bills to leave in an account), "debt", "sweep", "savings" or "spending".</param>
/// <param name="Bills">For "keep": the bills it covers.</param>
public record PlanItem(string Key, string Kind, string Label, long Cents, string Destination, string Why, IReadOnlyList<PlanBill>? Bills = null);

public record PaydayPlanResult(IReadOnlyList<PlanItem> Items, long ShortCents);

/// <summary>
/// The payday plan (SPEC 4.6): each expected check allocated across the bills due before the next payday, debt
/// payments, the rollover sweep, savings and spending money, each with where it goes.
/// </summary>
public static class PaydayPlan
{
    public static PaydayPlanResult Build(long netCents, IReadOnlyList<PlanBill> bills, IReadOnlyList<PlanTransfer> transfers, string spendingAccount)
    {
        // One "keep" line per account for every bill due from it before the next payday (DECISIONS.md 138).
        var items = bills.GroupBy(b => b.Account).Select(g =>
        {
            var list = g.OrderBy(b => b.Due).ToList();
            var when = list.Count == 1 ? $"due {list[0].Due:MMM d}" : $"due {list[0].Due:MMM d} to {list[^1].Due:MMM d}";
            return new PlanItem($"keep:{g.Key}", "keep", $"{list.Count} bill{(list.Count == 1 ? "" : "s")} {when}", list.Sum(b => b.Cents), g.Key,
                "Bills paid from this account before your next payday." + string.Concat(list.Where(b => b.Note is not null).Select(b => " " + b.Note)), list);
        }).ToList();
        items.AddRange(transfers.Where(t => t.Cents > 0).Select(t => new PlanItem(t.Key, t.Key.Split(':')[0], t.Label, t.Cents, t.Destination, t.Why)));
        var used = items.Sum(i => i.Cents);
        var spending = netCents - used;
        items.Add(new PlanItem("spending", "spending", "Spending money", Math.Max(0, spending), spendingAccount, "What's left for day-to-day spending until the next check."));
        return new PaydayPlanResult(items, Math.Max(0, -spending));
    }

    /// <summary>A monthly amount split evenly over the checks paid in that budget month, the last check taking any odd cent.</summary>
    public static long Share(long monthlyCents, int checksInMonth, int index) =>
        checksInMonth <= 1 ? monthlyCents : monthlyCents / checksInMonth + (index == checksInMonth - 1 ? monthlyCents % checksInMonth : 0);
}
