using System.Text.Json;
using SmoothBudget.Core.Pay;

namespace SmoothBudget.Core.Tests;

/// <summary>Milestone 9: the pay engine (SPEC section 5) on invented schedules and stubs.</summary>
public class PayEngineTests
{
    private static readonly PayRules Rules = PayRules.Default;
    private static readonly PayProfileInput Profile = new(Rules.RateFor(0)); // $30.32, the under-3-years step

    private static ScheduleMonthInput Month(ParsedSchedule s, bool reserve = false, string[]? overtime = null, string[]? paid = null) =>
        new(s, reserve, (overtime ?? []).ToHashSet(), (paid ?? []).ToHashSet());

    [Fact]
    public void Rates_come_from_the_table_by_years_of_service()
    {
        Assert.Equal((30_32L, 30_32L, 31_26L, 41_14L, 50_21L), (Rules.RateFor(0), Rules.RateFor(2), Rules.RateFor(3), Rules.RateFor(10), Rules.RateFor(40)));
    }

    [Fact]
    public void Years_of_service_follow_the_date_of_hire_and_the_rate_steps_up_on_the_anniversary()
    {
        var hire = new DateOnly(2023, 11, 20);
        Assert.Equal((2, 3, 3), (PayRules.YearsOfService(hire, new(2026, 11, 19)), PayRules.YearsOfService(hire, new(2026, 11, 20)), PayRules.YearsOfService(hire, new(2027, 11, 19))));
        Assert.Equal((new DateOnly(2026, 11, 20), 31_26L), Rules.NextRaise(hire, new(2026, 11, 7))!.Value);
        Assert.Equal((new DateOnly(2027, 11, 20), 33_11L), Rules.NextRaise(hire, new(2026, 11, 20))!.Value);
        Assert.Null(Rules.NextRaise(new DateOnly(1990, 1, 1), new(2026, 11, 7))); // past the top step
        Assert.Equal(0, PayRules.YearsOfService(new DateOnly(2027, 1, 1), new(2026, 11, 7)));
    }

    [Fact]
    public void A_dropped_trip_stays_on_the_list_but_is_left_out_of_pay()
    {
        var s = ScheduleTextParser.Parse(PayFixtures.Text, Rules);
        var feb1 = PayCalendar.PeriodContaining(new(2027, 2, 1), Rules);
        var month = new ScheduleMonthInput(s, false, new HashSet<string>(), new HashSet<string>(), new HashSet<string> { "T1001 02/04" });
        var g = PayEngine.Gross(feb1, month, Profile, Rules);
        Assert.Equal(Hm.Pay(504, 30_32), g.Lines.Single(l => l.Name == "Regular").Cents); // only T1002's first two days
        Assert.Equal(756, PayEngine.MonthCreditMinutes(month));
        Assert.Equal(2, month.Schedule.Pairings.Count);
    }

    [Fact]
    public void Credit_rules_day_and_pairing()
    {
        Assert.Equal(278, CreditRules.DayCredit(249, 555, Rules)); // 9:15 duty ÷ 2 = 4:37.5, rounded up to 4:38, beats legs 4:09
        Assert.Equal(252, CreditRules.DayCredit(228, 418, Rules)); // the 4:12 minimum
        Assert.Equal(294, CreditRules.DayCredit(294, 502, Rules)); // legs win
        Assert.Equal(1079, CreditRules.PairingCredit(1076, 4317, Rules)); // 71:57 ÷ 4 = 17:59 beats 17:56
        Assert.Equal(1085, CreditRules.PairingCredit(1085, 4287, Rules)); // day credits win
    }

