namespace SmoothBudget.Core.Pay;

/// <summary>An hourly rate that applies from this many completed years of service.</summary>
public record RateStep(int Years, long CentsPerHour);

/// <summary>An airport's time zone (IANA id) and whether it's outside the US (the earlier report time applies).</summary>
public record AirportZone(string Code, string TimeZoneId, bool International = false);

/// <summary>
/// The pay rules table (SPEC section 5, Appendix A). Every value is data the owner can edit; nothing here is hard-coded
/// in the engine. <see cref="Default"/> is seeded from the policy manual (revision 28).
/// </summary>
public record PayRules
{
    public IReadOnlyList<RateStep> Rates { get; init; } = [];
    public long PerDiemCentsPerHour { get; init; }

    /// <summary>The minimum daily guarantee (4:12).</summary>
    public int MinDayCreditMinutes { get; init; }
    /// <summary>The duty guarantee: 1 hour of credit per this many hours of duty.</summary>
    public int DutyHoursPerCreditHour { get; init; }
    /// <summary>The time-away-from-base guarantee: 1 hour of credit per this many hours away.</summary>
    public int TafbHoursPerCreditHour { get; init; }

    public int LineGuaranteeHours { get; init; }
    public int ReserveGuaranteeHours { get; init; }
    public int PartTimeGuaranteeHours { get; init; }

    public int OverrideAboveHours { get; init; }
    public long OverrideCentsPerHour { get; init; }
    public decimal OvertimeMultiplier { get; init; }

    public int HolidayCreditMinutes { get; init; }
    public IReadOnlyList<DateOnly> Holidays { get; init; } = [];

    /// <summary>Boarding pay: this many hours per boarded revenue leg, at this fraction of the hourly rate (0.5 × half rate).</summary>
    public decimal BoardingHoursPerLeg { get; init; }
    public decimal BoardingRateFactor { get; init; }

    /// <summary>For the older CSV export, which has no report or release times: report before the first departure, release after the last arrival.</summary>
    public int ReportMinutesBefore { get; init; }
    public int ReportMinutesBeforeInternational { get; init; }
    public int ReleaseMinutesAfter { get; init; }
    /// <summary>A CSV pairing ends when a leg arrives at base and the next departure is at least this many hours later.</summary>
    public int PairingBreakHours { get; init; }

    public string Base { get; init; } = "DEN";
    public IReadOnlyList<AirportZone> Airports { get; init; } = [];

    /// <summary>Pay dates: the first-half check (1st to the 15th) is paid on <see cref="FirstHalfPayDay"/>, the second half on <see cref="SecondHalfPayDay"/> of the next month.</summary>
    public int FirstHalfPayDay { get; init; }
    public int SecondHalfPayDay { get; init; }

    /// <summary>The pay stub's names for the lines the engine forecasts from your schedule.</summary>
    public StubLineNames Lines { get; init; } = new();

    public long RateFor(int yearsOfService) =>
        Rates.Where(r => r.Years <= Math.Max(0, yearsOfService)).OrderByDescending(r => r.Years).Select(r => r.CentsPerHour).FirstOrDefault(Rates.Count > 0 ? Rates.Min(r => r.CentsPerHour) : 0);

