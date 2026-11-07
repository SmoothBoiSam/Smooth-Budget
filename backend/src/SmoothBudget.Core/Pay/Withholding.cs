namespace SmoothBudget.Core.Pay;

public record StubEarning(string Name, long? RateCents, decimal? Hours, long CurrentCents);
public record StubAmount(string Name, long CurrentCents);

/// <summary>The numbers parsed from one pay stub: nothing personal (no name, address, SSN or bank account).</summary>
public record PayStubData(
    DateOnly PayDate, DateOnly PeriodStart, DateOnly PeriodEnd, long RateCents,
    long GrossCents, long FedTaxableCents, long TotalTaxesCents, long TotalDeductionsCents, long NetCents,
    IReadOnlyList<StubEarning> Earnings, IReadOnlyList<StubAmount> Taxes, IReadOnlyList<StubAmount> PreTax, IReadOnlyList<StubAmount> AfterTax,
    decimal? UserHoursEarned = null, decimal? VacationHoursEarned = null, long? YtdGrossCents = null, long? YtdNetCents = null)
{
    public bool SecondHalf => PeriodStart.Day >= 16;
}

/// <summary>Which checks an item appears on: every one, or only one half of the month (the Denver tax is on the check paid on the 7th).</summary>
public enum CheckSchedule { Every, SecondHalf, FirstHalf }

public enum TaxBase { FederalTaxable, WagesBeforePreTax }

public enum TaxKind { Rate, Flat }

/// <summary>One tax line: a flat amount, or rate × base + intercept (floored at $0), optionally rounded to whole dollars.</summary>
public record TaxRule(string Name, TaxKind Kind, CheckSchedule Schedule, decimal Rate, long InterceptCents, TaxBase Base, bool RoundToDollar, long FlatCents);

/// <summary>A deduction: a flat amount per check, or a percent of gross (a 401(k)).</summary>
public record DeductionRule(string Name, long Cents, CheckSchedule Schedule, decimal? PercentOfGross = null);

/// <summary>A fixed earnings line learned from the stubs (the $5 uniform allowance, the monthly commuter subsidy).</summary>
public record RecurringEarning(string Name, long Cents, CheckSchedule Schedule);

/// <summary>
/// The withholding model, learned from the stubs (SPEC 5.3): which earnings lines are untaxed, each tax's rate,
/// flat deductions, fixed earnings, and the PTO hours cashed out per check on average.
/// </summary>
/// <param name="MinTaxableCents">The fit is only valid between the lowest and highest taxable pay seen (Appendix C: don't extrapolate).</param>
public record WithholdingModel(
    IReadOnlyList<string> NonTaxable, IReadOnlyList<DeductionRule> PreTax, IReadOnlyList<DeductionRule> AfterTax, IReadOnlyList<TaxRule> Taxes,
    IReadOnlyList<RecurringEarning> RecurringEarnings, IReadOnlyList<string> LeftOut, decimal PtoHoursPerCheck,
    long MinTaxableCents, long MaxTaxableCents, int StubCount, IReadOnlyList<string> Notes);

public record NetResult(long GrossCents, long TaxableCents, IReadOnlyList<StubAmount> Taxes, IReadOnlyList<StubAmount> Deductions, long NetCents, bool OutsideFit)
{
    public long TaxesCents => Taxes.Sum(t => t.CurrentCents);
}

/// <param name="TaxErrorCents">The largest difference on any single tax line.</param>
public record BackTest(DateOnly PayDate, long ActualNetCents, long PredictedNetCents, long TaxErrorCents)
{
    public long ErrorCents => PredictedNetCents - ActualNetCents;
}

public static class Withholding
{
    /// <summary>Recent stubs count more: each older stub weighs this much of the one after it.</summary>
    public const double RecencyDecay = 0.8;

