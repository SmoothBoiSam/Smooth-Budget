using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Bills;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Debt;
using SmoothBudget.Core.Pay;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Payday;

public record LineDto(string Name, long Cents, string Note);
public record AmountDto(string Name, long Cents);
/// <param name="Done">Checked off by you, or done according to your accounts (<paramref name="Auto"/>).</param>
/// <param name="Status">For the check you're on now: what the app sees ("Sent $100 of $150 so far.").</param>
/// <param name="Skippable">"Pay off what you charged" can be skipped (DECISIONS.md 195); <paramref name="Skipped"/> when you did.</param>
/// <param name="Parts">For a "send" task: the tasks merged into it, each still checked off (and skipped) on its own
/// (DECISIONS.md 220). Its amount is what's still to send: the parts' total, less any skipped part.</param>
public record PlanItemDto(string Key, string Kind, string Label, long Cents, string Destination, string Why, bool Done,
    bool Auto = false, string? Status = null, IReadOnlyList<PlanBillDto>? Bills = null, long ShortCents = 0,
    bool Skippable = false, bool Skipped = false, IReadOnlyList<PlanItemDto>? Parts = null);
public record PlanBillDto(string Name, long Cents, DateOnly Due);
/// <param name="ShortCents">What the check still lacks for everything planned for it, after what an earlier check keeps for it.</param>
/// <param name="HeldEarlierCents">How much of what this check needs is kept back from the check before it, paid on <paramref name="HeldOn"/> (DECISIONS.md 253).</param>
public record PlanDto(IReadOnlyList<PlanItemDto> Items, long ShortCents, long HeldEarlierCents = 0, DateOnly? HeldOn = null)
{
    /// <summary>Every task as it was before merging: a merged task's parts, and the rest as they are.</summary>
    public IEnumerable<PlanItemDto> AllParts() => Items.SelectMany(i => i.Parts ?? [i]);
}

/// <param name="HasSchedule">False until that month's schedule is imported: no amounts yet.</param>
public record CheckDto(
    DateOnly PayDate, DateOnly PeriodStart, DateOnly PeriodEnd, bool HasSchedule,
    long GrossCents, long TaxableCents, long NetCents, long LowNetCents, long HighNetCents,
    bool LowConfidence, IReadOnlyList<string> Reasons, IReadOnlyList<string> Notes,
    IReadOnlyList<LineDto> Lines, IReadOnlyList<AmountDto> Taxes, IReadOnlyList<AmountDto> Deductions, PlanDto? Plan,
    // The check you're living on now (paid most recently), and what actually landed for it.
    bool IsCurrent = false, long? LandedNetCents = null);

/// <param name="Source">"deposit", "stub" or null (not landed yet).</param>
public record HistoryDto(DateOnly PayDate, long PredictedNetCents, long LowNetCents, long HighNetCents, long? ActualNetCents, long? VarianceCents, string? Source);

/// <param name="YearsOfService">Today: from the date of hire when it's set.</param>
/// <param name="NextRaiseOn">The anniversary on which the rate next goes up, and to what.</param>
public record ProfileDto(int YearsOfService, long RateCents, bool FullTime, bool HolidayPay, int FlatNetPercent,
    DateOnly? HireDate = null, DateOnly? NextRaiseOn = null, long? NextRaiseRateCents = null);

/// <param name="Hours">Whole flight hours to pick up.</param>
/// <param name="TakeHomePerHourCents">What one more hour brings home after taxes and deductions.</param>
public record PickUpDto(int Hours, long RateCents, long TakeHomePerHourCents);

public record PaydayDto(
    ProfileDto Profile, int StubCount, bool ModelLearned, bool ModelEdited, IReadOnlyList<CheckDto> Checks, IReadOnlyList<HistoryDto> History,
    IReadOnlyList<string> ScheduleMonths, IReadOnlyList<UploadDueDto>? UploadsDue = null);

/// <summary>A schedule or pay stub to upload (DECISIONS.md 238): Kind "schedule" (Date = the month's 1st) or "stub" (the pay date).</summary>
public record UploadDueDto(string Kind, string Label, DateOnly Date);

/// <summary>Everything the pay engine needs for one household.</summary>
public sealed record PayContext(
    PayRules Rules, PayProfile Profile, PayProfileInput ProfileInput, Dictionary<(int Year, int Month), ScheduleMonthInput> Months,
    List<(Guid Id, PayStubData Stub)> Stubs, WithholdingModel? LearnedModel, WithholdingModel? Model, DateOnly Today, Household Household, BudgetMonthCalendar Calendar)
{
    public int YearsOn(DateOnly day) => Profile.HireDate is { } hire ? PayRules.YearsOfService(hire, day) : Profile.YearsOfService;

    /// <summary>The profile with the hourly rate in effect on a day (it steps up on each hire anniversary).</summary>
    public PayProfileInput InputOn(DateOnly day) => ProfileInput with { RateCentsPerHour = Rules.RateFor(YearsOn(day)) };
}

/// <summary>
/// The Payday tab (SPEC 4.6, 5): the next checks with a range, the payday plan for each, and predicted vs actual once
/// a deposit lands or a stub arrives. All the math is in SmoothBudget.Core.Pay; this gathers inputs and keeps history.
/// </summary>
public class PaydayService(AppDbContext db, BudgetMonthService months, RecurringService recurring, TimeProvider clock)
{
    /// <summary>A checklist row with this before a plan item's key means you skipped that item for that check.</summary>
    public const string SkipPrefix = "skip:";

    /// <summary>The payday before a check's: where its "Pay off what you charged" window starts.</summary>
    public static DateOnly PaydayBefore(DateOnly pay, PayRules rules) =>
        PayCalendar.From(pay.AddDays(-45), rules).TakeWhile(p => p.PayDate < pay).Select(p => p.PayDate).LastOrDefault(pay.AddDays(-14));

    public const int ChecksShown = 3;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task<PayProfile> ProfileAsync(Guid householdId, CancellationToken ct)
    {
        var p = await db.PayProfiles.SingleOrDefaultAsync(x => x.HouseholdId == householdId, ct);
        if (p is not null) return p;
        p = new PayProfile { Id = Guid.NewGuid(), HouseholdId = householdId, UpdatedUtc = clock.GetUtcNow().UtcDateTime };
        db.PayProfiles.Add(p);
        await db.SaveChangesAsync(ct);
        return p;
    }

    public static PayRules RulesOf(PayProfile p) =>
        p.RulesJson is null ? PayRules.Default : JsonSerializer.Deserialize<PayRules>(p.RulesJson, Json) ?? PayRules.Default;

