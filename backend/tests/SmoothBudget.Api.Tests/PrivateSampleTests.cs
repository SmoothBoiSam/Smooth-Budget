using SmoothBudget.Api.Payday;
using SmoothBudget.Core.Pay;
using Xunit.Abstractions;

namespace SmoothBudget.Api.Tests;

/// <summary>
/// Runs only on the owner's machine, against the real samples in the git-ignored <c>private/pay</c> folder
/// (schedule.txt, schedule.csv, stub1.pdf …). No real values are written here: each check compares the files with
/// each other (the CSV estimate with the text export's printed values, the model with each stub's own net pay).
/// </summary>
public sealed class PrivateSamplesFactAttribute : FactAttribute
{
    public PrivateSamplesFactAttribute()
    {
        if (PrivateSampleTests.Folder is null)
            Skip = "Needs the owner's samples in private/pay (git-ignored).";
    }
}

public class PrivateSampleTests(ITestOutputHelper output)
{
    public static string? Folder { get; } = Find();

    private static string? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "private", "pay");
            if (File.Exists(Path.Combine(p, "schedule.txt"))) return p;
        }
        return null;
    }

    private static PayRules Rules => PayRules.Default;

    [PrivateSamplesFact]
    public void The_text_export_obeys_the_credit_rules_and_the_csv_reproduces_its_first_trip()
    {
        var text = ScheduleTextParser.Parse(File.ReadAllText(Path.Combine(Folder!, "schedule.txt")), Rules);
        Assert.Empty(text.Warnings); // every printed day and pairing credit matches the rules
        Assert.Equal(4, text.Pairings.Count);
        Assert.Equal(36, text.Pairings.Sum(p => p.RevenueLegs));
        Assert.Single(text.Entries); // the SKV entry, unpaid by default

        var csv = ScheduleCsvParser.Parse(File.ReadAllText(Path.Combine(Folder!, "schedule.csv")), Rules, ScheduleCsvParser.FindZone);
        var printed = text.Pairings[0];
        var estimated = csv.Pairings[0];
        Assert.Equal(printed.Days.Select(d => (d.Date, d.Report, d.Release, d.DutyMinutes, d.CreditMinutes)),
            estimated.Days.Select(d => (d.Date, d.Report, d.Release, d.DutyMinutes, d.CreditMinutes)));
        Assert.Equal((printed.TafbMinutes, printed.CreditMinutes, printed.RevenueLegs), (estimated.TafbMinutes, estimated.CreditMinutes, estimated.RevenueLegs));
        Assert.Equal(Hm.Pay(printed.TafbMinutes, Rules.PerDiemCentsPerHour), PayEngine.Trip(estimated, false, new(Rules.RateFor(0)), Rules).PerDiemCents);
        foreach (var c in text.Checks.Concat(csv.Checks)) output.WriteLine(c);
    }

    [PrivateSamplesFact]
    public void The_learned_withholding_model_reproduces_every_stub()
    {
        var stubs = Directory.GetFiles(Folder!, "stub*.pdf").Select(f => PayStubParser.Parse(PdfWords.FirstPage(File.ReadAllBytes(f)))).ToList();
        foreach (var s in stubs) Assert.Empty(s.Problems);
        var model = Withholding.Learn(stubs.Select(s => s.Stub).ToList(), Rules.Lines)!;
        foreach (var t in model.Taxes) output.WriteLine($"{t.Name}: {t.Kind} {t.Rate:0.######} × {t.Base} + {t.InterceptCents / 100m} {(t.RoundToDollar ? "(whole dollars)" : "")} {t.Schedule}");
        output.WriteLine($"Untaxed: {string.Join(", ", model.NonTaxable)}; fixed: {string.Join(", ", model.RecurringEarnings.Select(r => $"{r.Name} {r.Schedule}"))}; left out: {string.Join(", ", model.LeftOut)}; PTO {model.PtoHoursPerCheck} h");
        foreach (var b in Withholding.Test(model, stubs.Select(s => s.Stub).ToList()))
        {
            output.WriteLine($"{b.PayDate}: off by {b.ErrorCents} cents (largest tax line {b.TaxErrorCents})");
            Assert.InRange(b.ErrorCents, -1, 1);
            Assert.InRange(b.TaxErrorCents, 0, 1);
        }
    }

    [PrivateSamplesFact]
    public void Forecast_for_the_next_checks()
    {
        var stubs = Directory.GetFiles(Folder!, "stub*.pdf").Select(f => PayStubParser.Parse(PdfWords.FirstPage(File.ReadAllBytes(f))).Stub).ToList();
        var model = Withholding.Learn(stubs, Rules.Lines)!;
        var text = ScheduleTextParser.Parse(File.ReadAllText(Path.Combine(Folder!, "schedule.txt")), Rules);
        var month = new ScheduleMonthInput(text, false, new HashSet<string>(), new HashSet<string>());
        var profile = new PayProfileInput(Rules.RateFor(0));
        foreach (var p in PayCalendar.From(new DateOnly(text.Year, text.Month, 10), Rules).Take(2))
        {
            var f = PayForecast.Check(PayEngine.Gross(p, month, profile, Rules), model, 80, 0);
            output.WriteLine($"{p.PayDate} ({p.Start}–{p.End}): gross {f.GrossCents / 100m}, taxable {f.TaxableCents / 100m}, net {f.NetCents / 100m} ({f.LowNetCents / 100m}–{f.HighNetCents / 100m}) {(f.LowConfidence ? "LOW: " + string.Join(" ", f.Reasons) : "")}");
            foreach (var l in f.Lines) output.WriteLine($"   {l.Name}: {l.Cents / 100m} — {l.Note}");
            Assert.True(f.HasSchedule);
        }
    }
}
