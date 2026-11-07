using System.Globalization;

namespace SmoothBudget.Core.Pay;

/// <summary>A word on the page and where it sits (PDF points; Bottom grows upward).</summary>
public record PageWord(string Text, double Left, double Right, double Bottom);

/// <param name="Checks">Arithmetic checks shown on the review screen ("Earnings add up to gross").</param>
public record ParsedPayStub(PayStubData Stub, IReadOnlyList<string> Checks, IReadOnlyList<string> Problems);

public class PayStubParseException(string message) : Exception(message);

/// <summary>
/// The deterministic template parser for the owner's pay advice layout (SPEC 5.3). It reads numbers only from the
/// labeled sections: earnings, taxes, deductions, the totals row, the pay dates and rate, and the user and vacation hours.
/// The name, address, SSN, employee id and the net pay distribution (bank account) are never read.
/// </summary>
public static class PayStubParser
{
    private const double RowTolerance = 2.5;
    private const double ColumnTolerance = 14;

    public static ParsedPayStub Parse(IReadOnlyList<PageWord> words)
    {
        var rows = Rows(words);
        DateOnly Date(string label)
        {
            foreach (var r in rows)
                for (var i = 0; i < r.Count; i++)
                    if (Phrase(r, i, label) is { } end && end < r.Count && DateOnly.TryParseExact(r[end].Text, ["MM/dd/yyyy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                        return d;
            throw new PayStubParseException($"Couldn't find \"{label}\". This doesn't look like the pay advice layout the app knows.");
        }
        var begin = Date("Pay Begin Date:");
        var end = Date("Pay End Date:");
        var paid = Date("Advice Date:");
        long rate = 0;
        foreach (var r in rows)
            for (var i = 0; i < r.Count; i++)
                if (Phrase(r, i, "Pay Rate:") is { } e && e < r.Count && Money(r[e].Text) is { } v) rate = v;

        // Earnings and taxes: the header row with Description … Rate … Hours … Earnings … Description … Current … YTD.
        var headerIndex = rows.FindIndex(r => r.Any(w => w.Text == "Rate") && r.Count(w => w.Text == "Description") >= 2);
        if (headerIndex < 0) throw new PayStubParseException("Couldn't find the earnings table.");
        var header = rows[headerIndex];
        var rateCol = header.First(w => w.Text == "Rate");
        var hours = header.Where(w => w.Text == "Hours").ToList();
        var earnings = header.Where(w => w.Text == "Earnings").ToList();
        var taxDesc = header.Where(w => w.Text == "Description").OrderBy(w => w.Left).Last();
        var taxCurrent = header.First(w => w.Text == "Current" && w.Left > taxDesc.Left);
        var taxYtd = header.First(w => w.Text == "YTD" && w.Left > taxDesc.Left);
        if (hours.Count < 1 || earnings.Count < 1) throw new PayStubParseException("The earnings table's columns weren't found.");
        var earnCols = new (string Key, double Right)[] { ("rate", rateCol.Right), ("hours", hours[0].Right), ("current", earnings[0].Right) }
            .Concat(earnings.Skip(1).Select((w, i) => ($"other{i}", w.Right))).Concat(hours.Skip(1).Select((w, i) => ($"otherh{i}", w.Right))).ToArray();

        var earningLines = new List<StubEarning>();
        var taxLines = new List<StubAmount>();
        long? grossTotal = null, taxTotal = null;
        var i0 = headerIndex + 1;
        for (; i0 < rows.Count; i0++)
        {
            var r = rows[i0];
            var left = r.Where(w => w.Right < taxDesc.Left - 2).ToList();
            var right = r.Where(w => w.Left >= taxDesc.Left - 4).ToList();
            var isTotal = left.FirstOrDefault()?.Text == "Total:";

            var label = Label(left, rateCol.Left - 4);
            var values = Columns(left, rateCol.Left - 4, earnCols);
            if (isTotal) grossTotal = values.GetValueOrDefault("current");
            else if (label.Length > 0 && values.TryGetValue("current", out var cur))
                earningLines.Add(new StubEarning(label, values.GetValueOrDefault("rate"), values.TryGetValue("hours", out var h) ? h / 100m : null, cur));

            var tLabel = Label(right, taxCurrent.Left - 4);
            var tValues = Columns(right, taxCurrent.Left - 4, [("current", taxCurrent.Right), ("ytd", taxYtd.Right)]);
            if (tLabel == "Total") taxTotal = tValues.GetValueOrDefault("current");
            else if (tLabel.Length > 0 && tValues.TryGetValue("current", out var tc)) taxLines.Add(new StubAmount(tLabel, tc));
            if (isTotal) break;
        }

        // Deductions: BEFORE TAX DEDUCTIONS and AFTER TAX DEDUCTIONS, each with Description, Current and YTD.
        var dedHeader = rows.FindIndex(i0, r => r.Count(w => w.Text == "Description") >= 3 && r.Any(w => w.Text == "Current"));
        var preTax = new List<StubAmount>();
        var afterTax = new List<StubAmount>();
        var summaryIndex = rows.FindIndex(r => r.FirstOrDefault()?.Text == "Current:");
        if (dedHeader >= 0)
        {
            var descs = rows[dedHeader].Where(w => w.Text == "Description").OrderBy(w => w.Left).ToList();
            var currents = rows[dedHeader].Where(w => w.Text == "Current").OrderBy(w => w.Left).ToList();
            var ytds = rows[dedHeader].Where(w => w.Text == "YTD").OrderBy(w => w.Left).ToList();
            var last = summaryIndex > dedHeader ? summaryIndex : rows.Count;
            for (var k = dedHeader + 1; k < last; k++)
            {
                if (rows[k].Any(w => w.Text is "TOTAL" or "GROSS")) break;
                for (var s = 1; s < descs.Count && s < currents.Count && s < ytds.Count; s++)
                {
                    var from = descs[s].Left - 4;
                    var to = s + 1 < descs.Count ? descs[s + 1].Left - 4 : double.MaxValue;
                    var part = rows[k].Where(w => w.Left >= from && w.Left < to).ToList();
                    var label = Label(part, currents[s].Left - 4);
                    var vals = Columns(part, currents[s].Left - 4, [("current", currents[s].Right), ("ytd", ytds[s].Right)]);
                    if (label.Length == 0 || label == "Total:" || !vals.TryGetValue("current", out var c)) continue;
                    (s == 1 ? preTax : afterTax).Add(new StubAmount(label, c));
                }
            }
        }

        // The totals row: TOTAL GROSS, FED TAXABLE GROSS, TOTAL TAXES, TOTAL DEDUCTIONS, NET PAY.
        if (summaryIndex < 0) throw new PayStubParseException("Couldn't find the totals row (TOTAL GROSS … NET PAY).");
        var totals = rows[summaryIndex].Select(w => Money(w.Text)).OfType<long>().ToList();
        if (totals.Count < 5) throw new PayStubParseException("The totals row didn't have five amounts.");
        var ytdRow = rows.ElementAtOrDefault(summaryIndex + 1);
        var ytd = ytdRow?.FirstOrDefault()?.Text == "YTD:" ? ytdRow.Select(w => Money(w.Text)).OfType<long>().ToList() : [];

        // User and vacation hours earned this period.
        decimal? userEarned = null, vacationEarned = null;
        var vacationHeader = rows.SelectMany(r => r).FirstOrDefault(w => w.Text == "VACATION");
        var earnedRow = rows.FirstOrDefault(r => r.Any(w => w.Text == "Earned:"));
        if (earnedRow is not null)
            foreach (var w in earnedRow)
                if (Money(w.Text) is { } v && w.Right < 300)
                {
                    if (vacationHeader is not null && w.Left >= vacationHeader.Left - 30) vacationEarned = v / 100m;
                    else userEarned = v / 100m;
                }

        var stub = new PayStubData(paid, begin, end, rate, totals[0], totals[1], totals[2], totals[3], totals[4],
            earningLines, taxLines, preTax, afterTax, userEarned, vacationEarned, ytd.Count >= 5 ? ytd[0] : null, ytd.Count >= 5 ? ytd[4] : null);
        return Validate(stub, grossTotal, taxTotal);
    }

    /// <summary>The arithmetic that must hold on any stub; shown on the review screen before saving.</summary>
    public static ParsedPayStub Validate(PayStubData s, long? grossTotal = null, long? taxTotal = null)
    {
        var checks = new List<string>();
        var problems = new List<string>();
        void Check(bool ok, string good, string bad) { if (ok) checks.Add(good); else problems.Add(bad); }
        string D(long c) => PayEngine.Dollars(c);
        var earned = s.Earnings.Sum(e => e.CurrentCents);
        Check(earned == s.GrossCents, $"Earnings lines add up to gross ({D(s.GrossCents)}).", $"Earnings lines add up to {D(earned)}, but gross is {D(s.GrossCents)}.");
        if (grossTotal is { } g && g != s.GrossCents) problems.Add($"The earnings total says {D(g)}, but gross is {D(s.GrossCents)}.");
        var taxes = s.Taxes.Sum(t => t.CurrentCents);
        Check(taxes == s.TotalTaxesCents, $"Taxes add up to {D(s.TotalTaxesCents)}.", $"Tax lines add up to {D(taxes)}, but total taxes is {D(s.TotalTaxesCents)}.");
        if (taxTotal is { } t && t != s.TotalTaxesCents) problems.Add($"The taxes total says {D(t)}, but total taxes is {D(s.TotalTaxesCents)}.");
        var ded = s.PreTax.Sum(d => d.CurrentCents) + s.AfterTax.Sum(d => d.CurrentCents);
        Check(ded == s.TotalDeductionsCents, $"Deductions add up to {D(s.TotalDeductionsCents)}.", $"Deduction lines add up to {D(ded)}, but total deductions is {D(s.TotalDeductionsCents)}.");
        var net = s.GrossCents - s.TotalTaxesCents - s.TotalDeductionsCents;
        Check(net == s.NetCents, $"Gross − taxes − deductions = net ({D(s.NetCents)}).", $"Gross − taxes − deductions is {D(net)}, but net pay is {D(s.NetCents)}.");
        Check(s.PeriodStart <= s.PeriodEnd && s.PayDate >= s.PeriodEnd, $"Covers {s.PeriodStart:MMM d} to {s.PeriodEnd:MMM d}, paid {s.PayDate:MMM d, yyyy}.", "The pay period dates don't make sense.");
        return new ParsedPayStub(s, checks, problems);
    }

    private static List<List<PageWord>> Rows(IReadOnlyList<PageWord> words)
    {
        var rows = new List<List<PageWord>>();
        foreach (var w in words.OrderByDescending(w => w.Bottom).ThenBy(w => w.Left))
        {
            var row = rows.LastOrDefault();
            if (row is not null && Math.Abs(row[0].Bottom - w.Bottom) <= RowTolerance) row.Add(w);
            else rows.Add([w]);
        }
        return rows.Select(r => r.OrderBy(w => w.Left).ToList()).ToList();
    }

    /// <summary>The index just past a phrase that starts at word <paramref name="i"/>, or null.</summary>
    private static int? Phrase(List<PageWord> row, int i, string phrase)
    {
        var parts = phrase.Split(' ');
        if (i + parts.Length > row.Count) return null;
        for (var k = 0; k < parts.Length; k++)
            if (row[i + k].Text != parts[k]) return null;
        return i + parts.Length;
    }

    /// <summary>The words left of the first number column, joined: the line's description.</summary>
    private static string Label(List<PageWord> words, double numbersStart) =>
        string.Join(' ', words.Where(w => w.Left < numbersStart || Money(w.Text) is null).Select(w => w.Text));

    /// <summary>Numbers assigned to the column whose right edge they line up with.</summary>
    private static Dictionary<string, long> Columns(List<PageWord> words, double numbersStart, (string Key, double Right)[] columns)
    {
        var result = new Dictionary<string, long>();
        foreach (var w in words.Where(w => w.Left >= numbersStart))
        {
            if (Money(w.Text) is not { } v) continue;
            var (key, right) = columns.MinBy(c => Math.Abs(c.Right - w.Right));
            if (Math.Abs(right - w.Right) <= ColumnTolerance) result[key] = v;
        }
        return result;
    }

    /// <summary>"1,234.56" or "-12.00" as cents; null when it isn't an amount with two decimals.</summary>
    public static long? Money(string text)
    {
        var t = text.Replace(",", "").Replace("$", "");
        var neg = t.StartsWith('-') || (t.StartsWith('(') && t.EndsWith(')'));
        t = t.Trim('-', '(', ')');
        var dot = t.IndexOf('.');
        if (dot < 1 || t.Length - dot != 3 || !t.Remove(dot, 1).All(char.IsAsciiDigit)) return null;
        var cents = long.Parse(t.Remove(dot, 1), CultureInfo.InvariantCulture);
        return neg ? -cents : cents;
    }
}