    public async Task<PayContext> ContextAsync(Guid householdId, CancellationToken ct)
    {
        var profile = await ProfileAsync(householdId, ct);
        var rules = RulesOf(profile);
        var (household, today, calendar) = await months.ContextAsync(householdId, ct);
        var monthsRows = await db.ScheduleMonths.AsNoTracking().Where(m => m.HouseholdId == householdId).ToListAsync(ct);
        var map = monthsRows.ToDictionary(m => (m.Year, m.Month), Input);
        var stubs = (await db.PayStubs.AsNoTracking().Where(s => s.HouseholdId == householdId).OrderBy(s => s.PayDate).ToListAsync(ct))
            .Select(s => (s.Id, JsonSerializer.Deserialize<PayStubData>(s.Json, Json)!)).ToList();
        var learned = Withholding.Learn(stubs.Select(s => s.Item2).ToList(), rules.Lines);
        var edited = profile.ModelJson is null ? null : JsonSerializer.Deserialize<WithholdingModel>(profile.ModelJson, Json);
        var years = profile.HireDate is { } hire ? PayRules.YearsOfService(hire, today) : profile.YearsOfService;
        return new PayContext(rules, profile, new PayProfileInput(rules.RateFor(years), profile.FullTime, profile.HolidayPay),
            map, stubs, learned, edited ?? learned, today, household, calendar);
    }

    public static ScheduleMonthInput Input(ScheduleMonth m) => new(
        JsonSerializer.Deserialize<ParsedSchedule>(m.Json, Json)!, m.Reserve, Keys(m.OvertimeKeys), Keys(m.PaidEntryKeys), Keys(m.DroppedKeys));

    public static HashSet<string> Keys(string stored) => stored.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    /// <summary>Each check uses the rate in effect on the first day it covers.</summary>
    public static ForecastCheck Forecast(PayContext c, PayPeriod p, long typicalMiss)
    {
        var profile = c.InputOn(p.Start);
        return PayForecast.Check(PayEngine.Gross(p, c.Months.GetValueOrDefault((p.Start.Year, p.Start.Month)), profile, c.Rules),
            c.Model, c.Profile.FlatNetPercent, typicalMiss);
    }

    /// <summary>
    /// The take-home a budget month gets from pay (DECISIONS.md 135): every check paid inside it, at what its stub says,
    /// else what landed in checking, else the forecast from your schedule, else a typical recent check. Null when the app
    /// has no pay data at all, so the budget falls back to your median income.
    /// </summary>
    public async Task<(long Cents, string Why)?> IncomeForMonthAsync(Guid householdId, DateOnly start, DateOnly end, CancellationToken ct)
    {
        if (!await db.ScheduleMonths.AnyAsync(m => m.HouseholdId == householdId, ct) && !await db.PayStubs.AnyAsync(s => s.HouseholdId == householdId, ct))
            return null;
        var c = await ContextAsync(householdId, ct);
        var miss = await TypicalMissAsync(householdId, ct);
        var landed = await db.PayPeriodForecasts.AsNoTracking()
            .Where(f => f.HouseholdId == householdId && f.ActualNetCents != null && f.PayDate >= start && f.PayDate <= end)
            .ToDictionaryAsync(f => f.PayDate, f => f.ActualNetCents!.Value, ct);
        var stubs = c.Stubs.Select(s => s.Stub).OrderByDescending(s => s.PayDate).ToList();
        long? typical = stubs.Count > 0 ? BudgetGenerator.Median(stubs.Take(6).Select(s => s.NetCents).ToList()) : null;

        long total = 0;
        var parts = new List<string>();
        var checks = new List<(DateOnly PayDate, long Cents)>();
        foreach (var p in PayCalendar.From(start, c.Rules).TakeWhile(p => p.PayDate <= end))
        {
            long cents;
            string how;
            if (stubs.FirstOrDefault(s => s.PayDate == p.PayDate) is { } stub) (cents, how) = (stub.NetCents, "its pay stub");
            else if (landed.TryGetValue(p.PayDate, out var a)) (cents, how) = (a, "landed");
            else if (Forecast(c, p, miss) is { HasSchedule: true } f) (cents, how) = (f.NetCents, "forecast from your schedule");
            else if (typical is { } t) (cents, how) = (t, "a typical check: no schedule yet");
            else return null;
            total += cents;
            checks.Add((p.PayDate, cents));
            parts.Add($"{p.PayDate:MMM d} {PayEngine.Dollars(cents)} ({how})");
        }
        if (parts.Count == 0) return null;
        var other = await OtherIncomeAsync(householdId, start, end, c, checks, ct);
        return (total + other, $"Your paychecks this budget month: {string.Join(", ", parts)}."
                               + (other > 0 ? $" Plus {PayEngine.Dollars(other)} of other income that has come in." : ""));
    }

