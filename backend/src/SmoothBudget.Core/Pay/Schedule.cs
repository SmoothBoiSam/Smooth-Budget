using System.Globalization;
using System.Text.RegularExpressions;
using SmoothBudget.Core.Import;

namespace SmoothBudget.Core.Pay;

/// <param name="Flight">The flight number, or a code such as CDT for a credit-only row.</param>
public record ScheduleLeg(
    int Seq, string Flight, string Origin, string Destination, TimeOnly Departure, TimeOnly Arrival,
    int BlockMinutes, int CreditMinutes, bool Deadhead)
{
    /// <summary>A real flight number (not a code like CDT) that isn't a deadhead: it earns boarding pay (SPEC 5.2).</summary>
    public bool IsRevenue => !Deadhead && Flight.Length > 0 && Flight.All(char.IsAsciiDigit);
}

/// <param name="Report">Local time at the first departure airport.</param>
/// <param name="Release">Local time at the last arrival airport.</param>
/// <param name="CreditMinutes">The printed day credit (text export), or the one the rules give (CSV).</param>
public record DutyDay(
    DateOnly Date, TimeOnly Report, TimeOnly Release, int BlockMinutes, int LegCreditMinutes, int CreditMinutes, int DutyMinutes,
    IReadOnlyList<ScheduleLeg> Legs)
{
    public int RevenueLegs => Legs.Count(l => l.IsRevenue);
}

/// <param name="Key">Stable within a month ("N1459 11/06"), for your overtime choice to survive a re-import.</param>
/// <param name="Start">The first report, in base local time.</param>
public record Pairing(string Key, string Code, DateTime Start, int BlockMinutes, int CreditMinutes, int TafbMinutes, IReadOnlyList<DutyDay> Days)
{
    public DateTime End => Start.AddMinutes(TafbMinutes);
    public int RevenueLegs => Days.Sum(d => d.RevenueLegs);
    public int DayCreditSum => Days.Sum(d => d.CreditMinutes);
}

/// <summary>A non-flying entry such as SKV (Start, End, Credit). Unpaid unless you switch it on (SPEC 5.2).</summary>
public record ScheduleEntry(string Key, string Code, DateTime Start, DateTime End, int CreditMinutes);

/// <param name="Source">"text" (the detailed export, printed values) or "csv" (estimated).</param>
/// <param name="Checks">The credit rules checked against the printed values (text export) or what was estimated (CSV).</param>
public record ParsedSchedule(
    int Year, int Month, string Source, IReadOnlyList<ScheduleEntry> Entries, IReadOnlyList<Pairing> Pairings,
    IReadOnlyList<string> Checks, IReadOnlyList<string> Warnings)
{
    public int CreditMinutes => Pairings.Sum(p => p.CreditMinutes);
}

public class ScheduleParseException(string message) : Exception(message);

/// <summary>The credit rules (SPEC 5.2, Appendix A), used to validate the text export and to estimate from the CSV.</summary>
public static class CreditRules
{
    /// <summary>Day credit: the greater of leg credit, duty ÷ 2 (rounded up to the minute), and the 4:12 minimum.</summary>
    public static int DayCredit(int legCreditMinutes, int dutyMinutes, PayRules rules) =>
        Math.Max(Math.Max(legCreditMinutes, (dutyMinutes + rules.DutyHoursPerCreditHour - 1) / rules.DutyHoursPerCreditHour), rules.MinDayCreditMinutes);

    /// <summary>Pairing credit: the greater of the day credits and TAFB ÷ 4 (to the nearest minute).</summary>
    public static int PairingCredit(int dayCreditSum, int tafbMinutes, PayRules rules) =>
        Math.Max(dayCreditSum, (int)Math.Round(tafbMinutes / (decimal)rules.TafbHoursPerCreditHour, MidpointRounding.AwayFromZero));
}

/// <summary>
/// The SkedPlus+ detailed text export (SPEC 5.2). Reads only what the pay engine needs: the month, non-flying entries,
/// pairings, duty days and legs. The header's name and number, hotels and the Crew Members list are never read into
/// the result (CLAUDE.md: never store other crew members' names or employee numbers).
/// </summary>
public static partial class ScheduleTextParser
{
    public static ParsedSchedule Parse(string text, PayRules rules)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int? year = null, month = null;
        var entries = new List<ScheduleEntry>();
        var pairings = new List<Pairing>();
        var checks = new List<string>();
        var warnings = new List<string>();

