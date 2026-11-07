using SmoothBudget.Core.Pay;

namespace SmoothBudget.Core.Tests;

/// <summary>
/// An invented February 2027 schedule in the SkedPlus+ layouts (every flight, time and name is made up). It exercises
/// the same rules as the owner's sample: a duty-guarantee day, minimum-day days, a leg-credit day, a pairing paid by the
/// time-away-from-base guarantee, an international report (YQR), Central-time airports, a credit-only CDT day, an SKV
/// entry, and a trip that straddles the 15th.
/// </summary>
public static class PayFixtures
{
    private static readonly int[] Cols = [4, 12, 20, 25, 30, 37, 44, 48, 55, 62, 67, 71];
    public const string LegHeader = "    Flight  Tail    Org  Dest Dep    Arr    Pax Block  Credit D/PU Dhd Turn ";

    private static string Row(int seq, string flight, string org, string dest, string dep, string arr, string block, string credit, string pax = "0", string tail = "", string dhd = "", string turn = "")
    {
        var cells = new[] { flight, tail, org, dest, dep, arr, pax, block, credit, "", dhd, turn };
        var line = $"{seq,2}. ".PadRight(Cols[0]);
        for (var i = 0; i < Cols.Length; i++)
            line = line.PadRight(Cols[i]) + cells[i];
        return line;
    }

    private const string Rule = "________________________________________________________________________________";
    private const string Double = "================================================================================";

    public static string Text => string.Join("\n",
    [
        "000000 Test Attendant   DEN CR7 FA   February 2027",
        "",
        "SKV",
        "Start: 02/01/2027 08:00",
        "End: 02/02/2027 18:00",
        "Credit: 8:24",
        Rule, Double, "",
        "T1001 02/04/2027",
        "Block: 14:51   Credit: 17:58   TAFB: 71:51",
        Rule,
        "Thursday 02-04-2027    Report: 12:15    Release: 21:45",
        LegHeader,
        Row(1, "1101", "DEN", "COS", "13:00", "13:50", "0:50", "0:50", pax: "40", tail: "N100AA", turn: "0:40"),
        Row(2, "1102", "COS", "DEN", "14:30", "15:25", "0:55", "0:55", pax: "41", tail: "N100AA", turn: "2:35"),
        Row(3, "1103", "DEN", "YQR", "18:00", "21:30", "2:30", "2:20", pax: "30", tail: "N100AA"),
        "                                     Day Total: 4:15   4:15   Duty: 8:30   ",
        "Hotel: Test Hotel One   Layover: 10:15",
        Rule,
        "Friday 02-05-2027    Report: 08:00    Release: 14:00",
        LegHeader,
        Row(4, "1104", "YQR", "DEN", "09:00", "09:40", "1:40", "1:35", turn: "1:20"),
        Row(5, "1105", "DEN", "PIR", "11:00", "13:45", "1:45", "1:40"),
        "                                     Day Total: 3:25   4:12   Duty: 6:00   ",
        Rule,
        "Saturday 02-06-2027    Report: 06:15    Release: 10:20",
        LegHeader,
        Row(6, "1106", "PIR", "DEN", "07:00", "07:50", "1:50", "1:45", turn: "1:10"),
        Row(7, "1107", "DEN", "RAP", "09:00", "10:05", "1:05", "1:00"),
        "                                     Day Total: 2:55   4:12   Duty: 5:05   ",
        Rule,
        "Sunday 02-07-2027    Report: 04:15    Release: 12:06",
        LegHeader,
        Row(8, "1108", "RAP", "DEN", "05:00", "06:10", "1:10", "1:15", turn: "1:50"),
        Row(9, "1109", "DEN", "ROW", "08:00", "09:30", "1:30", "1:35", turn: "0:45"),
        Row(10, "1110", "ROW", "DEN", "10:15", "11:51", "1:36", "1:40"),
        "                                     Day Total: 4:16   4:30   Duty: 7:51   ",
        Rule, "",
        "Crew Members",
        " 1. CA: 000001 Pretend Captain   FO: 000002 Pretend Officer   FA: 000000 Test Attendant",
        "", Rule, Double, "",
        "T1002 02/14/2027",
        "Block: 5:35   Credit: 12:36   TAFB: 47:20",
        Rule,
        "Sunday 02-14-2027    Report: 13:15    Release: 17:05",
        LegHeader,
        Row(1, "2201", "DEN", "JLN", "14:00", "16:50", "1:50", "1:55"),
        "                                     Day Total: 1:50   4:12   Duty: 2:50   ",
        Rule,
        "Monday 02-15-2027    Report: 13:00    Release: 14:00",
        LegHeader,
        Row(2, "CDT", "JLN", "JLN", "13:00", "14:00", "0:00", "4:12", dhd: "N"),
        "                                     Day Total: 0:00   4:12   Duty: 1:00   ",
        Rule,
        "Tuesday 02-16-2027    Report: 07:15    Release: 12:35",
        LegHeader,
        Row(3, "2203", "JLN", "DEN", "08:00", "09:05", "2:05", "2:00", turn: "0:55"),
        Row(4, "2204", "DEN", "COS", "10:00", "10:50", "0:50", "0:50", turn: "0:40"),
        Row(5, "2205", "COS", "DEN", "11:30", "12:20", "0:50", "0:50"),
        "                                     Day Total: 3:45   4:12   Duty: 6:20   ",
        Rule, "",
        "Crew Members",
        " 1. CA: 000003 Another Pretend   FO: 000004 Also Pretend   FA: 000000 Test Attendant",
        "", Rule, Double,
    ]);