    /// <summary>
    /// Income that landed in checking this budget month and isn't a paycheck (a Venmo cash-out, a rebate check): it adds to
    /// the month's money (DECISIONS.md 252). Without it, such a deposit counted as part of your paychecks arriving, so the
    /// app expected less still to come than your next check. A deposit is a paycheck when it's linked to a pay date, comes
    /// from whoever paid a check before (the same name on a deposit that matched a pay stub to the cent), or lands around
    /// one of the month's pay dates for about that check's amount (the rule <see cref="MatchActualsAsync"/> uses).
    /// </summary>
    private async Task<long> OtherIncomeAsync(Guid householdId, DateOnly start, DateOnly end, PayContext c,
        IReadOnlyList<(DateOnly PayDate, long Cents)> checks, CancellationToken ct)
    {
        var to = end < c.Today ? end : c.Today;
        var deposits = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Kind == TransactionKind.Income && t.AmountCents < 0 && !t.IsTransfer && !t.IsPartnerTransfer
                        && t.Account.Type == AccountType.Checking && t.Account.IncludedInBudget && t.Date >= start.AddDays(-200) && t.Date <= to)
            .Select(t => new { t.Id, t.Date, t.AmountCents, t.Merchant }).ToListAsync(ct);
        if (!deposits.Any(d => d.Date >= start)) return 0;
        var linked = (await db.PayPeriodForecasts.AsNoTracking().Where(f => f.HouseholdId == householdId && f.DepositTransactionId != null)
            .Select(f => f.DepositTransactionId!.Value).ToListAsync(ct)).ToHashSet();
        static bool Near(DateOnly deposit, DateOnly payDate) => deposit >= payDate.AddDays(-2) && deposit <= payDate.AddDays(3);
        var payers = deposits.Where(d => linked.Contains(d.Id) || c.Stubs.Any(s => s.Stub.NetCents == -d.AmountCents && Near(d.Date, s.Stub.PayDate)))
            .Select(d => d.Merchant).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return deposits
            .Where(d => d.Date >= start && !linked.Contains(d.Id) && !payers.Contains(d.Merchant)
                        && !checks.Any(p => Near(d.Date, p.PayDate) && Math.Abs(-d.AmountCents - p.Cents) <= Math.Max(p.Cents / 2, 100_00)))
            .Sum(d => -d.AmountCents);
    }

    /// <summary>The next check, for Home (no history writes).</summary>
    public async Task<ForecastCheck?> NextAsync(Guid householdId, CancellationToken ct)
    {
        if (!await db.ScheduleMonths.AnyAsync(m => m.HouseholdId == householdId, ct) && !await db.PayStubs.AnyAsync(s => s.HouseholdId == householdId, ct))
            return null;
        var c = await ContextAsync(householdId, ct);
        var miss = await TypicalMissAsync(householdId, ct);
        return Forecast(c, PayCalendar.From(c.Today, c.Rules).First(), miss);
    }

    /// <summary>
    /// The flight hours to pick up so their take-home covers <paramref name="cents"/> (DECISIONS.md 192): your hourly rate
    /// today times the share of gross your latest pay stub took home (or the flat take-home percent before any stub),
    /// rounded up to whole hours. Null when the app has no pay data.
    /// </summary>
    public async Task<PickUpDto?> PickUpAsync(Guid householdId, long cents, CancellationToken ct)
    {
        if (cents <= 0) return null;
        if (!await db.ScheduleMonths.AnyAsync(m => m.HouseholdId == householdId, ct) && !await db.PayStubs.AnyAsync(s => s.HouseholdId == householdId, ct))
            return null;
        var c = await ContextAsync(householdId, ct);
        var rate = c.ProfileInput.RateCentsPerHour;
        var stub = c.Stubs.Select(s => s.Stub).Where(s => s.GrossCents > 0 && s.NetCents > 0).MaxBy(s => s.PayDate);
        var share = stub is not null ? (decimal)stub.NetCents / stub.GrossCents : c.Profile.FlatNetPercent / 100m;
        var perHour = (long)Math.Round(rate * share, MidpointRounding.AwayFromZero);
        if (perHour <= 0) return null;
        return new PickUpDto((int)Math.Ceiling((decimal)cents / perHour), rate, perHour);
    }

    /// <summary>One request per household keeps the predictions at a time (the app runs as a single container).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    public async Task<PaydayDto> GetAsync(Guid householdId, CancellationToken ct)
    {
        var c = await ContextAsync(householdId, ct);
        // Home, the tab badge and Tasks all ask for this, often at the same moment. Matching deposits and keeping the
        // predictions write rows, and two requests writing the same ones deadlocked in the database (DECISIONS.md 251):
        // so one at a time per household, and a row is only written when its numbers changed.
        List<ForecastCheck> forecasts;
        List<PayPeriodForecast> rows;
        var gate = Gates.GetOrAdd(householdId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await MatchActualsAsync(householdId, c, ct);
            var miss = await TypicalMissAsync(householdId, ct);
            // The check you're living on now (the latest one paid) and the next few.
            var current = PayCalendar.From(c.Today.AddDays(-45), c.Rules).TakeWhile(p => p.PayDate <= c.Today).LastOrDefault();
            var periods = PayCalendar.From(c.Today.AddDays(1), c.Rules).Take(ChecksShown).ToList();
            if (current is not null) periods.Insert(0, current);
            forecasts = periods.Select(p => Forecast(c, p, miss)).ToList();

            // Keep each prediction as it stands until payday; after that it's frozen for predicted vs actual.
            rows = await db.PayPeriodForecasts.Where(f => f.HouseholdId == householdId).ToListAsync(ct);
            foreach (var f in forecasts.Where(f => f.HasSchedule && f.Period.PayDate >= c.Today))
            {
                var row = rows.FirstOrDefault(r => r.PayDate == f.Period.PayDate);
                if (row is null)
                {
                    row = new PayPeriodForecast { Id = Guid.NewGuid(), HouseholdId = householdId, PayDate = f.Period.PayDate };
                    db.PayPeriodForecasts.Add(row);
                    rows.Add(row);
                }
                var now = (f.Period.Start, f.Period.End, f.GrossCents, f.NetCents, f.LowNetCents, f.HighNetCents);
                if ((row.PeriodStart, row.PeriodEnd, row.PredictedGrossCents, row.PredictedNetCents, row.LowNetCents, row.HighNetCents) == now) continue;
                (row.PeriodStart, row.PeriodEnd, row.PredictedGrossCents, row.PredictedNetCents, row.LowNetCents, row.HighNetCents) = now;
                row.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
            }
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            gate.Release();
        }

        var landed = rows.Where(r => r.ActualNetCents is not null).ToDictionary(r => r.PayDate, r => r.ActualNetCents!.Value);
        foreach (var (_, stub) in c.Stubs) landed[stub.PayDate] = stub.NetCents;
        var plans = await PlansAsync(householdId, c, forecasts, landed, ct);
        var checks = forecasts.Select((f, i) => new CheckDto(
            f.Period.PayDate, f.Period.Start, f.Period.End, f.HasSchedule, f.GrossCents, f.TaxableCents, f.NetCents, f.LowNetCents, f.HighNetCents,
            f.LowConfidence, f.Reasons, f.Notes, f.Lines.Select(l => new LineDto(l.Name, l.Cents, l.Note)).ToList(),
            f.Taxes.Select(t => new AmountDto(t.Name, t.CurrentCents)).ToList(), f.Deductions.Select(d => new AmountDto(d.Name, d.CurrentCents)).ToList(),
            plans[i], f.Period.PayDate <= c.Today, landed.TryGetValue(f.Period.PayDate, out var got) ? got : null)).ToList();

        var history = rows.Where(r => r.PayDate < c.Today || r.ActualNetCents is not null).OrderByDescending(r => r.PayDate).Take(12)
            .Select(r => new HistoryDto(r.PayDate, r.PredictedNetCents, r.LowNetCents, r.HighNetCents, r.ActualNetCents,
                r.ActualNetCents - r.PredictedNetCents, r.ActualSource)).ToList();

        return new PaydayDto(ProfileOf(c), c.Stubs.Count, c.LearnedModel is not null, c.Profile.ModelJson is not null, checks, history,
            c.Months.Keys.OrderByDescending(k => k).Select(k => $"{k.Year}-{k.Month:00}").ToList(),
            UploadsDue(c.Today, c.Rules, c.Months.Keys.ToList(), c.Stubs.Select(s => s.Item2.PayDate).ToList()));
    }

    /// <summary>The day of the month from which next month's schedule is expected to be out (DECISIONS.md 238).</summary>
    public const int NextScheduleFromDay = 20;

    /// <summary>
    /// What to upload now (DECISIONS.md 238), once you've uploaded any schedule or stub: this month's schedule if it's
    /// missing, next month's from the 20th, and the stub for each check paid in the last 5 weeks that has none (a stub
    /// dated within 3 days of the pay date counts, for holidays and weekends). Nothing from before your first upload.
    /// </summary>
    public static IReadOnlyList<UploadDueDto> UploadsDue(DateOnly today, PayRules rules, IReadOnlyCollection<(int Year, int Month)> schedules, IReadOnlyCollection<DateOnly> stubDates)
    {
        if (schedules.Count == 0 && stubDates.Count == 0) return [];
        var due = new List<UploadDueDto>();
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var months = new List<DateOnly> { thisMonth };
        if (today.Day >= NextScheduleFromDay) months.Add(thisMonth.AddMonths(1));
        foreach (var m in months.Where(m => !schedules.Contains((m.Year, m.Month))))
            due.Add(new UploadDueDto("schedule", $"{m.ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture)} schedule", m));

        var firstUpload = new[] { stubDates.Count > 0 ? stubDates.Min() : DateOnly.MaxValue, schedules.Count > 0 ? schedules.Min(s => new DateOnly(s.Year, s.Month, 1)) : DateOnly.MaxValue }.Min();
        var from = today.AddDays(-35) > firstUpload ? today.AddDays(-35) : firstUpload;
        foreach (var p in PayCalendar.From(from, rules).TakeWhile(p => p.PayDate <= today))
            if (!stubDates.Any(d => Math.Abs(d.DayNumber - p.PayDate.DayNumber) <= 3))
                due.Add(new UploadDueDto("stub", $"Pay stub for the check paid {p.PayDate.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture)}", p.PayDate));
        return due;
    }

    public static ProfileDto ProfileOf(PayContext c)
    {
        var raise = c.Profile.HireDate is { } hire ? c.Rules.NextRaise(hire, c.Today) : null;
        return new(c.YearsOn(c.Today), c.ProfileInput.RateCentsPerHour, c.Profile.FullTime, c.Profile.HolidayPay, c.Profile.FlatNetPercent,
            c.Profile.HireDate, raise?.On, raise?.CentsPerHour);
    }

    public async Task<long> TypicalMissAsync(Guid householdId, CancellationToken ct) =>
        PayForecast.TypicalMiss((await db.PayPeriodForecasts.AsNoTracking()
            .Where(f => f.HouseholdId == householdId && f.ActualNetCents != null).OrderByDescending(f => f.PayDate)
            .Select(f => new { f.PredictedNetCents, f.ActualNetCents }).ToListAsync(ct)).Select(f => (f.PredictedNetCents, f.ActualNetCents!.Value)));

    /// <summary>
    /// Predicted vs actual (SPEC 5.5): a stub for that pay date wins; otherwise a paycheck deposit into checking within a
    /// few days of payday, preferring the payer of earlier matched paychecks, then the amount closest to the prediction.
    /// </summary>
    public async Task MatchActualsAsync(Guid householdId, PayContext c, CancellationToken ct)
    {
        var open = await db.PayPeriodForecasts.Where(f => f.HouseholdId == householdId && f.PayDate <= c.Today.AddDays(3) && f.ActualSource != "stub").ToListAsync(ct);
        if (open.Count == 0) return;
        var stubByDate = c.Stubs.GroupBy(s => s.Stub.PayDate).ToDictionary(g => g.Key, g => g.First().Stub);
        var linked = await db.PayPeriodForecasts.Where(f => f.HouseholdId == householdId && f.DepositTransactionId != null).Select(f => f.DepositTransactionId!.Value).ToListAsync(ct);
        var payers = await db.Transactions.AsNoTracking().Where(t => linked.Contains(t.Id)).Select(t => t.Merchant).Distinct().ToListAsync(ct);
        var from = open.Min(f => f.PayDate).AddDays(-2);
        var to = open.Max(f => f.PayDate).AddDays(3);
        var deposits = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Kind == TransactionKind.Income && t.AmountCents < 0 && !t.IsTransfer && !t.IsPartnerTransfer
                        && t.Account.Type == AccountType.Checking && t.Date >= from && t.Date <= to)
            .Select(t => new { t.Id, t.Date, t.AmountCents, t.Merchant }).ToListAsync(ct);

        foreach (var f in open)
        {
            if (stubByDate.TryGetValue(f.PayDate, out var stub))
            {
                (f.ActualGrossCents, f.ActualNetCents, f.ActualSource) = (stub.GrossCents, stub.NetCents, "stub");
                continue;
            }
            if (f.DepositTransactionId is not null) continue;
            var match = deposits
                .Where(d => d.Date >= f.PayDate.AddDays(-2) && d.Date <= f.PayDate.AddDays(3) && !linked.Contains(d.Id)
                            && Math.Abs(-d.AmountCents - f.PredictedNetCents) <= Math.Max(f.PredictedNetCents / 2, 100_00))
                .OrderBy(d => payers.Contains(d.Merchant) ? 0 : 1).ThenBy(d => Math.Abs(-d.AmountCents - f.PredictedNetCents)).FirstOrDefault();
            if (match is null) continue;
            (f.ActualNetCents, f.ActualSource, f.DepositTransactionId) = (-match.AmountCents, "deposit", match.Id);
            linked.Add(match.Id);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The payday plan for each check (SPEC 4.6, DECISIONS.md 138). For the check you're living on now, each part shows
    /// whether it's done, worked out from your accounts: the "keep" line from the account's balance against the bills
    /// still to come, the card payment and savings from the transfers seen since payday, and the sweep from whether it's
    /// marked sent. You can still check anything off yourself.
    /// </summary>
    private async Task<List<PlanDto?>> PlansAsync(Guid householdId, PayContext c, List<ForecastCheck> checks, IReadOnlyDictionary<DateOnly, long> landed, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.HouseholdId == householdId && a.IncludedInBudget).ToListAsync(ct);
        string Label(Account a) => a.Mask is null ? a.Name : $"{a.Name} ••{a.Mask}";
        var savingsAccount = accounts.Where(a => a.Type == AccountType.Savings).OrderBy(a => a.CreatedUtc).Select(Label).FirstOrDefault() ?? "savings";
        var cards = accounts.Where(a => a.Type == AccountType.CreditCard).SelectMany(a => new[] { a.Name, Label(a) }).ToHashSet();
        var categories = await db.Categories.AsNoTracking().Where(x => x.HouseholdId == householdId && !x.IsHidden).ToListAsync(ct);
        var debts = await db.DebtAccounts.AsNoTracking().Include(d => d.Account).Where(d => d.HouseholdId == householdId && !d.IsHidden && d.BalanceCents > 0).ToListAsync(ct);
        // The card payment is the debt-payment line plus the Card interest bill: the interest is charged on the cards and the
        // minimums include it, so the budget keeps it on its own line rather than counting it twice (DECISIONS.md 172).
        // A "this month only" limit applies to the checks paid in this budget month (DECISIONS.md 251); later ones use the usual.
        var thisMonth = c.Calendar.MonthContaining(c.Today).Start;
        var monthOnly = await BudgetMonthService.MonthOnlyLimitsAsync(db, householdId, thisMonth, ct);
        long Limit(Category x, DateOnly monthStart) => monthStart == thisMonth && monthOnly.TryGetValue(x.Id, out var o) ? o : x.MonthlyLimitCents;
        long DebtMonthly(DateOnly monthStart) => categories.Where(x => x.Type == CategoryType.DebtPayment).Sum(x => Limit(x, monthStart))
                          + (debts.Count > 0 ? categories.Where(x => CardInterest.IsInterestCategory(x.Name, x.Type)).Sum(x => Limit(x, monthStart)) : 0);
        long SavingsMonthly(DateOnly monthStart) => categories.Where(x => x.Type == CategoryType.SavingsTransfer).Sum(x => Limit(x, monthStart));
        var savingsCategories = categories.Where(x => x.Type == CategoryType.SavingsTransfer).Select(x => x.Id).ToHashSet();
        var snowball = c.Household.DebtStrategy == DebtStrategy.Snowball;
        var targetDebt = (snowball
            ? debts.OrderBy(d => d.BalanceCents)
            : debts.OrderByDescending(d => d.AprBps ?? 0).ThenByDescending(d => d.BalanceCents)).FirstOrDefault();
        string DebtLabel(DebtAccount d) => d.Account is { } a ? Label(a) : d.Name;
        var target = targetDebt is null ? "your card" : DebtLabel(targetDebt);
        // With minimums known, each card gets its own line: its minimum, plus the extra on the target (DECISIONS.md 155).
        var withMinimums = debts.Where(d => d.MinimumPaymentCents > 0 || d == targetDebt).OrderByDescending(d => d == targetDebt).ThenBy(d => d.Name).ToList();
        var items = (await recurring.SummaryAsync(householdId, ct)).Items
            .Where(i => i.Status is not ("possiblyCanceled" or "canceled") && !cards.Contains(i.AccountName)).ToList();
        var sweeps = await db.SweepTasks.AsNoTracking().Where(s => s.HouseholdId == householdId)
            .OrderByDescending(s => s.BudgetMonth.StartDate).ToListAsync(ct);
        var arrangements = await db.PartnerArrangements.AsNoTracking().Where(a => a.HouseholdId == householdId).Include(a => a.Friend).ToListAsync(ct);
        var allSenders = arrangements.Select(a => a.Friend.Name).Distinct().ToList();
        var everyoneSends = $"{Shared.SharedService.JoinNames(allSenders) ?? "your friend"} {(allSenders.Count > 1 ? "send" : "sends")}";
        var done = await db.PaydayChecklistItems.AsNoTracking().Where(x => x.HouseholdId == householdId).Select(x => new { x.PayDate, x.Key, x.CreatedUtc }).ToListAsync(ct);
        // What you put on each card lately (purchases less refunds, not payments): it already counts in its spending or bill
        // category, so the plan pays it off from the spending money left in checking, or the card would grow (DECISIONS.md 179).
        var cardAccounts = accounts.Where(a => a.Type == AccountType.CreditCard).ToList();
        var cardIds = cardAccounts.Select(a => a.Id).ToList();
        var since = c.Today.AddDays(-62);
        var charged = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && cardIds.Contains(t.AccountId) && t.Date >= since && !t.IsTransfer && t.Kind != TransactionKind.CardPayment)
            .Select(t => new { t.AccountId, t.Date, t.AmountCents }).ToListAsync(ct);
        DateOnly PaydayBefore(DateOnly pay) => PaydayService.PaydayBefore(pay, c.Rules);
        // A friend's fixed contribution that has already arrived is sitting in the account, waiting for its bill
        // (DECISIONS.md 253): by budget month, what each contribution has received.
        var arrivedByMonth = new Dictionary<DateOnly, Dictionary<Guid, long>>();
        if (arrangements.Count > 0)
            foreach (var day in checks.SelectMany(x => new[] { x.Period.PayDate, PayCalendar.PeriodContaining(x.Period.End.AddDays(1), c.Rules).PayDate.AddDays(-1) }).Append(c.Today))
            {
                var m = c.Calendar.MonthContaining(day);
                if (!arrivedByMonth.ContainsKey(m.Start))
                    arrivedByMonth[m.Start] = (await months.ContributionsAsync(householdId, m, ct)).ToDictionary(s => s.ArrangementId, s => s.ReceivedCents);
            }
        long Arrived(Guid arrangementId, DateOnly due) =>
            arrivedByMonth.TryGetValue(c.Calendar.MonthContaining(due).Start, out var got) && got.TryGetValue(arrangementId, out var cents) ? cents : 0;

        // One check's plan before anything is carried between checks. It reads nothing from the database, so the loop
        // below can ask for the checks either side of the one it's on (DECISIONS.md 253).
        (PaydayPlanResult Plan, long NetCents, bool Current, DateOnly Pay, DateOnly Next)? Draft(int i)
        {
            var f = checks[i];
            var pay = f.Period.PayDate;
            var landedNet = landed.TryGetValue(pay, out var l) ? l : (long?)null;
            if (!f.HasSchedule && landedNet is null) return null;
            var current = pay <= c.Today;
            var next = PayCalendar.PeriodContaining(f.Period.End.AddDays(1), c.Rules).PayDate;
            var month = c.Calendar.MonthContaining(pay);
            var inMonth = PayCalendar.From(month.Start, c.Rules).TakeWhile(p => p.PayDate <= month.End).Select(p => p.PayDate).ToList();
            var index = Math.Max(0, inMonth.IndexOf(pay));
            var share = $"{(inMonth.Count == 2 ? "Half" : $"1/{inMonth.Count}")} of the month's";
            var (debtMonthly, savingsMonthly) = (DebtMonthly(month.Start), SavingsMonthly(month.Start));

            // Bills still to come before the next payday (for the current check: from today, since paid ones are done).
            // A bill your partner's fixed contribution goes toward (rent) still needs all of it in the account when it's
            // charged, since their money lands there too (DECISIONS.md 165); only your share comes out of this check.
            var from = current ? c.Today : pay;
            var offsetUsed = new HashSet<Guid>();
            var bills = items.Where(b => b.NextExpected >= from && b.NextExpected < next).OrderBy(b => b.NextExpected).Select(b =>
            {
                var toward = b.CategoryId is { } cat && offsetUsed.Add(cat)
                    ? arrangements.Where(a => a.CategoryId == cat && a.StartsOn <= b.NextExpected).ToList() : [];
                var covered = Math.Min(b.AmountCents, toward.Sum(a => a.AmountCents));
                // Whoever sends toward it (DECISIONS.md 211): "Jane sends", "Jane and Alex send".
                var senders = toward.Select(a => a.Friend.Name).Distinct().ToList();
                var who = $"{Shared.SharedService.JoinNames(senders) ?? "Your friend"} {(senders.Count > 1 ? "send" : "sends")}";
                return new PlanBill($"bill:{b.Key}", b.Name, b.AmountCents, b.NextExpected, b.AccountName,
                    covered > 0 ? $"{b.Name} counts in full: {who} {PayEngine.Dollars(covered)} of it to this account, so {PayEngine.Dollars(b.AmountCents - covered)} is yours from this check." : null,
                    covered, Math.Min(covered, toward.Sum(a => Math.Min(a.AmountCents, Arrived(a.Id, b.NextExpected)))));
            }).ToList();
            var debtShare = PaydayPlan.Share(debtMonthly, inMonth.Count, index);
            var transfers = new List<PlanTransfer>();
            if (withMinimums.Count > 1)
            {
                var split = PaydayPlan.SplitCardPayment(debtShare,
                    withMinimums.Select(d => new CardMinimum($"debt:{d.Id}", DebtLabel(d),
                        PaydayPlan.Share(Math.Min(d.MinimumPaymentCents ?? 0, d.BalanceCents), inMonth.Count, index))).ToList(),
                    $"debt:{targetDebt!.Id}");
                var why = snowball ? "smallest balance" : "highest interest";
                var checkShare = inMonth.Count > 1 ? " (this check's share)" : "";
                transfers.AddRange(split.Select(s => s.Card.Key == $"debt:{targetDebt.Id}"
                    ? new PlanTransfer(s.Card.Key, s.ExtraCents > 0 ? "Minimum + extra" : "Minimum payment", s.Cents, s.Card.Destination,
                        s.ExtraCents > 0
                            ? $"The {PayEngine.Dollars(s.Card.MinimumCents)} minimum{checkShare}, plus {PayEngine.Dollars(s.ExtraCents)} extra ({why})."
                            : $"The minimum{checkShare}. Extra goes here first ({why}).")
                    : new PlanTransfer(s.Card.Key, "Minimum payment", s.Cents, s.Card.Destination,
                        $"Just the minimum{checkShare}. Extra goes to {target} first.")));
            }
            else
                transfers.Add(new("debt", "Card payment", debtShare, target,
                    inMonth.Count > 1 ? $"{share} {PayEngine.Dollars(debtMonthly)} card payment in your budget." : $"Your budget's {PayEngine.Dollars(debtMonthly)} card payment."));

            // The check you're on pays off what you charged between the last payday and this one. The next check shows
            // what you've charged since this payday so far. It comes from the spending money still in checking (you spent
            // it on the card instead), so it isn't taken out of the check itself.
            var nextUp = !current && (i == 0 || checks[i - 1].Period.PayDate <= c.Today);
            if (current || nextUp)
            {
                var (spendFrom, spendTo) = current ? (PaydayBefore(pay), pay.AddDays(-1)) : (PaydayBefore(pay), c.Today);
                foreach (var card in cardAccounts)
                {
                    var cents = charged.Where(t => t.AccountId == card.Id && t.Date >= spendFrom && t.Date <= spendTo).Sum(t => t.AmountCents);
                    if (cents <= 0) continue;
                    transfers.Add(new PlanTransfer($"cardspend:{card.Id}", current ? "Pay off what you charged" : "Charged so far", cents, Label(card),
                        (current
                            ? $"What you put on this card from {spendFrom:MMM d} to {spendTo:MMM d}."
                            : $"What you've put on this card since {spendFrom:MMM d}; it'll be more by payday.")
                        + " Already in your spending, so pay it from checking.",
                        FromEarlierCents: cents));
                }
            }
            if (i == 0)
                transfers.AddRange(sweeps.Where(s => s.DoneUtc == null || (current && s.DoneUtc >= pay.ToDateTime(TimeOnly.MinValue)))
                    .Select(s => new PlanTransfer($"sweep:{s.Id}", "Last month's leftover", s.AmountCents, s.Destination, "Left over from last month.")));
            transfers.Add(new("savings", "Savings", PaydayPlan.Share(savingsMonthly, inMonth.Count, index), savingsAccount,
                inMonth.Count > 1 ? $"{share} {PayEngine.Dollars(savingsMonthly)} savings in your budget." : $"Your budget's {PayEngine.Dollars(savingsMonthly)} savings."));

            var net = landedNet ?? f.NetCents;
            return (PaydayPlan.Build(net, bills, transfers), net, current, pay, next);
        }
        // What a check has to pay from its own money (not a friend's part of a bill, or card charges paid from spending money).
        static long Own(PaydayPlanResult p) => p.Items.Sum(x => x.Cents - x.FromOthersCents);
        var firstChecking = accounts.Where(a => a.Type == AccountType.Checking).OrderBy(a => a.CreatedUtc).Select(Label).FirstOrDefault() ?? "checking";

        var result = new List<PlanDto?>();
        for (var i = 0; i < checks.Count; i++)
        {
            if (Draft(i) is not { } draft) { result.Add(null); continue; }
            var (plan, net, current, pay, next) = draft;

            // Money carried between checks (DECISIONS.md 253). When the next check can't pay for everything planned for it
            // (a small check with rent due right after it), this one keeps the difference in the account, as far as it has
            // room after its own plan; and a check an earlier one keeps money for is short by that much less.
            var after = i + 1 < checks.Count ? Draft(i + 1) : null;
            var before = i > 0 ? Draft(i - 1) : null;
            var hold = after is { } later ? Math.Min(later.Plan.ShortCents, Math.Max(0, net - Own(plan))) : 0;
            var heldEarlier = before is { } earlier ? Math.Min(plan.ShortCents, Math.Max(0, earlier.NetCents - Own(earlier.Plan))) : 0;
            if (hold > 0 && after is { } nextCheck)
            {
                var into = nextCheck.Plan.Items.FirstOrDefault(x => x.Kind == "keep")?.Destination
                           ?? plan.Items.FirstOrDefault(x => x.Kind == "keep")?.Destination ?? firstChecking;
                var withHold = plan.Items.ToList();
                withHold.Insert(withHold.FindLastIndex(x => x.Kind == "keep") + 1, new PlanItem($"hold:{nextCheck.Pay:yyyy-MM-dd}", "hold", $"bills after {nextCheck.Pay:MMM d}", hold, into,
                    $"Your {nextCheck.Pay:MMM d} check is {(landed.ContainsKey(nextCheck.Pay) ? "" : "about ")}{PayEngine.Dollars(nextCheck.NetCents)}, and {PayEngine.Dollars(Own(nextCheck.Plan))} has to come out of it for its bills, card payments and savings. Leave {PayEngine.Dollars(hold)} of this check in the account to cover the rest."
                    + (hold < nextCheck.Plan.ShortCents ? $" That check is still {PayEngine.Dollars(nextCheck.Plan.ShortCents - hold)} short after this." : "")));
                plan = plan with { Items = withHold };
            }

            // What happened since payday, for the check you're on now.
            var moves = current
                ? await db.Transactions.AsNoTracking()
                    .Where(t => t.HouseholdId == householdId && t.Date >= pay && t.Date < next && (t.Kind == TransactionKind.CardPayment || t.IsTransfer))
                    .Select(t => new { t.AmountCents, t.Kind, Type = t.Account.Type, t.CategoryId, t.AccountId }).ToListAsync(ct)
                : [];
            var cardPaid = moves.Where(m => m.Kind == TransactionKind.CardPayment && m.Type == AccountType.Checking && m.AmountCents > 0).Sum(m => m.AmountCents);
            // Per card (DECISIONS.md 244): the payment as the card's own account shows it, or as it left checking when the card
            // isn't synced (or its statement isn't uploaded yet). With none tied to a card, the checking side for all cards.
            var toCards = current ? await Debt.CardPayments.LoadAsync(db, householdId, pay, next.AddDays(-1), ct) : [];
            long PaidTo(Guid? accountId) => toCards.Where(p => p.CardAccountId == accountId).Sum(p => p.Cents);
            var cardSideSeen = toCards.Count > 0;
            var allDebtLines = plan.Items.Where(x => x.Kind == "debt").Sum(x => x.Cents);
            var intoSavings = accounts.Any(a => a.Type == AccountType.Savings)
                ? moves.Where(m => m.Type == AccountType.Savings && m.AmountCents < 0).Sum(m => -m.AmountCents)
                : moves.Where(m => m.Type == AccountType.Checking && m.AmountCents > 0 && m.CategoryId is { } id && savingsCategories.Contains(id)).Sum(m => m.AmountCents);

            Account? AccountFor(PlanItem x) => accounts.FirstOrDefault(a => Label(a) == x.Destination || a.Name == x.Destination);
            // How far the account's balance from the bank falls short of a "keep" line, for the check you're on now.
            long ShortBy(PlanItem x) =>
                current && x.Kind == "keep" && AccountFor(x) is { PlaidAccountId: not null } a && a.CurrentBalanceCents < x.Cents ? x.Cents - a.CurrentBalanceCents : 0;

            (bool Auto, string? Status) Status(PlanItem x)
            {
                if (!current) return (false, null);
                switch (x.Kind)
                {
                    case "keep":
                        var acct = AccountFor(x);
                        if (acct?.PlaidAccountId is null) return (false, "Check this balance yourself; it isn't bank-synced.");
                        return acct.CurrentBalanceCents >= x.Cents
                            ? (true, $"{acct.Name} has {PayEngine.Dollars(acct.CurrentBalanceCents)}, enough for these.")
                            : (false, $"{acct.Name} has {PayEngine.Dollars(acct.CurrentBalanceCents)}: {PayEngine.Dollars(x.Cents - acct.CurrentBalanceCents)} short for these."
                                      + (x.FromOthersCents > 0 ? $" That counts the {PayEngine.Dollars(x.FromOthersCents)} {everyoneSends}, if it hasn't arrived yet." : ""));
                    case "debt" when x.Key.StartsWith("debt:", StringComparison.Ordinal):
                        if (x.Cents <= 0) return (true, null);
                        var card = debts.FirstOrDefault(d => x.Key == $"debt:{d.Id}");
                        if (cardSideSeen && card?.AccountId is { } cardAccount)
                        {
                            var paid = PaidTo(cardAccount);
                            return paid >= x.Cents ? (true, $"Paid {PayEngine.Dollars(paid)} since payday.")
                                : (false, paid > 0 ? $"Paid {PayEngine.Dollars(paid)} of {PayEngine.Dollars(x.Cents)} so far." : "No payment to this card seen since payday yet.");
                        }
                        return cardPaid >= allDebtLines ? (true, $"Sent {PayEngine.Dollars(cardPaid)} to your cards since payday.")
                            : (false, cardPaid > 0 ? $"Sent {PayEngine.Dollars(cardPaid)} to your cards so far, of {PayEngine.Dollars(allDebtLines)} in all." : "No card payment seen since payday yet.");
                    case "debt":
                        return x.Cents <= 0 ? (true, null)
                            : cardPaid >= x.Cents ? (true, $"Sent {PayEngine.Dollars(cardPaid)} since payday.")
                            : (false, cardPaid > 0 ? $"Sent {PayEngine.Dollars(cardPaid)} of {PayEngine.Dollars(x.Cents)} so far." : "No card payment seen since payday yet.");
                    case "cardspend":
                    {
                        // Done once the card got this plus its payment line (everything the plan sends to it).
                        var owed = plan.Items.Where(y => (y.Kind is "debt" or "cardspend") && y.Destination == x.Destination).Sum(y => y.Cents);
                        if (cardSideSeen && Guid.TryParse(x.Key["cardspend:".Length..], out var spentOn))
                        {
                            var paid = PaidTo(spentOn);
                            return paid >= owed ? (true, $"Paid {PayEngine.Dollars(paid)} to this card since payday.")
                                : (false, paid > 0 ? $"Paid {PayEngine.Dollars(paid)} of {PayEngine.Dollars(owed)} to this card so far, counting its payment above." : null);
                        }
                        var allCards = plan.Items.Where(y => y.Kind is "debt" or "cardspend").Sum(y => y.Cents);
                        return cardPaid >= allCards ? (true, $"Sent {PayEngine.Dollars(cardPaid)} to your cards since payday.") : (false, null);
                    }
                    case "savings":
                        return x.Cents <= 0 ? (true, null)
                            : intoSavings >= x.Cents ? (true, $"Moved {PayEngine.Dollars(intoSavings)} to savings since payday.")
                            : (false, intoSavings > 0 ? $"Moved {PayEngine.Dollars(intoSavings)} of {PayEngine.Dollars(x.Cents)} so far." : "No transfer to savings seen since payday yet.");
                    case "hold":
                    {
                        // Done when the account covers this on top of the bills it's already keeping, and any friend's
                        // money that has arrived early for the later bills (it's in the balance, and spoken for).
                        var holds = AccountFor(x);
                        if (holds?.PlaidAccountId is null) return (false, "Check this balance yourself; it isn't bank-synced.");
                        var billsHere = plan.Items.Where(y => y.Kind == "keep" && y.Destination == x.Destination).Sum(y => y.Cents);
                        var arrivedForLater = after is { } an
                            ? an.Plan.Items.Where(y => y.Kind == "keep" && y.Destination == x.Destination).SelectMany(y => y.Bills ?? []).Sum(b => b.ArrivedCents) : 0;
                        var need = billsHere + x.Cents + arrivedForLater;
                        var also = (billsHere > 0 ? " and the bills above" : "")
                                   + (arrivedForLater > 0 ? $", with the {PayEngine.Dollars(arrivedForLater)} already sent toward the later bills" : "");
                        return holds.CurrentBalanceCents >= need
                            ? (true, $"{holds.Name} has {PayEngine.Dollars(holds.CurrentBalanceCents)}, enough for this{also}.")
                            : (false, $"{holds.Name} has {PayEngine.Dollars(holds.CurrentBalanceCents)}: {PayEngine.Dollars(need - holds.CurrentBalanceCents)} short of this{also}.");
                    }
                    case "sweep":
                        var task = sweeps.FirstOrDefault(s => x.Key == $"sweep:{s.Id}");
                        return task?.DoneUtc is not null ? (true, "Marked as sent.") : (false, null);
                    default:
                        return (false, null);
                }
            }

            var tasks = plan.Items.Select(x =>
            {
                var (auto, status) = Status(x);
                // A check's tasks open on its payday (DECISIONS.md 221): one ticked off before then (back when that was
                // allowed) doesn't count, so it isn't shown as done on payday without you having done it.
                var manual = current && done.Any(d => d.PayDate == pay && d.Key == x.Key && DateOnly.FromDateTime(d.CreatedUtc) >= pay.AddDays(-1));
                // Skipping "Pay off what you charged" leaves those charges on the card as debt (DECISIONS.md 195).
                var skippable = x.Kind == "cardspend" && current;
                var skipped = skippable && !auto && done.Any(d => d.PayDate == pay && d.Key == SkipPrefix + x.Key);
                return new PlanItemDto(x.Key, x.Kind, x.Label, x.Cents, x.Destination, x.Why, !skipped && (manual || auto), auto, skipped ? "Skipped: these charges stay on the card." : status,
                    x.Bills?.Select(b => new PlanBillDto(b.Name, b.Cents, b.Due)).ToList(), ShortBy(x), skippable, skipped);
            }).ToList();

            var shortCents = plan.ShortCents - heldEarlier;
            // The check you're on has landed, so what matters is whether the accounts cover what's still to go out
            // (DECISIONS.md 253): the bills being kept (less a friend's part that hasn't arrived yet) and the transfers
            // not made yet. Money kept from the check before is in that balance. Never more than the plan says.
            if (current && shortCents > 0)
            {
                var keeps = plan.Items.Where(x => x.Kind == "keep").ToList();
                var paidFrom = (keeps.Count > 0 ? keeps.Select(AccountFor) : accounts.Where(a => a.Type == AccountType.Checking)).Distinct().ToList();
                if (paidFrom.Count > 0 && paidFrom.All(a => a?.PlaidAccountId is not null))
                {
                    var stillOut = keeps.Sum(x => x.Cents - (x.FromOthersCents - (x.Bills ?? []).Sum(b => b.ArrivedCents)))
                                   + tasks.Where(t => t.Kind is "debt" or "savings" or "sweep" && !t.Done).Sum(t => t.Cents);
                    shortCents = Math.Min(shortCents, Math.Max(0, stillOut - paidFrom.Sum(a => a!.CurrentBalanceCents)));
                }
            }
            result.Add(new PlanDto(MergeSends(tasks), shortCents, heldEarlier, heldEarlier > 0 ? before!.Value.Pay : null));
        }
        return result;
    }

    /// <summary>
    /// Tasks that send money to the same account become one "send" task, with the others as its parts (DECISIONS.md 220):
    /// a card's payment and paying off what you charged to it, or savings and last month's leftover. It sits where its
    /// first part was. "Keep" and "hold" tasks leave money where it is, so they're never merged.
    /// </summary>
    public static IReadOnlyList<PlanItemDto> MergeSends(IReadOnlyList<PlanItemDto> items) =>
        items.GroupBy(i => i.Kind is "keep" or "hold" ? i.Kind + "\n" + i.Key : "send\n" + i.Destination).Select(g =>
        {
            var parts = g.ToList();
            if (parts.Count == 1) return parts[0];
            var allSkipped = parts.All(p => p.Skipped);
            var open = parts.Where(p => !p.Done && !p.Skipped).ToList();
            // What the app sees: the last part that still has something to say (paying off what you charged counts the
            // card's payment too), or else the last word on a finished one.
            var status = open.Select(p => p.Status).LastOrDefault(s => s is not null) ?? parts.Select(p => p.Status).LastOrDefault(s => s is not null);
            return new PlanItemDto("send:" + string.Join("+", parts.Select(p => p.Key)), "send", $"{parts.Count} payments",
                parts.Where(p => !p.Skipped).Sum(p => p.Cents), parts[0].Destination, "",
                Done: !allSkipped && open.Count == 0, Auto: !allSkipped && parts.All(p => p.Auto || p.Skipped), Status: status,
                Skipped: allSkipped, Parts: parts);
        }).ToList();
}