    public AirportZone? Airport(string code) => Airports.FirstOrDefault(a => string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase));

    public static PayRules Default { get; } = new()
    {
        Rates =
        [
            new(0, 30_32), new(3, 31_26), new(4, 33_11), new(5, 34_29), new(6, 36_00), new(7, 37_54), new(8, 38_88), new(9, 40_00),
            new(10, 41_14), new(11, 42_22), new(12, 42_93), new(13, 44_00), new(14, 44_58), new(15, 45_12), new(16, 45_73), new(17, 46_38),
            new(18, 47_01), new(19, 47_66), new(20, 48_09), new(21, 48_52), new(22, 48_94), new(23, 49_37), new(24, 49_80), new(25, 50_21),
        ],
        PerDiemCentsPerHour = 2_25,
        MinDayCreditMinutes = 4 * 60 + 12,
        DutyHoursPerCreditHour = 2,
        TafbHoursPerCreditHour = 4,
        LineGuaranteeHours = 80,
        ReserveGuaranteeHours = 76,
        PartTimeGuaranteeHours = 40,
        OverrideAboveHours = 87,
        OverrideCentsPerHour = 5_00,
        OvertimeMultiplier = 1.5m,
        HolidayCreditMinutes = 4 * 60 + 18,
        Holidays =
        [
            new(2026, 1, 1), new(2026, 5, 25), new(2026, 7, 4), new(2026, 9, 7), new(2026, 11, 26), new(2026, 12, 25),
            new(2027, 1, 1), new(2027, 5, 31), new(2027, 7, 4), new(2027, 9, 6), new(2027, 11, 25), new(2027, 12, 25),
            new(2028, 1, 1), new(2028, 5, 29), new(2028, 7, 4), new(2028, 9, 4), new(2028, 11, 23), new(2028, 12, 25),
        ],
        BoardingHoursPerLeg = 0.5m,
        BoardingRateFactor = 0.5m,
        ReportMinutesBefore = 45,
        ReportMinutesBeforeInternational = 60,
        ReleaseMinutesAfter = 15,
        PairingBreakHours = 8,
        Base = "DEN",
        Airports =
        [
            new("DEN", "America/Denver"), new("COS", "America/Denver"), new("GJT", "America/Denver"), new("ASE", "America/Denver"),
            new("CPR", "America/Denver"), new("CYS", "America/Denver"), new("LAR", "America/Denver"), new("RKS", "America/Denver"),
            new("SHR", "America/Denver"), new("GCC", "America/Denver"), new("RAP", "America/Denver"), new("ROW", "America/Denver"),
            new("BIL", "America/Denver"), new("BZN", "America/Denver"), new("MSO", "America/Denver"), new("SLC", "America/Denver"),
            new("ABQ", "America/Denver"), new("ELP", "America/Denver"), new("BOI", "America/Boise"),
            new("PHX", "America/Phoenix"), new("PRC", "America/Phoenix"), new("FLG", "America/Phoenix"), new("TUS", "America/Phoenix"),
            new("PIR", "America/Chicago"), new("JLN", "America/Chicago"), new("JMS", "America/Chicago"), new("FAR", "America/Chicago"),
            new("ORD", "America/Chicago"), new("LNK", "America/Chicago"), new("OMA", "America/Chicago"), new("MSP", "America/Chicago"),
            new("DFW", "America/Chicago"), new("IAH", "America/Chicago"), new("MCI", "America/Chicago"), new("STL", "America/Chicago"),
            new("FSD", "America/Chicago"), new("SUX", "America/Chicago"), new("BIS", "America/Chicago"), new("MOT", "America/Chicago"),
            new("ICT", "America/Chicago"), new("GRI", "America/Chicago"), new("SGF", "America/Chicago"), new("ATY", "America/Chicago"),
            new("ABR", "America/Chicago"), new("DSM", "America/Chicago"), new("XNA", "America/Chicago"), new("HYS", "America/Chicago"),
            new("LAX", "America/Los_Angeles"), new("SFO", "America/Los_Angeles"), new("SEA", "America/Los_Angeles"), new("PDX", "America/Los_Angeles"),
            new("LAS", "America/Los_Angeles"), new("SAN", "America/Los_Angeles"), new("SMF", "America/Los_Angeles"), new("RNO", "America/Los_Angeles"),
            new("YQR", "America/Regina", true), new("YXE", "America/Regina", true), new("YYC", "America/Edmonton", true), new("YEG", "America/Edmonton", true),
            new("YWG", "America/Winnipeg", true),
        ],
        FirstHalfPayDay = 22,
        SecondHalfPayDay = 7,
    };
}

/// <summary>How the owner's pay stub names the lines that come from the schedule (Appendix C).</summary>
public record StubLineNames
{
    public string Regular { get; init; } = "Regular";
    public string Boarding { get; init; } = "Boarding Pay";
    public string PerDiem { get; init; } = "NonTax Per Diem";
    public string Override { get; init; } = "Over 87 Hours";
    public string Holiday { get; init; } = "Holiday Pay";
    public string Overtime { get; init; } = "Overtime";
    public IReadOnlyList<string> Pto { get; init; } = ["User Pay", "Vacation Pay"];

    /// <summary>Lines the forecast computes itself, so they're never treated as fixed amounts learned from stubs.</summary>
    public bool IsScheduleDriven(string name) =>
        new[] { Regular, Boarding, PerDiem, Override, Holiday, Overtime }.Contains(name, StringComparer.OrdinalIgnoreCase)
        || Pto.Contains(name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Durations printed as H:MM.</summary>
public static class Hm
{
    public static int Parse(string s)
    {
        var t = s.Trim();
        var i = t.IndexOf(':');
        if (i < 1 || !int.TryParse(t[..i], out var h) || !int.TryParse(t[(i + 1)..], out var m) || m is < 0 or > 59)
            throw new FormatException($"Not an H:MM duration: \"{s}\"");
        return h * 60 + m;
    }

    public static bool TryParse(string s, out int minutes)
    {
        try { minutes = Parse(s); return true; }
        catch (FormatException) { minutes = 0; return false; }
    }

    public static string Format(int minutes) => $"{minutes / 60}:{minutes % 60:00}";

    /// <summary>Money for minutes at a rate per hour, rounded to the cent (half away from zero).</summary>
    public static long Pay(long minutes, long centsPerHour, decimal multiplier = 1m) =>
        (long)Math.Round(minutes * centsPerHour * multiplier / 60m, MidpointRounding.AwayFromZero);
}