        string? code = null;
        DateOnly pairingDate = default;
        int pBlock = 0, pCredit = 0, pTafb = 0;
        var days = new List<DutyDay>();
        DateOnly? dayDate = null;
        TimeOnly report = default, release = default;
        var legs = new List<ScheduleLeg>();
        int[]? columns = null;
        var inCrew = false;

        void EndPairing()
        {
            if (code is null) return;
            if (days.Count == 0) { warnings.Add($"{code} has no duty days; skipped."); code = null; return; }
            var start = days[0].Date.ToDateTime(days[0].Report);
            var p = new Pairing($"{code} {pairingDate:MM/dd}", code, start, pBlock, pCredit, pTafb, days.ToList());
            var rule = CreditRules.PairingCredit(p.DayCreditSum, pTafb, rules);
            checks.Add(rule == pCredit
                ? $"{code}: printed credit {Hm.Format(pCredit)} matches the rules (day credits {Hm.Format(p.DayCreditSum)}, TAFB ÷ 4 = {Hm.Format(pTafb / rules.TafbHoursPerCreditHour)})."
                : $"{code}: printed credit {Hm.Format(pCredit)}, but the rules give {Hm.Format(rule)}. The printed value is used.");
            if (rule != pCredit) warnings.Add(checks[^1]);
            pairings.Add(p);
            code = null;
            days.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("====", StringComparison.Ordinal)) { inCrew = false; continue; }
            if (inCrew) continue; // other crew members' names and numbers are never read
            if (trimmed.Length == 0 || trimmed.All(c => c is '_' or '¯' or '-' or '=')) continue;

            if (year is null && MonthHeader().Match(line) is { Success: true } mh)
            {
                month = DateTime.ParseExact(mh.Groups[1].Value, "MMMM", CultureInfo.InvariantCulture).Month;
                year = int.Parse(mh.Groups[2].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (trimmed.StartsWith("Crew Members", StringComparison.OrdinalIgnoreCase)) { inCrew = true; continue; }

            if (PairingHeader().Match(trimmed) is { Success: true } ph && i + 1 < lines.Length && PairingTotals().Match(lines[i + 1]) is { Success: true } pt)
            {
                EndPairing();
                code = ph.Groups[1].Value;
                pairingDate = DateOnly.ParseExact(ph.Groups[2].Value, "MM/dd/yyyy", CultureInfo.InvariantCulture);
                (pBlock, pCredit, pTafb) = (Hm.Parse(pt.Groups[1].Value), Hm.Parse(pt.Groups[2].Value), Hm.Parse(pt.Groups[3].Value));
                i++;
                continue;
            }

            if (EntryCode().IsMatch(trimmed) && i + 3 < lines.Length && lines[i + 1].TrimStart().StartsWith("Start:", StringComparison.Ordinal))
            {
                EndPairing();
                var s = Stamp(lines[i + 1]);
                var e = Stamp(lines[i + 2]);
                var c = lines[i + 3].Trim().StartsWith("Credit:", StringComparison.Ordinal) ? Hm.Parse(lines[i + 3].Trim()[7..]) : 0;
                entries.Add(new ScheduleEntry($"{trimmed} {s:MM/dd}", trimmed, s, e, c));
                i += 3;
                continue;
            }

            if (DayHeader().Match(trimmed) is { Success: true } dh)
            {
                dayDate = DateOnly.ParseExact(dh.Groups[1].Value, "MM-dd-yyyy", CultureInfo.InvariantCulture);
                report = TimeOnly.ParseExact(dh.Groups[2].Value.PadLeft(5, '0'), "HH:mm", CultureInfo.InvariantCulture);
                release = TimeOnly.ParseExact(dh.Groups[3].Value.PadLeft(5, '0'), "HH:mm", CultureInfo.InvariantCulture);
                legs = [];
                continue;
            }

            if (line.Contains("Flight", StringComparison.Ordinal) && line.Contains("Org", StringComparison.Ordinal) && line.Contains("Credit", StringComparison.Ordinal))
            {
                columns = Header.Select(h => line.IndexOf(h, StringComparison.Ordinal)).ToArray();
                continue;
            }

            if (dayDate is { } date && LegRow().IsMatch(line) && columns is not null)
            {
                legs.Add(Leg(line, columns));
                continue;
            }

            if (dayDate is { } d && DayTotal().Match(line) is { Success: true } dt)
            {
                var block = Hm.Parse(dt.Groups[1].Value);
                var credit = Hm.Parse(dt.Groups[2].Value);
                var duty = dt.Groups[3].Success ? Hm.Parse(dt.Groups[3].Value) : 0;
                var legCredit = legs.Sum(l => l.CreditMinutes);
                var day = new DutyDay(d, report, release, block, legCredit, credit, duty, legs.ToList());
                if (dt.Groups[3].Success)
                {
                    var rule = CreditRules.DayCredit(legCredit, duty, rules);
                    var label = $"{code} {d:MM-dd}";
                    checks.Add(rule == credit
                        ? $"{label}: printed day credit {Hm.Format(credit)} matches the rules (legs {Hm.Format(legCredit)}, duty {Hm.Format(duty)} ÷ 2, minimum {Hm.Format(rules.MinDayCreditMinutes)})."
                        : $"{label}: printed day credit {Hm.Format(credit)}, but the rules give {Hm.Format(rule)}. The printed value is used.");
                    if (rule != credit) warnings.Add(checks[^1]);
                }
                days.Add(day);
                dayDate = null;
                continue;
            }
            // Hotel, layover and anything else are ignored.
        }
        EndPairing();

        if (year is null || month is null)
        {
            var first = pairings.Select(p => (DateTime?)p.Start).Concat(entries.Select(e => (DateTime?)e.Start)).Min();
            if (first is null) throw new ScheduleParseException("This doesn't look like a SkedPlus+ schedule export: no month and no trips found.");
            (year, month) = (first.Value.Year, first.Value.Month);
        }
        if (pairings.Count == 0 && entries.Count == 0)
            throw new ScheduleParseException("No trips found in this file.");
        return new ParsedSchedule(year.Value, month.Value, "text", entries, pairings, checks, warnings);
    }

