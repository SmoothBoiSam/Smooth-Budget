namespace SmoothBudget.Core.Pay;

/// <param name="Start">The first day the check covers.</param>
/// <param name="PayDate">When it's paid, after the weekend and holiday shifts.</param>
public record PayPeriod(DateOnly Start, DateOnly End, DateOnly PayDate)
{
    /// <summary>The 16th to the end of the month, paid on the 7th: the check that carries once-a-month items.</summary>
    public bool SecondHalf => Start.Day >= 16;
}

/// <summary>Semimonthly pay dates (SPEC 5.4, Appendix A): the 1st to the 15th is paid on the 22nd; the 16th to the end on the 7th of the next month.</summary>
public static class PayCalendar
{
    public static PayPeriod PeriodContaining(DateOnly day, PayRules rules)
    {
        if (day.Day <= 15)
        {
            var start = new DateOnly(day.Year, day.Month, 1);
            return new PayPeriod(start, start.AddDays(14), PayDate(new DateOnly(day.Year, day.Month, rules.FirstHalfPayDay), rules));
        }
        var s = new DateOnly(day.Year, day.Month, 16);
        var end = new DateOnly(day.Year, day.Month, DateTime.DaysInMonth(day.Year, day.Month));
        var next = end.AddDays(1);
        return new PayPeriod(s, end, PayDate(new DateOnly(next.Year, next.Month, rules.SecondHalfPayDay), rules));
    }

    /// <summary>The checks paid on or after <paramref name="from"/>, in order.</summary>
    public static IEnumerable<PayPeriod> From(DateOnly from, PayRules rules)
    {
        var p = PeriodContaining(from.AddMonths(-1), rules);
        while (true)
        {
            if (p.PayDate >= from) yield return p;
            p = PeriodContaining(p.End.AddDays(1), rules);
        }
    }

    /// <summary>A Saturday moves earlier, a Sunday later, and a holiday to the next day.</summary>
    public static DateOnly PayDate(DateOnly nominal, PayRules rules)
    {
        var d = nominal;
        if (d.DayOfWeek == DayOfWeek.Saturday) d = d.AddDays(-1);
        else if (d.DayOfWeek == DayOfWeek.Sunday) d = d.AddDays(1);
        for (var i = 0; i < 7 && (rules.Holidays.Contains(d) || d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday); i++)
            d = d.AddDays(1);
        return d;
    }
}

/// <param name="RateCentsPerHour">From years of service in the pay rules table.</param>
/// <param name="HolidayPay">Eligible for holiday pay (full-time, 90 days of employment).</param>
public record PayProfileInput(long RateCentsPerHour, bool FullTime = true, bool HolidayPay = true);

/// <summary>A month's imported schedule and your choices about it.</summary>
/// <param name="Reserve">Awarded reserve this month: the 76-hour guarantee replaces trip credit for base pay.</param>
/// <param name="OvertimeKeys">Pairings you flew as overtime (1.5×).</param>
/// <param name="PaidEntryKeys">Non-flying entries you switched on as paid (all are unpaid by default).</param>
/// <param name="DroppedKeys">Trips you dropped after importing: kept on the list, left out of pay.</param>
public record ScheduleMonthInput(ParsedSchedule Schedule, bool Reserve, IReadOnlySet<string> OvertimeKeys, IReadOnlySet<string> PaidEntryKeys, IReadOnlySet<string>? DroppedKeys = null)
{
    /// <summary>The trips you're flying: everything imported except the ones you dropped.</summary>
    public IEnumerable<Pairing> Flown => Schedule.Pairings.Where(p => DroppedKeys is null || !DroppedKeys.Contains(p.Key));
}

/// <param name="Name">The pay stub's name for the line, so withholding treats it the way your stubs do.</param>
/// <param name="Note">How it was figured, in plain words.</param>
public record GrossLine(string Name, long Cents, string Note);