    /// <summary>The same month as the older CSV export: decimal hours, no report or release, and no CDT row.</summary>
    public static string Csv => string.Join("\n",
    [
        "Flight,Date,A/C Type,Tail,Origin,Dest,Depart,Arrive,Block,Credit,Captain,First Officer,Flight Attendant",
        "1101,02/04/2027,CRJ,N100AA,DEN,COS,13:00,13:50,0.83,0.83,000001 Pretend Captain,000002 Pretend Officer,000000 Test Attendant",
        "1102,02/04/2027,CRJ,N100AA,COS,DEN,14:30,15:25,0.92,0.92,000001 Pretend Captain,000002 Pretend Officer,000000 Test Attendant",
        "1103,02/04/2027,CRJ,N100AA,DEN,YQR,18:00,21:30,2.50,2.33,000001 Pretend Captain,000002 Pretend Officer,000000 Test Attendant",
        "1104,02/05/2027,CRJ,,YQR,DEN,09:00,09:40,1.67,1.58,,,",
        "1105,02/05/2027,CRJ,,DEN,PIR,11:00,13:45,1.75,1.67,,,",
        "1106,02/06/2027,CRJ,,PIR,DEN,07:00,07:50,1.83,1.75,,,",
        "1107,02/06/2027,CRJ,,DEN,RAP,09:00,10:05,1.08,1.00,,,",
        "1108,02/07/2027,CRJ,,RAP,DEN,05:00,06:10,1.17,1.25,,,",
        "1109,02/07/2027,CRJ,,DEN,ROW,08:00,09:30,1.50,1.58,,,",
        "1110,02/07/2027,CRJ,,ROW,DEN,10:15,11:51,1.60,1.67,,,",
        "2201,02/14/2027,CRJ,,DEN,JLN,14:00,16:50,1.83,1.92,,,",
        "2203,02/16/2027,CRJ,,JLN,DEN,08:00,09:05,2.08,2.00,,,",
        "2204,02/16/2027,CRJ,,DEN,COS,10:00,10:50,0.83,0.83,,,",
        "2205,02/16/2027,CRJ,,COS,DEN,11:30,12:20,0.83,0.83,,,",
    ]);