    [Fact]
    public void The_text_export_is_read_with_its_printed_values_and_every_credit_checks_out()
    {
        var s = ScheduleTextParser.Parse(PayFixtures.Text, Rules);
        Assert.Equal((2027, 2, "text"), (s.Year, s.Month, s.Source));
        Assert.Empty(s.Warnings);
        Assert.Equal(9, s.Checks.Count); // 7 duty days and 2 pairings

        var t1 = s.Pairings[0];
        Assert.Equal(("T1001", 1078, 4311, 10), (t1.Code, t1.CreditMinutes, t1.TafbMinutes, t1.RevenueLegs)); // 17:58 from TAFB ÷ 4, above the day credits' 17:09
        Assert.Equal([255, 252, 252, 270], t1.Days.Select(d => d.CreditMinutes));
        Assert.Equal(new DateTime(2027, 2, 4, 12, 15, 0), t1.Start);
        var leg = t1.Days[0].Legs[2];
        Assert.Equal(("1103", "DEN", "YQR", new TimeOnly(18, 0), new TimeOnly(21, 30), 150, 140), (leg.Flight, leg.Origin, leg.Destination, leg.Departure, leg.Arrival, leg.BlockMinutes, leg.CreditMinutes));

        // The credit-only CDT day pays the 4:12 minimum but is not a revenue leg (no boarding pay, not a deadhead).
        var t2 = s.Pairings[1];
        var cdt = t2.Days[1].Legs.Single();
        Assert.Equal(("CDT", false, false, 252), (cdt.Flight, cdt.IsRevenue, cdt.Deadhead, t2.Days[1].CreditMinutes));
        Assert.Equal((756, 4), (t2.CreditMinutes, t2.RevenueLegs));

        var skv = Assert.Single(s.Entries);
        Assert.Equal(("SKV", 504, new DateTime(2027, 2, 1, 8, 0, 0)), (skv.Code, skv.CreditMinutes, skv.Start));
    }

    [Fact]
    public void Nothing_from_the_header_or_crew_list_is_kept()
    {
        var json = JsonSerializer.Serialize(ScheduleTextParser.Parse(PayFixtures.Text, Rules));
        foreach (var personal in new[] { "Pretend", "Test Attendant", "000001", "000002", "000000", "Test Hotel" })
            Assert.DoesNotContain(personal, json);
        json = JsonSerializer.Serialize(ScheduleCsvParser.Parse(PayFixtures.Csv, Rules, ScheduleCsvParser.FindZone));
        foreach (var personal in new[] { "Pretend", "Test Attendant", "000001" })
            Assert.DoesNotContain(personal, json);
    }

    [Fact]
    public void The_csv_estimate_converts_to_utc_and_reproduces_the_printed_trip()
    {
        var printed = ScheduleTextParser.Parse(PayFixtures.Text, Rules).Pairings[0];
        var csv = ScheduleCsvParser.Parse(PayFixtures.Csv, Rules, ScheduleCsvParser.FindZone);
        Assert.Equal(("csv", 2), (csv.Source, csv.Pairings.Count)); // grouped into two pairings at base
        var est = csv.Pairings[0];
        // Report 45 minutes before (60 out of YQR), release 15 after, duty across time zones, day and pairing credit.
        Assert.Equal(printed.Days.Select(d => (d.Date, d.Report, d.Release, d.DutyMinutes, d.CreditMinutes)),
            est.Days.Select(d => (d.Date, d.Report, d.Release, d.DutyMinutes, d.CreditMinutes)));
        Assert.Equal((printed.TafbMinutes, printed.CreditMinutes, printed.RevenueLegs), (est.TafbMinutes, est.CreditMinutes, est.RevenueLegs));

        // The CSV leaves out the credit-only day, so the second trip is understated (11:50 from TAFB instead of the printed 12:36).
        Assert.Equal((2, 710), (csv.Pairings[1].Days.Count, csv.Pairings[1].CreditMinutes));
        Assert.Contains(csv.Warnings, w => w.Contains("estimates"));
    }

    [Fact]
    public void Without_the_time_zone_conversion_the_duty_would_be_wrong()
    {
        // Skipping UTC conversion would make day 2 (YQR to PIR) look like 6:00 of local clock time either way, but day 1
        // (DEN to YQR) would read 9:30 instead of 8:30. Unknown airports are flagged rather than guessed silently.
        var csv = ScheduleCsvParser.Parse(PayFixtures.Csv.Replace("YQR", "XYZ"), Rules, ScheduleCsvParser.FindZone);
        Assert.Contains(csv.Warnings, w => w.StartsWith("XYZ isn't in the airport time-zone table"));
        Assert.Equal(9 * 60 + 30, csv.Pairings[0].Days[0].DutyMinutes);
    }