    public static NetResult Apply(WithholdingModel m, IReadOnlyList<(string Name, long Cents)> lines, bool secondHalf)
    {
        bool On(CheckSchedule s) => s == CheckSchedule.Every || (s == CheckSchedule.SecondHalf) == secondHalf;
        var gross = lines.Sum(l => l.Cents);
        var nonTax = lines.Where(l => m.NonTaxable.Contains(l.Name, StringComparer.OrdinalIgnoreCase)).Sum(l => l.Cents);
        long Deduction(DeductionRule d) => d.PercentOfGross is { } p ? (long)Math.Round(gross * p, MidpointRounding.AwayFromZero) : d.Cents;
        var pre = m.PreTax.Where(d => On(d.Schedule)).Select(d => new StubAmount(d.Name, Deduction(d))).ToList();
        var taxable = gross - nonTax - pre.Sum(d => d.CurrentCents);
        var wages = taxable + pre.Sum(d => d.CurrentCents);
        var taxes = m.Taxes.Where(t => On(t.Schedule))
            .Select(t => new StubAmount(t.Name, t.Kind == TaxKind.Flat ? t.FlatCents : Tax(t, t.Base == TaxBase.FederalTaxable ? taxable : wages)))
            .ToList();
        var after = m.AfterTax.Where(d => On(d.Schedule)).Select(d => new StubAmount(d.Name, Deduction(d))).ToList();
        var deductions = pre.Concat(after).ToList();
        var net = gross - taxes.Sum(t => t.CurrentCents) - deductions.Sum(d => d.CurrentCents);
        return new NetResult(gross, taxable, taxes, deductions, net, taxable < m.MinTaxableCents || taxable > m.MaxTaxableCents);
    }

    public static long Tax(TaxRule t, long baseCents)
    {
        var v = t.Rate * baseCents + t.InterceptCents;
        var cents = t.RoundToDollar ? (long)Math.Round(v / 100m, MidpointRounding.AwayFromZero) * 100 : (long)Math.Round(v, MidpointRounding.AwayFromZero);
        return Math.Max(0, cents);
    }

    /// <summary>Runs the model over each stub's own earnings and compares with what was actually paid.</summary>
    public static List<BackTest> Test(WithholdingModel m, IReadOnlyList<PayStubData> stubs) => stubs.OrderBy(s => s.PayDate).Select(s =>
    {
        var r = Apply(m, s.Earnings.Select(e => (e.Name, e.CurrentCents)).ToList(), s.SecondHalf);
        var taxErr = s.Taxes.Select(t => Math.Abs((r.Taxes.FirstOrDefault(x => x.Name == t.Name)?.CurrentCents ?? 0) - t.CurrentCents)).DefaultIfEmpty(0).Max();
        return new BackTest(s.PayDate, s.NetCents, r.NetCents, taxErr);
    }).ToList();