/// <param name="HasSchedule">False when the month's schedule hasn't been imported: the check can't be forecast yet.</param>
/// <param name="Notes">Information the owner should see (below the line guarantee, unpaid entries).</param>
public record PeriodGross(PayPeriod Period, bool HasSchedule, IReadOnlyList<GrossLine> Lines, IReadOnlyList<string> Notes)
{
    public long Cents => Lines.Sum(l => l.Cents);
}

/// <summary>What one trip is expected to pay (SPEC 5.2: "each trip's expected pay").</summary>
public record TripPay(string Key, long CreditCents, long OvertimeCents, long BoardingCents, long PerDiemCents)
{
    public long TotalCents => CreditCents + OvertimeCents + BoardingCents + PerDiemCents;
}

/// <summary>
/// Gross pay by pay period from the schedule (SPEC 5.4). Everything is allocated by day: credit, boarding and per diem
/// belong to the pay period of the day they're earned, so a trip that straddles the 15th splits across two checks.
/// </summary>
public static class PayEngine
{
    public static PeriodGross Gross(PayPeriod period, ScheduleMonthInput? month, PayProfileInput profile, PayRules rules)
    {
        var lines = new List<GrossLine>();
        var notes = new List<string>();
        var rate = profile.RateCentsPerHour;
        var names = rules.Lines;

        if (profile.FullTime && profile.HolidayPay)
            foreach (var h in rules.Holidays.Where(h => h >= period.Start && h <= period.End).Order())
                lines.Add(new GrossLine(names.Holiday, Hm.Pay(rules.HolidayCreditMinutes, rate),
                    $"{Hm.Format(rules.HolidayCreditMinutes)} for the {h:MMM d} holiday at {Dollars(rate)}/hour."));

        if (month is null)
            return new PeriodGross(period, false, lines, notes);

        var s = month.Schedule;
        var inPeriod = (DateOnly d) => d >= period.Start && d <= period.End;

        // Credit: day credits on their days; a pairing's guarantee top-up (TAFB ÷ 4 above the day credits) on its last day.
        long regular = 0, overtime = 0;
        foreach (var p in month.Flown)
        {
            var mins = p.Days.Where(d => inPeriod(d.Date)).Sum(d => d.CreditMinutes)
                       + (inPeriod(p.Days[^1].Date) ? p.CreditMinutes - p.DayCreditSum : 0);
            if (mins == 0) continue;
            if (month.OvertimeKeys.Contains(p.Key)) overtime += mins;
            regular += mins;
        }
        foreach (var e in s.Entries.Where(e => inPeriod(DateOnly.FromDateTime(e.Start))))
        {
            if (month.PaidEntryKeys.Contains(e.Key)) regular += e.CreditMinutes;
            else notes.Add($"{e.Code} on {e.Start:MMM d} ({Hm.Format(e.CreditMinutes)} credit) is treated as unpaid. Switch it on if it's paid.");
        }

        var monthCredit = MonthCreditMinutes(month);
        if (month.Reserve)
        {
            var half = rules.ReserveGuaranteeHours * 60 / 2;
            lines.Add(new GrossLine(names.Regular, Hm.Pay(half, rate), $"Reserve: half of the {rules.ReserveGuaranteeHours}-hour guarantee ({Hm.Format(half)}) at {Dollars(rate)}/hour."));
        }
        else if (regular > 0)
        {
            lines.Add(new GrossLine(names.Regular, Hm.Pay(regular, rate), $"{Hm.Format((int)regular)} of credit at {Dollars(rate)}/hour."));
            if (overtime > 0)
                lines.Add(new GrossLine(names.Overtime, Hm.Pay(overtime, rate, rules.OvertimeMultiplier - 1m),
                    $"Overtime: the extra {(rules.OvertimeMultiplier - 1m) * 100:0}% on {Hm.Format((int)overtime)} you marked as overtime."));

            // The override goes on the check for the period in which monthly credit crosses the threshold.
            var threshold = rules.OverrideAboveHours * 60;
            if (monthCredit > threshold)
            {
                var firstHalf = MonthCreditMinutes(month, through: new DateOnly(s.Year, s.Month, 15));
                var crossesHere = period.SecondHalf ? firstHalf <= threshold : firstHalf > threshold;
                if (crossesHere && period.Start.Month == s.Month)
                    lines.Add(new GrossLine(names.Override, Hm.Pay(monthCredit - threshold, rules.OverrideCentsPerHour),
                        $"Estimate: {Hm.Format(monthCredit - threshold)} above {rules.OverrideAboveHours} hours this month at {Dollars(rules.OverrideCentsPerHour)}/hour."));
            }
        }

        var legs = month.Flown.SelectMany(p => p.Days).Where(d => inPeriod(d.Date)).Sum(d => d.RevenueLegs);
        if (legs > 0)
        {
            var perLeg = BoardingPerLeg(rate, rules);
            lines.Add(new GrossLine(names.Boarding, legs * perLeg,
                $"{legs} boarded revenue legs × {rules.BoardingHoursPerLeg:0.##} hour at {Dollars(Half(rate, rules))}/hour."));
        }

        var awayStart = period.Start.ToDateTime(TimeOnly.MinValue);
        var awayEnd = period.End.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var away = month.Flown.Sum(p => Overlap(p.Start, p.End, awayStart, awayEnd));
        if (away > 0)
            lines.Add(new GrossLine(names.PerDiem, Hm.Pay(away, rules.PerDiemCentsPerHour), $"{Hm.Format(away)} away from base at {Dollars(rules.PerDiemCentsPerHour)}/hour."));

        if (!month.Reserve && period.Start.Month == s.Month)
        {
            var guarantee = (profile.FullTime ? rules.LineGuaranteeHours : rules.PartTimeGuaranteeHours) * 60;
            if (monthCredit < guarantee)
                notes.Add($"This month's credit is {Hm.Format(monthCredit)}, {Hm.Format(guarantee - monthCredit)} below the {guarantee / 60}-hour line guarantee. No top-up is assumed, so the checks are lighter (information only).");
        }
        return new PeriodGross(period, true, lines, notes);
    }