    [Fact]
    public void Pay_dates_shift_for_weekends_and_holidays()
    {
        Assert.Equal(new DateOnly(2026, 11, 6), PayCalendar.PayDate(new(2026, 11, 7), Rules)); // Saturday moves earlier
        Assert.Equal(new DateOnly(2026, 11, 23), PayCalendar.PayDate(new(2026, 11, 22), Rules)); // Sunday moves later
        Assert.Equal(new DateOnly(2027, 2, 23), PayCalendar.PayDate(new(2027, 2, 22), Rules with { Holidays = [new(2027, 2, 22)] }));

        var first = PayCalendar.PeriodContaining(new(2026, 11, 14), Rules);
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 23), false), (first.Start, first.End, first.PayDate, first.SecondHalf));
        var second = PayCalendar.PeriodContaining(new(2026, 11, 16), Rules);
        Assert.Equal((new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 7), true), (second.End, second.PayDate, second.SecondHalf));
        Assert.Equal([new DateOnly(2026, 11, 23), new DateOnly(2026, 12, 7), new DateOnly(2026, 12, 22)],
            PayCalendar.From(new(2026, 11, 7), Rules).Take(3).Select(p => p.PayDate));
    }

    [Fact]
    public void Gross_is_allocated_by_day_and_a_trip_straddling_the_15th_splits()
    {
        var s = ScheduleTextParser.Parse(PayFixtures.Text, Rules);
        var feb1 = PayCalendar.PeriodContaining(new(2027, 2, 1), Rules);
        var g1 = PayEngine.Gross(feb1, Month(s), Profile, Rules);
        Assert.Equal(new DateOnly(2027, 2, 22), feb1.PayDate);
        // Credit: T1001's 17:58 plus T1002's first two days (4:12 + 4:12) = 26:22 at $30.32.
        Assert.Equal(("Regular", 799_44L), (g1.Lines[0].Name, g1.Lines[0].Cents));
        Assert.Equal(("Boarding Pay", 83_38L), (g1.Lines[1].Name, g1.Lines[1].Cents)); // 11 revenue legs × 0.5 h × $15.16
        Assert.Equal(("NonTax Per Diem", 239_85L), (g1.Lines[2].Name, g1.Lines[2].Cents)); // 71:51 + 34:45 to midnight on the 15th, × $2.25
        Assert.Contains(g1.Notes, n => n.StartsWith("SKV on Feb 1 (8:24 credit) is treated as unpaid"));
        Assert.Contains(g1.Notes, n => n.Contains("30:34, 49:26 below the 80-hour line guarantee"));

        var g2 = PayEngine.Gross(PayCalendar.PeriodContaining(new(2027, 2, 16), Rules), Month(s), Profile, Rules);
        Assert.Equal(new DateOnly(2027, 3, 8), g2.Period.PayDate); // the 7th is a Sunday
        Assert.Equal([127_34L, 22_74L, 28_31L], g2.Lines.Select(l => l.Cents)); // 4:12; 3 legs; 12:35 of per diem on the 16th
    }

    [Fact]
    public void Overtime_paid_entries_reserve_holidays_and_the_override()
    {
        var s = ScheduleTextParser.Parse(PayFixtures.Text, Rules);
        var feb1 = PayCalendar.PeriodContaining(new(2027, 2, 1), Rules);

        var ot = PayEngine.Gross(feb1, Month(s, overtime: ["T1001 02/04"]), Profile, Rules);
        Assert.Equal(272_37L, ot.Lines.Single(l => l.Name == "Overtime").Cents); // the extra 50% on 17:58

        var paid = PayEngine.Gross(feb1, Month(s, paid: ["SKV 02/01"]), Profile, Rules);
        Assert.Equal(Hm.Pay(1582 + 504, 30_32), paid.Lines[0].Cents);

        var reserve = PayEngine.Gross(feb1, Month(s, reserve: true), Profile, Rules);
        Assert.Equal(1_152_16L, reserve.Lines[0].Cents); // half of 76 hours, whatever the trips; boarding and per diem still count
        Assert.Equal(3, reserve.Lines.Count);

        var holiday = PayEngine.Gross(feb1, Month(s), Profile, Rules with { Holidays = [new(2027, 2, 15)] });
        Assert.Equal(("Holiday Pay", 130_38L), (holiday.Lines[0].Name, holiday.Lines[0].Cents)); // 4:18 at $30.32

        // Above the threshold: the hours over it go on the check for the period in which the month crosses it.
        var low = Rules with { OverrideAboveHours = 20 };
        var over = PayEngine.Gross(feb1, Month(s), Profile, low);
        Assert.Equal(52_83L, over.Lines.Single(l => l.Name == "Over 87 Hours").Cents); // 30:34 − 20:00 = 10:34 at $5
        Assert.DoesNotContain(PayEngine.Gross(PayCalendar.PeriodContaining(new(2027, 2, 16), Rules), Month(s), Profile, low).Lines, l => l.Name == "Over 87 Hours");

        var none = PayEngine.Gross(PayCalendar.PeriodContaining(new(2027, 3, 1), Rules), null, Profile, Rules);
        Assert.False(none.HasSchedule);
    }

    [Fact]
    public void Each_trip_has_its_expected_pay()
    {
        var t = PayEngine.Trip(ScheduleTextParser.Parse(PayFixtures.Text, Rules).Pairings[0], false, Profile, Rules);
        Assert.Equal((Hm.Pay(1078, 30_32), 75_80L, 161_66L), (t.CreditCents, t.BoardingCents, t.PerDiemCents)); // 10 legs; 71:51 × $2.25
    }

    [Fact]
    public void The_withholding_model_is_learned_not_assumed()
    {
        var m = Withholding.Learn(PayFixtures.Stubs, Rules.Lines)!;
        Assert.Equal(["Commuter Subsid", "NonTax Per Diem", "Uni Allowance"], m.NonTaxable);
        TaxRule T(string n) => m.Taxes.Single(t => t.Name == n);
        Assert.Equal((TaxKind.Rate, 0.11m, -75_00L, TaxBase.FederalTaxable), (T("Federal Income Tax").Kind, T("Federal Income Tax").Rate, T("Federal Income Tax").InterceptCents, T("Federal Income Tax").Base));
        Assert.Equal((0.062m, 0L), (T("Social Security").Rate, T("Social Security").InterceptCents));
        Assert.Equal((0.0145m, 0L), (T("Medicare").Rate, T("Medicare").InterceptCents));
        Assert.Equal((0.005m, 0L, TaxBase.WagesBeforePreTax), (T("State Leave").Rate, T("State Leave").InterceptCents, T("State Leave").Base));
        Assert.True(T("State Income Tax").RoundToDollar);
        Assert.Equal((TaxKind.Flat, 4_00L, CheckSchedule.SecondHalf), (T("City Tax").Kind, T("City Tax").FlatCents, T("City Tax").Schedule));
        Assert.Equal([("Dental", 7_00L), ("Vision", 3_00L)], m.PreTax.Select(d => (d.Name, d.Cents)));
        Assert.Equal(("Life", 1_20L), (m.AfterTax.Single().Name, m.AfterTax.Single().Cents));
        Assert.Equal([("Commuter Subsid", 40_00L, CheckSchedule.SecondHalf), ("Uni Allowance", 4_00L, CheckSchedule.Every)],
            m.RecurringEarnings.Select(r => (r.Name, r.Cents, r.Schedule)));
        Assert.Equal(["Bonus Line"], m.LeftOut); // irregular: left out of the forecast
        Assert.Equal(3.3m, m.PtoHoursPerCheck);

        foreach (var b in Withholding.Test(m, PayFixtures.Stubs))
        {
            Assert.InRange(b.ErrorCents, -1, 1);
            Assert.InRange(b.TaxErrorCents, 0, 1);
        }
        Assert.Null(Withholding.Learn(PayFixtures.Stubs.Take(1).ToList(), Rules.Lines)); // 2 is the minimum
    }

    [Fact]
    public void Taxes_are_floored_and_flagged_outside_the_fitted_range()
    {
        var m = Withholding.Learn(PayFixtures.Stubs, Rules.Lines)!;
        var r = Withholding.Apply(m, [("Regular", 300_00), ("NonTax Per Diem", 100_00)], secondHalf: false);
        Assert.True(r.OutsideFit);
        Assert.Equal(0, r.Taxes.Single(t => t.Name == "Federal Income Tax").CurrentCents); // 11% × $290 − $75 is negative
        Assert.Equal(290_00, r.TaxableCents);
        Assert.DoesNotContain(r.Taxes, t => t.Name == "City Tax"); // only on the check paid on the 7th
    }

    [Fact]
    public void A_check_forecast_adds_fixed_lines_and_pto_and_gives_a_range()
    {
        var s = ScheduleTextParser.Parse(PayFixtures.Text, Rules);
        var m = Withholding.Learn(PayFixtures.Stubs, Rules.Lines)!;
        var g = PayEngine.Gross(PayCalendar.PeriodContaining(new(2027, 2, 16), Rules), Month(s), Profile, Rules);
        var f = PayForecast.Check(g, m, Profile.RateCentsPerHour, 80, typicalMissCents: 10_00);
        Assert.Contains(f.Lines, l => l.Name == "Commuter Subsid" && l.Cents == 40_00); // second-half check
        Assert.Contains(f.Lines, l => l.Name == "Uni Allowance");
        Assert.Contains(f.Lines, l => l.Name == PayForecast.PtoLine && l.Cents == Hm.Pay(198, 30_32)); // 3.3 hours
        Assert.True(f.LowNetCents < f.NetCents && f.NetCents < f.HighNetCents);
        Assert.Equal(f.GrossCents - f.Taxes.Sum(t => t.CurrentCents) - f.Deductions.Sum(d => d.CurrentCents), f.NetCents);

        var first = PayForecast.Check(PayEngine.Gross(PayCalendar.PeriodContaining(new(2027, 2, 1), Rules), Month(s), Profile, Rules), m, Profile.RateCentsPerHour, 80, 0);
        Assert.DoesNotContain(first.Lines, l => l.Name == "Commuter Subsid");

        var flat = PayForecast.Check(g, null, Profile.RateCentsPerHour, 80, 0);
        Assert.True(flat.LowConfidence);
        Assert.Equal((long)Math.Round(g.Cents * 0.8m), flat.NetCents);

        var noSchedule = PayForecast.Check(PayEngine.Gross(PayCalendar.PeriodContaining(new(2027, 3, 1), Rules), null, Profile, Rules), m, Profile.RateCentsPerHour, 80, 0);
        Assert.False(noSchedule.HasSchedule);

        Assert.Equal(15_00, PayForecast.TypicalMiss([(100_00, 110_00), (100_00, 80_00)]));
    }

    [Fact]
    public void The_payday_plan_allocates_the_check_and_says_where_each_part_goes()
    {
        var plan = PaydayPlan.Build(1_500_00,
            [new("bill:rent", "Rent", 850_00, new(2027, 3, 1), "Checking"), new("bill:phone", "Phone", 60_00, new(2027, 2, 25), "Checking")],
            [new("debt", "Card payment", 200_00, "Test Visa", "Half the month's card payment."), new("savings", "Savings", 100_00, "Test Savings", "Half the month's savings."), new("sweep:x", "Sweep", 0, "Test Visa", "")]);
        // The bills become one "keep" line for the account they're paid from, listing each.
        Assert.Equal(["keep:Checking", "debt", "savings"], plan.Items.Select(i => i.Key)); // spending money is Home's Left to Spend
        var keep = plan.Items[0];
        Assert.Equal(("2 bills due Feb 25 to Mar 1", 910_00L, "Checking"), (keep.Label, keep.Cents, keep.Destination));
        Assert.Equal(["Phone", "Rent"], keep.Bills!.Select(b => b.Name));
        Assert.Equal(0, plan.ShortCents);

        var short_ = PaydayPlan.Build(1_000_00, [new("bill:rent", "Rent", 850_00, new(2027, 3, 1), "Checking")], [new("debt", "Card payment", 200_00, "Test Visa", "")]);
        Assert.Equal(50_00L, short_.ShortCents);

        Assert.Equal((150_00L, 150_01L), (PaydayPlan.Share(300_01, 2, 0), PaydayPlan.Share(300_01, 2, 1)));
    }

    [Fact]
    public void Every_card_gets_its_minimum_before_the_extra_goes_to_the_target()
    {
        CardMinimum visa = new("debt:visa", "Test Visa", 25_00), store = new("debt:store", "Test Store Card", 35_00);
        // $200 this check: both minimums ($60), and the other $140 on the target (say the Visa has the higher APR).
        var split = PaydayPlan.SplitCardPayment(200_00, [visa, store], "debt:visa");
        Assert.Equal([("debt:visa", 165_00L, 140_00L), ("debt:store", 35_00L, 0L)], split.Select(s => (s.Card.Key, s.Cents, s.ExtraCents)));
        // A payment smaller than the minimums still lists every minimum in full; nothing extra.
        Assert.Equal([25_00L, 35_00L], PaydayPlan.SplitCardPayment(40_00, [visa, store], "debt:visa").Select(s => s.Cents));
    }
}