    /// <summary>An invented withholding scheme the learner has to discover from stubs alone.</summary>
    public static class Scheme
    {
        public const long Rate = 25_00;
        public static long Fed(long taxable) => Math.Max(0, (long)Math.Round(taxable * 0.11m - 75_00, MidpointRounding.AwayFromZero));
        public static long Ss(long taxable) => (long)Math.Round(taxable * 0.062m, MidpointRounding.AwayFromZero);
        public static long Medicare(long taxable) => (long)Math.Round(taxable * 0.0145m, MidpointRounding.AwayFromZero);
        public static long Leave(long wages) => (long)Math.Round(wages * 0.005m, MidpointRounding.AwayFromZero);
        public static long State(long taxable) => (long)Math.Round((taxable * 0.04m - 8_00) / 100m, MidpointRounding.AwayFromZero) * 100;
        public const long City = 4_00, Dental = 7_00, Vision = 3_00, Life = 1_20, Uniform = 4_00, Commuter = 40_00;
    }

    /// <summary>A stub built from the scheme: the untaxed lines are per diem, the uniform allowance and the commuter subsidy.</summary>
    public static PayStubData Stub(DateOnly payDate, decimal regularHours, int legs, decimal perDiemHours, decimal userCashOut = 0, long bonus = 0, decimal userEarned = 1.4m, decimal vacationEarned = 1.9m)
    {
        var second = payDate.Day < 15;
        var start = second ? new DateOnly(payDate.AddMonths(-1).Year, payDate.AddMonths(-1).Month, 16) : new DateOnly(payDate.Year, payDate.Month, 1);
        var end = second ? new DateOnly(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month)) : start.AddDays(14);
        var earnings = new List<StubEarning>
        {
            new("Regular", Scheme.Rate, regularHours, (long)Math.Round(regularHours * Scheme.Rate, MidpointRounding.AwayFromZero)),
            new("Boarding Pay", Scheme.Rate / 2, legs * 0.5m, legs * 6_25),
            new("NonTax Per Diem", 2_25, perDiemHours, (long)Math.Round(perDiemHours * 2_25, MidpointRounding.AwayFromZero)),
            new("Uni Allowance", null, null, Scheme.Uniform),
        };
        if (second) earnings.Add(new("Commuter Subsid", null, null, Scheme.Commuter));
        if (userCashOut > 0) earnings.Add(new("User Pay", Scheme.Rate, userCashOut, (long)Math.Round(userCashOut * Scheme.Rate, MidpointRounding.AwayFromZero)));
        if (bonus > 0) earnings.Add(new("Bonus Line", null, null, bonus));
        var gross = earnings.Sum(e => e.CurrentCents);
        var untaxed = earnings.Where(e => e.Name is "NonTax Per Diem" or "Uni Allowance" or "Commuter Subsid").Sum(e => e.CurrentCents);
        var pre = new List<StubAmount> { new("Dental", Scheme.Dental), new("Vision", Scheme.Vision) };
        var taxable = gross - untaxed - pre.Sum(p => p.CurrentCents);
        var taxes = new List<StubAmount>
        {
            new("Federal Income Tax", Scheme.Fed(taxable)), new("Social Security", Scheme.Ss(taxable)), new("Medicare", Scheme.Medicare(taxable)),
            new("State Leave", Scheme.Leave(taxable + pre.Sum(p => p.CurrentCents))), new("State Income Tax", Scheme.State(taxable)),
        };
        if (second) taxes.Add(new("City Tax", Scheme.City));
        var after = new List<StubAmount> { new("Life", Scheme.Life) };
        var ded = pre.Sum(p => p.CurrentCents) + after.Sum(a => a.CurrentCents);
        var tax = taxes.Sum(t => t.CurrentCents);
        return new PayStubData(payDate, start, end, Scheme.Rate, gross, taxable, tax, ded, gross - tax - ded, earnings, taxes, pre, after, userEarned, vacationEarned);
    }

    public static IReadOnlyList<PayStubData> Stubs =>
    [
        Stub(new(2026, 9, 4), 41.5m, 18, 160m, userCashOut: 3.1m),
        Stub(new(2026, 9, 22), 52.25m, 22, 190.5m),
        Stub(new(2026, 10, 7), 47m, 20, 170m, userCashOut: 3.4m, bonus: 12_50),
        Stub(new(2026, 10, 22), 58.75m, 24, 205.25m),
        Stub(new(2026, 11, 6), 44.25m, 19, 150m, userCashOut: 3.2m),
    ];
}