    private static readonly string[] Header = ["Flight", "Tail", "Org", "Dest", "Dep", "Arr", "Pax", "Block", "Credit", "D/PU", "Dhd", "Turn"];

    /// <summary>Fixed-width row: each value is taken from the column whose header starts nearest at or before it.</summary>
    private static ScheduleLeg Leg(string line, int[] columns)
    {
        var seqMatch = LegRow().Match(line);
        var seq = int.Parse(seqMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        var values = new string[Header.Length];
        foreach (Match token in Token().Matches(line, seqMatch.Length))
        {
            var col = -1;
            for (var c = 0; c < columns.Length; c++)
                if (columns[c] >= 0 && columns[c] <= token.Index + 1) col = c;
            if (col >= 0) values[col] = values[col] is null ? token.Value : values[col] + " " + token.Value;
        }
        string V(int c) => values[c] ?? "";
        TimeOnly T(int c) => TimeOnly.ParseExact(V(c).PadLeft(5, '0'), "HH:mm", CultureInfo.InvariantCulture);
        var dhd = V(10).Trim();
        return new ScheduleLeg(seq, V(0), V(2), V(3), T(4), T(5), Hm.TryParse(V(7), out var b) ? b : 0, Hm.TryParse(V(8), out var cr) ? cr : 0,
            Deadhead: dhd.Length > 0 && !dhd.Equals("N", StringComparison.OrdinalIgnoreCase));
    }

    private static DateTime Stamp(string line) =>
        DateTime.ParseExact(StampValue().Match(line).Value, "MM/dd/yyyy HH:mm", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{4})\s*$")]
    private static partial Regex MonthHeader();
    [GeneratedRegex(@"^(\S+)\s+(\d{2}/\d{2}/\d{4})$")]
    private static partial Regex PairingHeader();
    [GeneratedRegex(@"Block:\s*(\d+:\d\d)\s+Credit:\s*(\d+:\d\d)\s+TAFB:\s*(\d+:\d\d)")]
    private static partial Regex PairingTotals();
    [GeneratedRegex(@"^[A-Z]{2,5}$")]
    private static partial Regex EntryCode();
    [GeneratedRegex(@"^\w+day\s+(\d{2}-\d{2}-\d{4})\s+Report:\s*(\d{1,2}:\d\d)\s+Release:\s*(\d{1,2}:\d\d)")]
    private static partial Regex DayHeader();
    [GeneratedRegex(@"^\s*(\d+)\.\s")]
    private static partial Regex LegRow();
    [GeneratedRegex(@"Day Total:\s*(\d+:\d\d)\s+(\d+:\d\d)(?:\s+Duty:\s*(\d+:\d\d))?")]
    private static partial Regex DayTotal();
    [GeneratedRegex(@"\S+")]
    private static partial Regex Token();
    [GeneratedRegex(@"\d{2}/\d{2}/\d{4}\s+\d{1,2}:\d\d")]
    private static partial Regex StampValue();
}

/// <summary>
/// The older SkedPlus+ CSV export (one row per leg, decimal hours, local times): the fallback with fewer details.
/// Report, release, duty and TAFB are estimated (SPEC 5.2), with every time converted to UTC through the airport
/// time-zone table first. The crew columns are never read.
/// </summary>
public static class ScheduleCsvParser
{
    public static ParsedSchedule Parse(string text, PayRules rules, Func<string, TimeZoneInfo?> zoneLookup)
    {
        var rows = CsvReader.Parse(text);
        if (rows.Count < 2) throw new ScheduleParseException("The CSV has no rows.");
        var header = rows[0].Select(h => h.Trim()).ToList();
        int Col(string name) => header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        int flight = Col("Flight"), date = Col("Date"), org = Col("Origin"), dest = Col("Dest"), dep = Col("Depart"), arr = Col("Arrive"), block = Col("Block"), creditCol = Col("Credit");
        if (new[] { flight, date, org, dest, dep, arr, creditCol }.Any(c => c < 0))
            throw new ScheduleParseException("This doesn't look like a SkedPlus+ schedule CSV (expected Flight, Date, Origin, Dest, Depart, Arrive, Block and Credit columns).");

        var warnings = new List<string>();
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TimeZoneInfo Zone(string code)
        {
            var z = rules.Airport(code) is { } a ? zoneLookup(a.TimeZoneId) : null;
            if (z is not null) return z;
            if (unknown.Add(code)) warnings.Add($"{code} isn't in the airport time-zone table; its times were treated as {rules.Base} time. Add it under Pay rules.");
            return zoneLookup(rules.Airport(rules.Base)?.TimeZoneId ?? "America/Denver") ?? TimeZoneInfo.Utc;
        }
        DateTime Utc(DateOnly d, TimeOnly t, string airport) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(d.ToDateTime(t), DateTimeKind.Unspecified), Zone(airport));

