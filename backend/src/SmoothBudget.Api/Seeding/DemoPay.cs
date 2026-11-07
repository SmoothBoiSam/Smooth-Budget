using SmoothBudget.Core.Pay;

namespace SmoothBudget.Api.Seeding;

/// <summary>
/// FAKE pay data for the demo household (milestone 9): invented 3-day trips and invented pay stubs whose taxes follow a
/// made-up scheme, so the Payday tab has a forecast, a learned model and history to show. Nothing here is real.
/// </summary>
public static class DemoPay
{
    private record DemoDay(int Offset, TimeOnly Report, TimeOnly Release, int Duty, (string Flight, string Org, string Dest, string Dep, string Arr, int Block, int Credit)[] Legs);

    private static readonly DemoDay[] Trip =
    [
        new(0, new(12, 15), new(20, 0), 405, [("01", "DEN", "COS", "13:00", "13:50", 50, 50), ("02", "COS", "DEN", "14:30", "15:25", 55, 55), ("03", "DEN", "PIR", "17:00", "19:45", 105, 100)]),
        new(1, new(6, 15), new(14, 35), 560, [("04", "PIR", "DEN", "07:00", "07:50", 110, 105), ("05", "DEN", "RAP", "09:00", "10:05", 65, 60), ("06", "RAP", "DEN", "11:00", "12:10", 70, 75), ("07", "DEN", "COS", "13:30", "14:20", 50, 50)]),
        new(2, new(7, 15), new(14, 6), 411, [("08", "COS", "DEN", "08:00", "08:50", 50, 50), ("09", "DEN", "ROW", "10:00", "11:30", 90, 95), ("10", "ROW", "DEN", "12:15", "13:51", 96, 100)]),
    ];

    /// <summary>Four invented 3-day trips in the month, starting on the 3rd, 10th, 18th and 25th.</summary>
    public static ParsedSchedule Schedule(int year, int month, PayRules rules)
    {
        var pairings = new List<Pairing>();
        foreach (var (startDay, n) in new[] { 3, 10, 18, 25 }.Select((d, i) => (d, i + 1)))
        {
            var start = new DateOnly(year, month, startDay);
            var days = Trip.Select(d =>
            {
                var legs = d.Legs.Select((l, i) => new ScheduleLeg(i + 1, $"9{n}{l.Flight}", l.Org, l.Dest, TimeOnly.Parse(l.Dep), TimeOnly.Parse(l.Arr), l.Block, l.Credit, false)).ToList();
                var legCredit = legs.Sum(l => l.CreditMinutes);
                return new DutyDay(start.AddDays(d.Offset), d.Report, d.Release, legs.Sum(l => l.BlockMinutes), legCredit, CreditRules.DayCredit(legCredit, d.Duty, rules), d.Duty, legs);
            }).ToList();
            var first = days[0].Date.ToDateTime(days[0].Report);
            var tafb = (int)(days[^1].Date.ToDateTime(days[^1].Release) - first).TotalMinutes;
            var code = $"D{month:00}{n}";
            pairings.Add(new Pairing($"{code} {start:MM/dd}", code, first, days.Sum(d => d.BlockMinutes), CreditRules.PairingCredit(days.Sum(d => d.CreditMinutes), tafb, rules), tafb, days));
        }
        return new ParsedSchedule(year, month, "text", [], pairings, ["Demo schedule (invented)."], []);
    }

    /// <summary>An invented stub: per diem, the $5 uniform allowance and a $50 monthly commuter subsidy are untaxed; taxes follow a made-up scheme.</summary>
    public static PayStubData Stub(PayPeriod p, int i)
    {
        const long rate = 30_32;
        var hours = 26.5m + i % 3 * 4.25m;
        var legs = 10 + i % 3 * 2;
        var perDiem = 98m + i % 3 * 22.5m;
        var earnings = new List<StubEarning>
        {
            new("Regular", rate, hours, (long)Math.Round(hours * rate, MidpointRounding.AwayFromZero)),
            new("Boarding Pay", 15_16, legs * 0.5m, legs * 7_58),
            new("NonTax Per Diem", 2_25, perDiem, (long)Math.Round(perDiem * 2_25, MidpointRounding.AwayFromZero)),
            new("Uni Allowance", null, null, 5_00),
        };
        if (p.SecondHalf) earnings.Add(new("Commuter Subsid", null, null, 50_00));
        if (i % 2 == 0) earnings.Add(new("User Pay", rate, 3.3m, (long)Math.Round(3.3m * rate, MidpointRounding.AwayFromZero)));
        var gross = earnings.Sum(e => e.CurrentCents);
        var untaxed = earnings.Where(e => e.Name is "NonTax Per Diem" or "Uni Allowance" or "Commuter Subsid").Sum(e => e.CurrentCents);
        var pre = new List<StubAmount> { new("Demo Dental", 9_00), new("Demo Vision", 4_00) };
        var taxable = gross - untaxed - 13_00;
        long R(decimal v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);
        var taxes = new List<StubAmount>
        {
            new("Federal Income Tax", Math.Max(0, R(taxable * 0.11m - 70_00))), new("Social Security", R(taxable * 0.062m)), new("Medicare", R(taxable * 0.0145m)),
            new("State Income Tax", Math.Max(0, R((taxable * 0.04m - 9_00) / 100m) * 100)),
        };
        if (p.SecondHalf) taxes.Add(new("City Tax", 4_00));
        var after = new List<StubAmount> { new("Demo Life", 1_50) };
        var tax = taxes.Sum(t => t.CurrentCents);
        var ded = 13_00 + 1_50;
        return new PayStubData(p.PayDate, p.Start, p.End, rate, gross, taxable, tax, ded, gross - tax - ded, earnings, taxes, pre, after, 1.4m + i % 2 * 0.3m, 1.9m);
    }
}