    /// <summary>Each trip's expected pay, before taxes.</summary>
    public static TripPay Trip(Pairing p, bool overtime, PayProfileInput profile, PayRules rules) => new(
        p.Key, Hm.Pay(p.CreditMinutes, profile.RateCentsPerHour), overtime ? Hm.Pay(p.CreditMinutes, profile.RateCentsPerHour, rules.OvertimeMultiplier - 1m) : 0,
        p.RevenueLegs * BoardingPerLeg(profile.RateCentsPerHour, rules), Hm.Pay(p.TafbMinutes, rules.PerDiemCentsPerHour));

    public static int MonthCreditMinutes(ScheduleMonthInput month, DateOnly? through = null)
    {
        var total = 0;
        foreach (var p in month.Flown)
        {
            total += p.Days.Where(d => through is null || d.Date <= through).Sum(d => d.CreditMinutes);
            if (through is null || p.Days[^1].Date <= through) total += p.CreditMinutes - p.DayCreditSum;
        }
        total += month.Schedule.Entries.Where(e => month.PaidEntryKeys.Contains(e.Key) && (through is null || DateOnly.FromDateTime(e.Start) <= through)).Sum(e => e.CreditMinutes);
        return total;
    }

    /// <summary>Half the hourly rate (rounded to the cent, as the stubs print it) × 0.5 hour.</summary>
    public static long BoardingPerLeg(long rate, PayRules rules) =>
        (long)Math.Round(Half(rate, rules) * rules.BoardingHoursPerLeg, MidpointRounding.AwayFromZero);

    private static long Half(long rate, PayRules rules) => (long)Math.Round(rate * rules.BoardingRateFactor, MidpointRounding.AwayFromZero);

    private static int Overlap(DateTime a0, DateTime a1, DateTime b0, DateTime b1)
    {
        var s = a0 > b0 ? a0 : b0;
        var e = a1 < b1 ? a1 : b1;
        return e > s ? (int)Math.Round((e - s).TotalMinutes) : 0;
    }

    public static string Dollars(long cents) => (cents / 100m).ToString("$#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
}