    /// <summary>
    /// Learns the model from at least 2 stubs (SPEC 5.3). Nothing is assumed: the untaxed lines are the ones that explain
    /// each stub's federal taxable gross, and each tax is fitted against the base that explains it best.
    /// </summary>
    public static WithholdingModel? Learn(IReadOnlyList<PayStubData> stubs, StubLineNames names)
    {
        if (stubs.Count < 2) return null;
        var ordered = stubs.OrderByDescending(s => s.PayDate).ToList();
        var weights = ordered.Select((_, i) => Math.Pow(RecencyDecay, i)).ToList();
        var notes = new List<string>();

        // 1. Untaxed lines: the smallest set of earnings lines that explains gross − pre-tax − federal taxable on every stub.
        var earningNames = ordered.SelectMany(s => s.Earnings.Where(e => e.CurrentCents != 0).Select(e => e.Name)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        long Cur(PayStubData s, string name) => s.Earnings.Where(e => e.Name == name).Sum(e => e.CurrentCents);
        var targets = ordered.Select(s => s.GrossCents - s.PreTax.Sum(p => p.CurrentCents) - s.FedTaxableCents).ToList();
        List<string>? nonTaxable = null;
        if (earningNames.Count <= 20)
        {
            foreach (var mask in Enumerable.Range(0, 1 << earningNames.Count).OrderBy(m => System.Numerics.BitOperations.PopCount((uint)m)))
            {
                var set = earningNames.Where((_, i) => (mask & (1 << i)) != 0).ToList();
                if (ordered.Select((s, i) => Math.Abs(set.Sum(n => Cur(s, n)) - targets[i])).All(e => e <= 1)) { nonTaxable = set; break; }
            }
        }
        if (nonTaxable is null)
        {
            nonTaxable = earningNames.Where(n => n.Contains("nontax", StringComparison.OrdinalIgnoreCase) || n.Contains("non-tax", StringComparison.OrdinalIgnoreCase)).ToList();
            notes.Add("No set of earnings lines explains the federal taxable gross on every stub, so only lines named \"NonTax\" are treated as untaxed.");
        }

        // 2. Deductions: flat per check (the newest amount), or a percent of gross for a 401(k).
        List<DeductionRule> Deductions(Func<PayStubData, IReadOnlyList<StubAmount>> pick) =>
            ordered.SelectMany(s => pick(s).Select(a => a.Name)).Distinct().Select(name =>
            {
                var schedule = Pattern(ordered, s => pick(s).Any(a => a.Name == name && a.CurrentCents != 0));
                var newest = ordered.Select(s => pick(s).FirstOrDefault(a => a.Name == name && a.CurrentCents != 0)).FirstOrDefault(a => a is not null);
                if (name.Contains("401", StringComparison.Ordinal))
                {
                    var with = ordered.Where(s => s.GrossCents > 0 && pick(s).Any(a => a.Name == name)).ToList();
                    var pct = with.Count == 0 ? 0m : Math.Round(with.Average(s => pick(s).First(a => a.Name == name).CurrentCents / (decimal)s.GrossCents), 4);
                    return new DeductionRule(name, 0, schedule, pct);
                }
                return new DeductionRule(name, newest?.CurrentCents ?? 0, schedule);
            }).Where(d => d.Cents != 0 || d.PercentOfGross is not null).ToList();
        var preTax = Deductions(s => s.PreTax);
        var afterTax = Deductions(s => s.AfterTax);

        // 3. Taxes: flat when the amount never changes, otherwise rate × base + intercept against the base that fits best.
        var taxes = new List<TaxRule>();
        foreach (var name in ordered.SelectMany(s => s.Taxes.Select(t => t.Name)).Distinct())
        {
            var present = ordered.Select((s, i) => (s, w: weights[i], cents: s.Taxes.Where(t => t.Name == name).Sum(t => t.CurrentCents))).Where(x => x.cents != 0).ToList();
            if (present.Count == 0) continue;
            var schedule = Pattern(ordered, s => s.Taxes.Any(t => t.Name == name && t.CurrentCents != 0));
            if (present.Count == 1 || present.All(p => p.cents == present[0].cents))
            {
                taxes.Add(new TaxRule(name, TaxKind.Flat, schedule, 0, 0, TaxBase.FederalTaxable, false, present[0].cents));
                continue;
            }
            Fit? best = null;
            foreach (var b in new[] { TaxBase.FederalTaxable, TaxBase.WagesBeforePreTax })
            {
                var xs = present.Select(p => b == TaxBase.FederalTaxable ? p.s.FedTaxableCents : p.s.FedTaxableCents + p.s.PreTax.Sum(a => a.CurrentCents)).ToList();
                var fit = FitLine(xs, present.Select(p => p.cents).ToList(), present.Select(p => p.w).ToList(), b);
                if (best is null || fit.Score.CompareTo(best.Score) < 0) best = fit;
            }
            taxes.Add(new TaxRule(name, TaxKind.Rate, schedule, best!.Rate, best.Intercept, best.Base, best.RoundToDollar, 0));
            if (best.MaxError > 1) notes.Add($"{name} doesn't fit a straight line exactly (off by up to {best.MaxError / 100m:$0.00} on a stub).");
        }

        // 4. Fixed earnings lines: the same amount every time it appears, on every check or every other one.
        var recurring = new List<RecurringEarning>();
        var leftOut = new List<string>();
        foreach (var name in earningNames.Where(n => !names.IsScheduleDriven(n)))
        {
            var amounts = ordered.Select(s => Cur(s, name)).Where(c => c != 0).Distinct().ToList();
            var schedule = ExactPattern(ordered, s => Cur(s, name) != 0);
            if (amounts.Count == 1 && schedule is { } sch && ordered.Count(s => Cur(s, name) != 0) >= 2)
                recurring.Add(new RecurringEarning(name, amounts[0], sch));
            else
                leftOut.Add(name);
        }

        // 5. PTO cash-out: the hours earned per check on average (they're cashed out on alternating checks).
        var pto = ordered.Select((s, i) => (h: (s.UserHoursEarned ?? 0) + (s.VacationHoursEarned ?? 0), w: weights[i], any: s.UserHoursEarned is not null || s.VacationHoursEarned is not null))
            .Where(x => x.any).ToList();
        var ptoHours = pto.Count == 0 ? 0m : Math.Round((decimal)pto.Sum(x => (double)x.h * x.w) / (decimal)pto.Sum(x => x.w), 2);

        return new WithholdingModel(nonTaxable, preTax, afterTax, taxes, recurring, leftOut, ptoHours,
            ordered.Min(s => s.FedTaxableCents), ordered.Max(s => s.FedTaxableCents), ordered.Count, notes);
    }

    private record Fit(TaxBase Base, decimal Rate, long Intercept, bool RoundToDollar, long MaxError, int Decimals)
    {
        // Within a cent counts as exact; then prefer fewer decimals, no intercept, and the federal taxable base.
        public (long, int, int, int) Score => (Math.Max(MaxError, 1), Decimals, Intercept == 0 ? 0 : 1, (int)Base);
    }

    /// <summary>Weighted least squares, then the simplest rate and intercept (in cents) that predict every stub as well.</summary>
    private static Fit FitLine(List<long> xs, List<long> ys, List<double> w, TaxBase b)
    {
        var roundToDollar = ys.Count >= 2 && ys.All(y => y % 100 == 0);
        double sw = w.Sum(), xm = xs.Select((x, i) => x * w[i]).Sum() / sw, ym = ys.Select((y, i) => y * w[i]).Sum() / sw;
        double sxx = xs.Select((x, i) => w[i] * (x - xm) * (x - xm)).Sum(), sxy = xs.Select((x, i) => w[i] * (x - xm) * (ys[i] - ym)).Sum();
        var r0 = sxx > 0 ? sxy / sxx : xm != 0 ? ym / xm : 0;

        Fit? best = null;
        foreach (var decimals in new[] { 2, 3, 4, 5, 6, 10 })
        {
            var rate = Math.Round((decimal)r0, decimals);
            var residuals = xs.Select((x, i) => (e: ys[i] - rate * x, w: w[i])).OrderBy(x => x.e).ToList();
            var half = residuals.Sum(x => x.w) / 2;
            decimal median = 0;
            double acc = 0;
            foreach (var (e, wt) in residuals) { acc += wt; if (acc >= half) { median = e; break; } }
            var center = (long)Math.Round(median, MidpointRounding.AwayFromZero);
            var span = roundToDollar ? 100 : 2;
            for (var c = center - span; c <= center + span; c++)
            {
                var rule = new TaxRule("", TaxKind.Rate, CheckSchedule.Every, rate, c, b, roundToDollar, 0);
                var err = xs.Select((x, i) => Math.Abs(Tax(rule, x) - ys[i])).Max();
                var fit = new Fit(b, rate, c, roundToDollar, err, decimals);
                if (best is null || fit.Score.CompareTo(best.Score) < 0 || (fit.Score.CompareTo(best.Score) == 0 && Math.Abs(c - center) < Math.Abs(best.Intercept - center)))
                    best = fit;
            }
            if (Math.Abs(center) <= 2)
            {
                var zero = new TaxRule("", TaxKind.Rate, CheckSchedule.Every, rate, 0, b, roundToDollar, 0);
                var fit = new Fit(b, rate, 0, roundToDollar, xs.Select((x, i) => Math.Abs(Tax(zero, x) - ys[i])).Max(), decimals);
                if (fit.Score.CompareTo(best!.Score) <= 0) best = fit;
            }
        }
        return best!;
    }

    /// <summary>Every check, or only one half of the month when that's the only place it shows up.</summary>
    private static CheckSchedule Pattern(IReadOnlyList<PayStubData> stubs, Func<PayStubData, bool> present) => ExactPattern(stubs, present) ?? CheckSchedule.Every;

    private static CheckSchedule? ExactPattern(IReadOnlyList<PayStubData> stubs, Func<PayStubData, bool> present)
    {
        if (stubs.All(present)) return CheckSchedule.Every;
        bool hasFirst = stubs.Any(s => !s.SecondHalf), hasSecond = stubs.Any(s => s.SecondHalf);
        if (hasFirst && hasSecond && stubs.All(s => present(s) == s.SecondHalf)) return CheckSchedule.SecondHalf;
        if (hasFirst && hasSecond && stubs.All(s => present(s) == !s.SecondHalf)) return CheckSchedule.FirstHalf;
        return null;
    }
}