        var legs = new List<(DateOnly Date, ScheduleLeg Leg, DateTime DepUtc, DateTime ArrUtc, decimal CreditHours)>();
        foreach (var r in rows.Skip(1))
        {
            string F(int c) => c >= 0 && c < r.Length ? r[c].Trim() : "";
            if (F(flight).Length == 0 && F(date).Length == 0) continue;
            if (!DateOnly.TryParseExact(F(date), ["MM/dd/yyyy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                || !TimeOnly.TryParseExact(F(dep).PadLeft(5, '0'), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                || !TimeOnly.TryParseExact(F(arr).PadLeft(5, '0'), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            {
                warnings.Add($"Skipped a row that couldn't be read (flight {F(flight)}).");
                continue;
            }
            var creditHours = decimal.TryParse(F(creditCol), NumberStyles.Number, CultureInfo.InvariantCulture, out var ch) ? ch : 0m;
            var blockHours = decimal.TryParse(F(block), NumberStyles.Number, CultureInfo.InvariantCulture, out var bh) ? bh : 0m;
            var depUtc = Utc(d, dt, F(org));
            var arrUtc = Utc(d, at, F(dest));
            if (arrUtc < depUtc) arrUtc = arrUtc.AddDays(1);
            legs.Add((d, new ScheduleLeg(0, F(flight), F(org).ToUpperInvariant(), F(dest).ToUpperInvariant(), dt, at,
                (int)Math.Round(blockHours * 60), (int)Math.Round(creditHours * 60), false), depUtc, arrUtc, creditHours));
        }
        if (legs.Count == 0) throw new ScheduleParseException("No legs found in this file.");
        legs = legs.OrderBy(l => l.DepUtc).ToList();

        // A pairing ends when a leg arrives at base and the next departure is at least 8 hours later.
        var groups = new List<List<int>> { new() };
        for (var i = 0; i < legs.Count; i++)
        {
            groups[^1].Add(i);
            var atBase = legs[i].Leg.Destination.Equals(rules.Base, StringComparison.OrdinalIgnoreCase);
            if (i + 1 < legs.Count && atBase && legs[i + 1].DepUtc - legs[i].ArrUtc >= TimeSpan.FromHours(rules.PairingBreakHours))
                groups.Add([]);
        }

        var baseZone = Zone(rules.Base);
        var pairings = new List<Pairing>();
        var checks = new List<string>();
        foreach (var g in groups.Where(g => g.Count > 0))
        {
            var days = new List<DutyDay>();
            DateTime? firstReportUtc = null, lastReleaseUtc = null;
            var seq = 0;
            foreach (var day in g.GroupBy(i => legs[i].Date))
            {
                var dl = day.Select(i => legs[i]).ToList();
                var first = dl[0];
                var last = dl[^1];
                var intl = rules.Airport(first.Leg.Origin)?.International ?? false;
                var reportUtc = first.DepUtc.AddMinutes(-(intl ? rules.ReportMinutesBeforeInternational : rules.ReportMinutesBefore));
                var releaseUtc = last.ArrUtc.AddMinutes(rules.ReleaseMinutesAfter);
                firstReportUtc ??= reportUtc;
                lastReleaseUtc = releaseUtc;
                var duty = (int)Math.Round((releaseUtc - reportUtc).TotalMinutes);
                var legCredit = (int)Math.Round(dl.Sum(x => x.CreditHours) * 60);
                var credit = CreditRules.DayCredit(legCredit, duty, rules);
                var report = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(reportUtc, Zone(first.Leg.Origin)));
                var release = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(releaseUtc, Zone(last.Leg.Destination)));
                days.Add(new DutyDay(day.Key, report, release, dl.Sum(x => x.Leg.BlockMinutes), legCredit, credit, duty,
                    dl.Select(x => x.Leg with { Seq = ++seq }).ToList()));
                checks.Add($"Trip from {legs[g[0]].Date:MM-dd}, {day.Key:MM-dd}: report {report:HH:mm}, release {release:HH:mm}, duty {Hm.Format(duty)}, credit {Hm.Format(credit)} (estimated).");
            }
            var tafb = (int)Math.Round((lastReleaseUtc!.Value - firstReportUtc!.Value).TotalMinutes);
            var sum = days.Sum(d => d.CreditMinutes);
            var pCredit = CreditRules.PairingCredit(sum, tafb, rules);
            var start = TimeZoneInfo.ConvertTimeFromUtc(firstReportUtc.Value, baseZone);
            var code = $"Trip {days[0].Date:MM/dd}";
            pairings.Add(new Pairing($"{code}", code, DateTime.SpecifyKind(start, DateTimeKind.Unspecified), days.Sum(d => d.BlockMinutes), pCredit, tafb, days));
            checks.Add($"{code}: credit {Hm.Format(pCredit)} (day credits {Hm.Format(sum)}, TAFB {Hm.Format(tafb)} ÷ 4), estimated.");
        }

        var month = pairings.GroupBy(p => (p.Start.Year, p.Start.Month)).OrderByDescending(x => x.Count()).First().Key;
        warnings.Add("The CSV export has no report, release or credit-only days, so these are estimates. The detailed text export is exact.");
        return new ParsedSchedule(month.Year, month.Month, "csv", [], pairings, checks, warnings);
    }

    /// <summary>Finds a time zone by IANA id on any platform.</summary>
    public static TimeZoneInfo? FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }
        return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win) && TimeZoneInfo.TryFindSystemTimeZoneById(win, out var z) ? z : null;
    }
}
