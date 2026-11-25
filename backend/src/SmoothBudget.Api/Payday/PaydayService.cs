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
/// <param name="WaitCents">For the check you're on: the part of this move checking can't spare yet (DECISIONS.md 308).</param>
/// <param name="NowCents">With <paramref name="WaitCents"/>: what checking can spare for it now.</param>
/// <param name="WaitFor">With <paramref name="WaitCents"/>: what it waits for ("Bonus lands Nov 30").</param>
/// <param name="WaitDate">With <paramref name="WaitCents"/>: the day the money it waits for is expected, if it's a bonus.</param>
/// <param name="WaitName">With <paramref name="WaitDate"/>: that money's name.</param>
/// <param name="SentCents">For a move, on the check you're on: how much has gone toward it since payday (DECISIONS.md 313).</param>
/// <param name="HaveCents">For "keep" and "hold", on the check you're on: what the account has now (null when the app can't tell).</param>
/// <param name="NeedCents">With <paramref name="HaveCents"/>: what it needs to have for this.</param>
/// <param name="MinimumCents">For a card's payment: the part of it that's the card's minimum (the rest is extra).</param>
public record PlanItemDto(string Key, string Kind, string Label, long Cents, string Destination, string Why, bool Done,
    bool Auto = false, string? Status = null, IReadOnlyList<PlanBillDto>? Bills = null, long ShortCents = 0,
    bool Skippable = false, bool Skipped = false, IReadOnlyList<PlanItemDto>? Parts = null,
    long WaitCents = 0, long NowCents = 0, string? WaitFor = null, DateOnly? WaitDate = null, string? WaitName = null,
    long SentCents = 0, long? HaveCents = null, long? NeedCents = null, long MinimumCents = 0);
public record PlanBillDto(string Name, long Cents, DateOnly Due);
/// <summary>Money still to land before the next payday (DECISIONS.md 277): "Bonus lands Nov 24", "Jane sends $800.00".</summary>
/// <param name="Date">When it's expected: a bonus's date, or the start of a friend's usual window.</param>
public record ComingDto(string Label, long Cents, DateOnly Date);
/// <param name="ShortCents">What the check still lacks for everything planned for it, after what an earlier check keeps for it.</param>
/// <param name="HeldEarlierCents">How much of what this check needs is kept back from the check before it, paid on <paramref name="HeldOn"/> (DECISIONS.md 253).</param>
/// <param name="SpareCents">For the check you're on now: what checking can spare before the next payday (DECISIONS.md 272, 293).
/// Null when a checking account isn't bank-synced. Below zero when the accounts are already short.</param>
/// <param name="Coming">For the check you're on now: money still to land before the next payday, soonest first (a bonus, or a
/// friend's part of a bill). Safe to Spend counts it from the day it's due (DECISIONS.md 277, 293).</param>
public record PlanDto(IReadOnlyList<PlanItemDto> Items, long ShortCents, long HeldEarlierCents = 0, DateOnly? HeldOn = null, long? SpareCents = null,
    IReadOnlyList<ComingDto>? Coming = null)
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

    /// <summary>A card payment or a transfer: what went to the cards and to savings over some days.</summary>
    private sealed record Move(DateOnly Date, long AmountCents, TransactionKind Kind, AccountType Type, Guid? CategoryId, Guid AccountId,
        bool IsTransfer, Guid? TransferAccountId, Guid? TransferGroupId);

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
        // Income you said is coming, like a bonus (DECISIONS.md 256): counted until it lands, then it's other income above.
        var coming = await ExpectedIncomes.StillComingAsync(db, householdId, start, end, c.Today, ct);
        var bonus = coming.Sum(x => x.AmountCents);
        return (total + other + bonus, $"Your paychecks this budget month: {string.Join(", ", parts)}."
                               + (other > 0 ? $" Plus {PayEngine.Dollars(other)} of other income that has come in." : "")
                               + (bonus > 0 ? $" Plus {string.Join(", ", coming.Select(x => $"{x.Name} {PayEngine.Dollars(x.AmountCents)} expected {x.ExpectedDate:MMM d}"))}." : ""));
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
            .Select(t => new { t.Id, t.Date, t.AmountCents, t.Merchant, t.IncomeType }).ToListAsync(ct);
        if (!deposits.Any(d => d.Date >= start)) return 0;
        var linked = (await db.PayPeriodForecasts.AsNoTracking().Where(f => f.HouseholdId == householdId && f.DepositTransactionId != null)
            .Select(f => f.DepositTransactionId!.Value).ToListAsync(ct)).ToHashSet();
        static bool Near(DateOnly deposit, DateOnly payDate) => deposit >= payDate.AddDays(-2) && deposit <= payDate.AddDays(3);
        var payers = deposits.Where(d => linked.Contains(d.Id) || c.Stubs.Any(s => s.Stub.NetCents == -d.AmountCents && Near(d.Date, s.Stub.PayDate)))
            .Select(d => d.Merchant).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // What you said in Review comes first (DECISIONS.md 254): other income always counts here, even from your employer
        // (a bonus); a paycheck counts here only when it's extra pay, away from the month's pay dates.
        return deposits
            .Where(d => d.Date >= start && !linked.Contains(d.Id))
            .Where(d => d.IncomeType switch
            {
                Data.IncomeType.Other => true,
                Data.IncomeType.Paycheck => !checks.Any(p => Near(d.Date, p.PayDate)),
                _ => !payers.Contains(d.Merchant)
                     && !checks.Any(p => Near(d.Date, p.PayDate) && Math.Abs(-d.AmountCents - p.Cents) <= Math.Max(p.Cents / 2, 100_00)),
            })
            .Sum(d => -d.AmountCents);
    }

    /// <summary>Matches new deposits to their pay dates (after a sync or import), so a paycheck doesn't wait in Review (DECISIONS.md 254).</summary>
    public async Task MatchDepositsAsync(Guid householdId, CancellationToken ct)
    {
        if (!await db.PayPeriodForecasts.AnyAsync(f => f.HouseholdId == householdId, ct))
        {
            await ExpectedIncomes.MatchAsync(db, householdId, ct); // a bonus still lands without a pay forecast (DECISIONS.md 256)
            return;
        }
        var c = await ContextAsync(householdId, ct);
        var gate = Gates.GetOrAdd(householdId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { await MatchActualsAsync(householdId, c, ct); }
        finally { gate.Release(); }
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

    /// <summary>
    /// The day each recent check's paycheck landed: the deposit matched to it, which can come up to 2 days early or 3 late
    /// (DECISIONS.md 270, 303). A check with one is the one you're on from then, even before its payday.
    /// </summary>
    public static async Task<Dictionary<DateOnly, DateOnly>> LandedOnAsync(AppDbContext db, Guid householdId, DateOnly today, CancellationToken ct) =>
        await db.PayPeriodForecasts.AsNoTracking()
            .Where(f => f.HouseholdId == householdId && f.PayDate > today.AddDays(-80) && f.PayDate <= today.AddDays(3) && f.DepositTransactionId != null)
            .Join(db.Transactions, f => f.DepositTransactionId, t => t.Id, (f, t) => new { f.PayDate, t.Date })
            .ToDictionaryAsync(x => x.PayDate, x => x.Date, ct);

    /// <summary>
    /// Where a check's pay period starts (DECISIONS.md 303): its payday, assumed from your schedule, until its paycheck lands;
    /// then the day it landed. "Since payday" and the card-charge windows all start here, so a check that comes early or late
    /// moves them with it.
    /// </summary>
    public static DateOnly PeriodStart(DateOnly pay, IReadOnlyDictionary<DateOnly, DateOnly> landedOn) =>
        landedOn.TryGetValue(pay, out var on) ? on : pay;

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
        Dictionary<DateOnly, DateOnly> landedOn;
        var gate = Gates.GetOrAdd(householdId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await MatchActualsAsync(householdId, c, ct);
            var miss = await TypicalMissAsync(householdId, ct);
            // The check you're living on now (the latest one paid, or one that landed early: DECISIONS.md 270) and the next few.
            landedOn = await LandedOnAsync(db, householdId, c.Today, ct);
            var paidThrough = landedOn.Keys.Append(c.Today).Max();
            var current = PayCalendar.From(c.Today.AddDays(-45), c.Rules).TakeWhile(p => p.PayDate <= paidThrough).LastOrDefault();
            var periods = PayCalendar.From(paidThrough.AddDays(1), c.Rules).Take(ChecksShown).ToList();
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
        var plans = await PlansAsync(householdId, c, forecasts, landed, landedOn, ct);
        var checks = forecasts.Select((f, i) => new CheckDto(
            f.Period.PayDate, f.Period.Start, f.Period.End, f.HasSchedule, f.GrossCents, f.TaxableCents, f.NetCents, f.LowNetCents, f.HighNetCents,
            f.LowConfidence, f.Reasons, f.Notes, f.Lines.Select(l => new LineDto(l.Name, l.Cents, l.Note)).ToList(),
            f.Taxes.Select(t => new AmountDto(t.Name, t.CurrentCents)).ToList(), f.Deductions.Select(d => new AmountDto(d.Name, d.CurrentCents)).ToList(),
            plans[i], f.Period.PayDate <= c.Today || landedOn.ContainsKey(f.Period.PayDate), landed.TryGetValue(f.Period.PayDate, out var got) ? got : null)).ToList();

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
    /// What you said in Review comes first (DECISIONS.md 254): a deposit you called a paycheck is that check whatever its
    /// amount, and one you called other income is never a paycheck (a check it was matched to looks again). The deposit is
    /// linked under a stub too, and a matched deposit leaves Review: it's your paycheck.
    /// </summary>
    public async Task MatchActualsAsync(Guid householdId, PayContext c, CancellationToken ct)
    {
        // A bonus you said was coming is matched first (DECISIONS.md 256): matched, it's other income, which a paycheck
        // never takes, so a bonus landing on payday for about a check's amount isn't mistaken for the check.
        await ExpectedIncomes.MatchAsync(db, householdId, ct);
        var rows = await db.PayPeriodForecasts.Where(f => f.HouseholdId == householdId && f.PayDate <= c.Today.AddDays(3)).ToListAsync(ct);
        var linkedIds = rows.Where(f => f.DepositTransactionId != null).Select(f => f.DepositTransactionId!.Value).ToList();
        var notPay = (await db.Transactions.AsNoTracking()
            .Where(t => linkedIds.Contains(t.Id) && (t.IncomeType == Data.IncomeType.Other || t.Kind != TransactionKind.Income || t.IsTransfer))
            .Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        foreach (var f in rows.Where(f => f.DepositTransactionId is { } d && notPay.Contains(d)))
        {
            f.DepositTransactionId = null;
            if (f.ActualSource == "deposit") (f.ActualNetCents, f.ActualSource) = (null, null);
        }
        var open = rows.Where(f => f.DepositTransactionId is null).ToList();
        if (open.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return;
        }
        var stubByDate = c.Stubs.GroupBy(s => s.Stub.PayDate).ToDictionary(g => g.Key, g => g.First().Stub);
        var linked = await db.PayPeriodForecasts.Where(f => f.HouseholdId == householdId && f.DepositTransactionId != null).Select(f => f.DepositTransactionId!.Value).ToListAsync(ct);
        linked.RemoveAll(notPay.Contains);
        var payers = await db.Transactions.AsNoTracking().Where(t => linked.Contains(t.Id)).Select(t => t.Merchant).Distinct().ToListAsync(ct);
        var from = open.Min(f => f.PayDate).AddDays(-2);
        var to = open.Max(f => f.PayDate).AddDays(3);
        var deposits = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Kind == TransactionKind.Income && t.AmountCents < 0 && !t.IsTransfer && !t.IsPartnerTransfer
                        && t.IncomeType != Data.IncomeType.Other && t.Account.Type == AccountType.Checking && t.Date >= from && t.Date <= to)
            .Select(t => new { t.Id, t.Date, t.AmountCents, t.Merchant, Said = t.IncomeType == Data.IncomeType.Paycheck && t.CategorySource == CategorySource.User }).ToListAsync(ct);
        var matched = new List<Guid>();

        foreach (var f in open)
        {
            var stub = stubByDate.GetValueOrDefault(f.PayDate);
            if (stub is not null)
                (f.ActualGrossCents, f.ActualNetCents, f.ActualSource) = (stub.GrossCents, stub.NetCents, "stub");
            var expected = stub?.NetCents ?? f.PredictedNetCents;
            var match = deposits
                .Where(d => d.Date >= f.PayDate.AddDays(-2) && d.Date <= f.PayDate.AddDays(3) && !linked.Contains(d.Id)
                            && (d.Said || Math.Abs(-d.AmountCents - expected) <= Math.Max(expected / 2, 100_00)))
                .OrderBy(d => d.Said ? 0 : 1).ThenBy(d => payers.Contains(d.Merchant) ? 0 : 1).ThenBy(d => Math.Abs(-d.AmountCents - expected)).FirstOrDefault();
            if (match is null) continue;
            f.DepositTransactionId = match.Id;
            if (stub is null) (f.ActualNetCents, f.ActualSource) = (-match.AmountCents, "deposit");
            linked.Add(match.Id);
            matched.Add(match.Id);
        }
        await db.SaveChangesAsync(ct);
        // Matched to its pay date it's most likely your paycheck, but it still waits in Review for you to confirm (DECISIONS.md 259).
        if (matched.Count > 0)
            await db.Transactions.Where(t => matched.Contains(t.Id) && t.CategorySource != CategorySource.User)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.IncomeType, Data.IncomeType.Paycheck)
                    .SetProperty(t => t.CategoryReason, "Looks like your paycheck: it landed on payday."), ct);
    }

    /// <summary>
    /// The payday plan for each check (SPEC 4.6, DECISIONS.md 138). For the check you're living on now, each part shows
    /// whether it's done, worked out from your accounts: the "keep" line from the account's balance against the bills
    /// still to come, the card payment and savings from the transfers seen since payday, and the sweep from whether it's
    /// marked sent. You can still check anything off yourself.
    /// </summary>
    private async Task<List<PlanDto?>> PlansAsync(Guid householdId, PayContext c, List<ForecastCheck> checks, IReadOnlyDictionary<DateOnly, long> landed,
        IReadOnlyDictionary<DateOnly, DateOnly> landedOn, CancellationToken ct)
    {
        // A check is paid on its payday, or on the day it landed when that was earlier (DECISIONS.md 270). Its pay period
        // starts on the day it landed once it has (303). "Since payday", for the tasks, counts from then or from payday,
        // whichever came first: what you sent on payday, before a late check showed up, still counts.
        bool Paid(DateOnly pay) => pay <= c.Today || landedOn.ContainsKey(pay);
        DateOnly Start(DateOnly pay) => PeriodStart(pay, landedOn);
        DateOnly Since(DateOnly pay) => Start(pay) < pay ? Start(pay) : pay;
        // Closed accounts (DECISIONS.md 318) aren't anywhere to keep or send money, but what moved through them still tells a
        // transfer to savings or a card's bill apart, and a sweep or bill naming one goes to what replaced it.
        var everyAccount = await db.Accounts.AsNoTracking().Where(a => a.HouseholdId == householdId && a.IncludedInBudget).ToListAsync(ct);
        var accounts = everyAccount.Where(a => a.ClosedOn == null).ToList();
        // The bank's balance leaves out pending charges and deposits; every check of a balance here counts them
        // (DECISIONS.md 276), so the payday tasks and Home's warning see the same money.
        var accountIds = accounts.Select(a => a.Id).ToList();
        var pendingBy = await db.Transactions.AsNoTracking().Where(t => t.HouseholdId == householdId && t.Pending && accountIds.Contains(t.AccountId))
            .GroupBy(t => t.AccountId).Select(g => new { g.Key, Cents = g.Sum(t => t.AmountCents) }).ToDictionaryAsync(x => x.Key, x => x.Cents, ct);
        long Balance(Account a) => a.CurrentBalanceCents - pendingBy.GetValueOrDefault(a.Id);
        // An account the bank doesn't sync (DECISIONS.md 313): the balance from its last import (or the one you typed), moved by
        // every transaction since, as its Accounts card shows it (DECISIONS.md 189). Unknown when no balance was ever given.
        var unsyncedIds = accounts.Where(a => a.PlaidAccountId is null).Select(a => a.Id).ToList();
        var unsyncedActivity = unsyncedIds.Count == 0 ? []
            : await db.Transactions.AsNoTracking().Where(t => unsyncedIds.Contains(t.AccountId)).Select(t => new { t.AccountId, t.Date, t.AmountCents }).ToListAsync(ct);
        long? Known(Account a) => a.PlaidAccountId is not null ? Balance(a)
            : a.BalanceAsOf is { } asOf ? a.CurrentBalanceCents - unsyncedActivity.Where(t => t.AccountId == a.Id && t.Date > asOf).Sum(t => t.AmountCents)
            : null;
        string Has(Account a) => $"{AccountNames.Short(a)} has {PayEngine.Dollars(Known(a) ?? 0)}"
                                 + (a.PlaidAccountId is null ? " going by your last import" : pendingBy.GetValueOrDefault(a.Id) != 0 ? " counting pending" : "");
        string Label(Account a) => AccountNames.Label(a);
        // Where the savings goes (Savings → Payment settings, DECISIONS.md 314): all to the first savings account, or split.
        var savingsShares = Savings.SavingsSplit.Shares(accounts);
        var savingsAccount = savingsShares.Select(s => Label(s.Account)).FirstOrDefault() ?? "savings";
        var cards = everyAccount.Where(a => a.Type == AccountType.CreditCard).SelectMany(a => new[] { a.Name, Label(a), AccountNames.Imported(a) }).ToHashSet();
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
        var savingsIds = everyAccount.Where(a => a.Type == AccountType.Savings).Select(a => a.Id).ToHashSet();
        Task<List<Move>> MovesAsync(DateOnly from, DateOnly before) => db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Date >= from && t.Date < before && (t.Kind == TransactionKind.CardPayment || t.IsTransfer))
            .Select(t => new Move(t.Date, t.AmountCents, t.Kind, t.Account.Type, t.CategoryId, t.AccountId, t.IsTransfer, t.TransferAccountId, t.TransferGroupId))
            .ToListAsync(ct);
        // Money into savings, in all and by account where the account is known.
        (long Total, Dictionary<Guid, long> By) IntoSavings(List<Move> moves)
        {
            var by = new Dictionary<Guid, long>();
            if (savingsIds.Count == 0)
                return (moves.Where(m => m.Type == AccountType.Checking && m.AmountCents > 0 && m.CategoryId is { } id && savingsCategories.Contains(id)).Sum(m => m.AmountCents), by);
            // Money that arrived on a savings account, and money that left checking for one (DECISIONS.md 302): you said
            // which account in Review, or the app matched it. A savings account that isn't bank-synced shows its side only
            // once you import its file, so the checking side counts until then; once it's there, the transfer counts once.
            // Not money moved from another savings account (DECISIONS.md 317): that was saved already.
            var arrived = moves.Where(m => savingsIds.Contains(m.AccountId) && m.AmountCents < 0 && !(m.TransferAccountId is { } from && savingsIds.Contains(from))).ToList();
            var seen = arrived.Select(m => (m.AccountId, Cents: -m.AmountCents, m.Date, m.TransferGroupId)).ToList();
            foreach (var m in arrived) by[m.AccountId] = by.GetValueOrDefault(m.AccountId) - m.AmountCents;
            long sent = 0;
            foreach (var m in moves.Where(m => m.Type == AccountType.Checking && m.AmountCents > 0 && m.IsTransfer
                                               && (m.TransferAccountId is { } to ? savingsIds.Contains(to) : m.CategoryId is { } id && savingsCategories.Contains(id))))
            {
                var twin = seen.FindIndex(s => (m.TransferGroupId is { } g && s.TransferGroupId == g)
                                               || (s.Cents == m.AmountCents && (m.TransferAccountId is null || s.AccountId == m.TransferAccountId)
                                                   && Math.Abs(s.Date.DayNumber - m.Date.DayNumber) <= Transactions.TransferFiling.OtherHalfDays));
                if (twin >= 0) seen.RemoveAt(twin);
                else
                {
                    sent += m.AmountCents;
                    if (m.TransferAccountId is { } into && savingsIds.Contains(into)) by[into] = by.GetValueOrDefault(into) + m.AmountCents;
                }
            }
            return (arrived.Sum(m => -m.AmountCents) + sent, by);
        }
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
        // Income you said is coming, like a bonus (DECISIONS.md 263): it's money for the check it lands with, from that
        // payday to the next, until it lands (then it's in the balance) or is late. Without it, a small check before a big
        // bonus asked the check before it to hold back money the bonus would cover.
        var bonuses = checks.Count == 0 ? []
            : await ExpectedIncomes.StillComingAsync(db, householdId, checks[0].Period.PayDate, checks[^1].Period.End.AddDays(1), c.Today, ct);
        List<ExpectedIncome> BonusesFor(DateOnly pay, DateOnly next) => bonuses.Where(x => x.ExpectedDate >= pay && x.ExpectedDate < next).ToList();
        // Planned expenses (DECISIONS.md 327): each check up to the due date keeps its part in checking, on top of what the
        // checks before it kept, until it's paid.
        var planned = await Budget.PlannedExpenseService.LoadAsync(db, householdId, c.Rules, ct);
        var keepIn = accounts.Where(a => a.Type == AccountType.Checking).OrderBy(a => a.CreatedUtc).Select(Label).FirstOrDefault() ?? "checking";

        // What the checks already gone by this budget month sent toward its card payment and savings (DECISIONS.md 316). The
        // checks still to come split the rest of the month's lines, not an even share each: one that sent less (a bonus was
        // going to pay for the extra, and only landed later) leaves more for the next, and one that sent more leaves less.
        // To the cards, that's what was paid less what you'd charged (that check paid it off from its spending money, unless
        // you skipped it) and last month's leftover marked sent; to savings, what went in less last month's leftover. Only
        // when every check gone by this month has landed: otherwise the app can't tell what came from them.
        var gone = new List<DateOnly>();
        var lineBefore = new Dictionary<Guid, long>(); // by card account
        long savedBefore = 0;
        DateOnly? goneMonth = null;
        if (checks.Count > 0)
        {
            var first = checks[0].Period.PayDate;
            var m0 = c.Calendar.MonthContaining(first);
            var earlier = PayCalendar.From(m0.Start, c.Rules).TakeWhile(p => p.PayDate <= m0.End).Select(p => p.PayDate).Where(p => p < first).ToList();
            if (earlier.Count > 0 && earlier.All(landed.ContainsKey))
            {
                (gone, goneMonth) = (earlier, m0.Start);
                var (from, to) = (Since(gone[0]), (Paid(first) ? Since(first) : c.Today.AddDays(1)).AddDays(-1));
                var marked = sweeps.Where(s => s.DoneUtc is { } d && DateOnly.FromDateTime(d) >= from && DateOnly.FromDateTime(d) <= to).ToList();
                var paid = (await Debt.CardPayments.LoadAsync(db, householdId, from, to, ct)).GroupBy(p => p.CardAccountId).ToDictionary(g => g.Key, g => g.Sum(p => p.Cents));
                foreach (var card in cardAccounts)
                {
                    var payOff = gone.Where(g => !done.Any(d => d.PayDate == g && d.Key == SkipPrefix + $"cardspend:{card.Id}"))
                        .Sum(g => Math.Max(0, charged.Where(t => t.AccountId == card.Id && t.Date >= Start(PaydayBefore(g)) && t.Date <= Start(g).AddDays(-1)).Sum(t => t.AmountCents)));
                    var leftover = marked.Where(s => s.Kind != SweepKind.Savings && AccountNames.Current(everyAccount, s.Destination) == Label(card)).Sum(s => s.AmountCents);
                    lineBefore[card.Id] = Math.Max(0, paid.GetValueOrDefault(card.Id) - payOff - leftover);
                }
                savedBefore = Math.Max(0, IntoSavings(await MovesAsync(from, to.AddDays(1))).Total - marked.Where(s => s.Kind == SweepKind.Savings).Sum(s => s.AmountCents));
            }
        }
        // How many of a month's checks have gone by, for a check in it.
        int Settled(DateOnly monthStart, IReadOnlyList<DateOnly> inMonth, DateOnly pay) => monthStart == goneMonth && inMonth.Contains(pay) ? gone.Count : 0;
        // A card's minimum for a check: its share of what's left of the month's minimum after the checks gone by.
        long MinimumShare(DebtAccount d, DateOnly pay)
        {
            var month = c.Calendar.MonthContaining(pay);
            var inMonth = PayCalendar.From(month.Start, c.Rules).TakeWhile(p => p.PayDate <= month.End).Select(p => p.PayDate).ToList();
            var settled = Settled(month.Start, inMonth, pay);
            var sent = settled > 0 && d.AccountId is { } card ? lineBefore.GetValueOrDefault(card) : 0;
            return PaydayPlan.Share(Math.Max(0, Math.Min(d.MinimumPaymentCents ?? 0, d.BalanceCents) - sent), inMonth.Count - settled, Math.Max(0, inMonth.IndexOf(pay)) - settled);
        }

        // One check's plan before anything is carried between checks. It reads nothing from the database, so the loop
        // below can ask for the checks either side of the one it's on (DECISIONS.md 253).
        (PaydayPlanResult Plan, long NetCents, bool Current, DateOnly Pay, DateOnly Next, List<ExpectedIncome> Bonuses, long BonusFunded, List<ComingDto> FriendsComing)? Draft(int i)
        {
            var f = checks[i];
            var pay = f.Period.PayDate;
            var landedNet = landed.TryGetValue(pay, out var l) ? l : (long?)null;
            if (!f.HasSchedule && landedNet is null) return null;
            var current = Paid(pay);
            var next = PayCalendar.PeriodContaining(f.Period.End.AddDays(1), c.Rules).PayDate;
            var coming = BonusesFor(pay, next);
            var month = c.Calendar.MonthContaining(pay);
            var periods = PayCalendar.From(month.Start, c.Rules).TakeWhile(p => p.PayDate <= month.End).ToList();
            var inMonth = periods.Select(p => p.PayDate).ToList();
            var index = Math.Max(0, inMonth.IndexOf(pay));
            var share = $"{(inMonth.Count == 2 ? "Half" : $"1/{inMonth.Count}")} of the month's";
            var (debtMonthly, savingsMonthly) = (DebtMonthly(month.Start), SavingsMonthly(month.Start));
            // The month's checks gone by (DECISIONS.md 316): what they sent comes off, and this check and the ones after it
            // split the rest.
            var settled = Settled(month.Start, inMonth, pay);
            var (debtBefore, saveBefore) = settled > 0 ? (cardAccounts.Sum(a => lineBefore.GetValueOrDefault(a.Id)), savedBefore) : (0L, 0L);
            var left = inMonth.Count - settled;

            // The budget gives a month's leftover to extra card payments and savings, and a bonus is part of that month's
            // money (DECISIONS.md 263). The part of those a bonus pays for comes from the check the bonus lands with; the
            // rest, and the card minimums, are split evenly. Otherwise the check before a bonus was asked for half of it.
            var bonusEach = periods.Select(p => BonusesFor(p.PayDate, PayCalendar.PeriodContaining(p.End.AddDays(1), c.Rules).PayDate).Sum(x => x.AmountCents)).ToList();
            var bonusMonth = bonusEach.Sum();
            var debtExtra = Math.Max(0, debtMonthly - debts.Sum(d => Math.Min(d.MinimumPaymentCents ?? 0, d.BalanceCents)));
            var flex = debtExtra + savingsMonthly;
            var funded = Math.Min(flex, bonusMonth);
            var lastWithBonus = bonusEach.FindLastIndex(b => b > 0);
            bool BySplit() => funded > 0 && flex > 0 && inMonth.IndexOf(pay) >= 0;
            // The part of a line's extra that this check's bonus pays for (zero without one still to come).
            long BonusPart(long extra)
            {
                if (!BySplit()) return 0;
                var byBonus = extra * funded / flex;
                long Part(int k) => byBonus * bonusEach[k] / bonusMonth;
                return index == lastWithBonus ? byBonus - Enumerable.Range(0, bonusEach.Count).Where(k => k != index).Sum(Part) : Part(index);
            }
            long Split(long line, long extra, long before) => BySplit()
                ? PaydayPlan.Share(Math.Max(0, line - extra * funded / flex - before), left, index - settled) + BonusPart(extra)
                : PaydayPlan.Share(Math.Max(0, line - before), left, index - settled);
            // What the checks gone by sent toward a line, for its note.
            string Gone(long before, string toward = "it") => settled == 0 ? ""
                : $" Your {string.Join(" and ", gone.Select(g => $"{g:MMM d}"))} check{(settled > 1 ? "s" : "")} sent {(before > 0 ? PayEngine.Dollars(before) : "nothing")} toward {toward}.";
            // What the card payment and savings lines say (DECISIONS.md 305): how much is this check's share, and how much a
            // bonus pays for, so the note matches the amount.
            string Why(string what, long line, long extra, long before)
            {
                var whole = PayEngine.Dollars(line);
                if (!BySplit())
                    return settled > 0
                        ? $"{(left > 1 ? $"{(left == 2 ? "Half" : $"1/{left}")} of what's left" : "What's left")} of the month's {whole} {what}.{Gone(before)}"
                        : inMonth.Count > 1 ? $"{share} {whole} {what} in your budget." : $"Your budget's {whole} {what}.";
                var bonus = BonusPart(extra);
                var own = Split(line, extra, before) - bonus;
                var name = coming.Count == 1 ? coming[0].Name : "your bonuses";
                return bonus <= 0 ? $"This check's share of the month's {whole} {what}. The part your bonus pays for comes from the check it lands with."
                    : own <= 0 ? $"The month's {whole} {what}, all from this check: {name} pays for it."
                    : $"{PayEngine.Dollars(own)} is this check's share of the month's {whole} {what}, plus {PayEngine.Dollars(bonus)} that {name} pays for.";
            }
            string WhyWithGone(string text, long before) => text + Gone(before, "the month's card payments");

            // Bills still to come before the next payday (for the current check: from today, since paid ones are done).
            // A bill your partner's fixed contribution goes toward (rent) still needs all of it in the account when it's
            // charged, since their money lands there too (DECISIONS.md 165); only your share comes out of this check.
            var from = current ? c.Today : pay;
            var offsetUsed = new HashSet<Guid>();
            var friendsComing = new List<ComingDto>();
            var bills = items.Where(b => b.NextExpected >= from && b.NextExpected < next).OrderBy(b => b.NextExpected).Select(b =>
            {
                var toward = b.CategoryId is { } cat && offsetUsed.Add(cat)
                    ? arrangements.Where(a => a.CategoryId == cat && a.StartsOn <= b.NextExpected).ToList() : [];
                var covered = Math.Min(b.AmountCents, toward.Sum(a => a.AmountCents));
                // A friend's part that hasn't arrived yet (DECISIONS.md 277), expected from the start of their usual window.
                foreach (var a in toward)
                    if (a.AmountCents - Math.Min(a.AmountCents, Arrived(a.Id, b.NextExpected)) is var left and > 0)
                        friendsComing.Add(new ComingDto($"{a.Friend.Name} sends {PayEngine.Dollars(left)}", left,
                            PartnerContributions.Window(c.Calendar.MonthContaining(b.NextExpected), a.WindowFromDay, a.WindowToDay).Start));
                // Whoever sends toward it (DECISIONS.md 211): "Jane sends", "Jane and Alex send".
                var senders = toward.Select(a => a.Friend.Name).Distinct().ToList();
                var who = $"{Shared.SharedService.JoinNames(senders) ?? "Your friend"} {(senders.Count > 1 ? "send" : "sends")}";
                return new PlanBill($"bill:{b.Key}", b.Name, b.AmountCents, b.NextExpected, b.AccountName,
                    covered > 0 ? $"{b.Name} counts in full: {who} {PayEngine.Dollars(covered)} of it to this account, so {PayEngine.Dollars(b.AmountCents - covered)} is yours from this check." : null,
                    covered, Math.Min(covered, toward.Sum(a => Math.Min(a.AmountCents, Arrived(a.Id, b.NextExpected)))));
            }).ToList();
            var debtShare = Split(debtMonthly, debtExtra, debtBefore);
            var transfers = new List<PlanTransfer>();
            if (withMinimums.Count > 1)
            {
                var split = PaydayPlan.SplitCardPayment(debtShare,
                    withMinimums.Select(d => new CardMinimum($"debt:{d.Id}", DebtLabel(d), MinimumShare(d, pay))).ToList(),
                    $"debt:{targetDebt!.Id}");
                var why = snowball ? "smallest balance" : "highest interest";
                var checkShare = inMonth.Count > 1 ? " (this check's share)" : "";
                transfers.AddRange(split.Select(s => s.Card.Key == $"debt:{targetDebt.Id}"
                    ? new PlanTransfer(s.Card.Key, s.ExtraCents > 0 ? "Minimum + extra" : "Minimum payment", s.Cents, s.Card.Destination,
                        WhyWithGone(s.ExtraCents > 0
                            ? $"The {PayEngine.Dollars(s.Card.MinimumCents)} minimum{checkShare}, plus {PayEngine.Dollars(s.ExtraCents)} extra ({why})."
                            : $"The minimum{checkShare}. Extra goes here first ({why}).", debtBefore))
                    : new PlanTransfer(s.Card.Key, "Minimum payment", s.Cents, s.Card.Destination,
                        $"Just the minimum{checkShare}. Extra goes to {target} first.")));
            }
            else
                transfers.Add(new("debt", "Card payment", debtShare, target,
                    Why("card payment", debtMonthly, debtExtra, debtBefore)));

            // The check you're on pays off what you charged between the last payday and this one. The next check shows
            // what you've charged since this payday so far. It comes from the spending money still in checking (you spent
            // it on the card instead), so it isn't taken out of the check itself.
            var nextUp = !current && (i == 0 || Paid(checks[i - 1].Period.PayDate));
            if (current || nextUp)
            {
                // Each window runs from the day the check before landed to the day before this one did (DECISIONS.md 303).
                var (spendFrom, spendTo) = current ? (Start(PaydayBefore(pay)), Start(pay).AddDays(-1)) : (Start(PaydayBefore(pay)), c.Today);
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
                transfers.AddRange(sweeps.Where(s => s.DoneUtc == null || (current && s.DoneUtc >= Since(pay).ToDateTime(TimeOnly.MinValue)))
                    .Select(s => new PlanTransfer($"sweep:{s.Id}", "Last month's leftover", s.AmountCents, AccountNames.Current(everyAccount, s.Destination), "Left over from last month.")));
            var toSave = Split(savingsMonthly, savingsMonthly, saveBefore);
            var saveWhy = Why("savings", savingsMonthly, savingsMonthly, saveBefore);
            if (savingsShares.Count <= 1)
                transfers.Add(new("savings", "Savings", toSave, savingsAccount, saveWhy));
            else
            {
                // Split between accounts (DECISIONS.md 314): a move to each, its percent of this check's savings.
                var parts = Savings.SavingsSplit.Divide(toSave, savingsShares.Select(s => (long)s.Percent).ToList());
                for (var k = 0; k < savingsShares.Count; k++)
                    transfers.Add(new($"savings:{savingsShares[k].Account.Id}", "Savings", parts[k], Label(savingsShares[k].Account),
                        $"{saveWhy} {savingsShares[k].Percent}% of it goes here."));
            }

            var net = landedNet ?? f.NetCents;
            var built = PaydayPlan.Build(net + coming.Sum(x => x.AmountCents), bills, transfers);
            // Money kept for a planned expense is a "hold": it stays in checking, after the bills it keeps (327).
            var setAside = planned.Where(e => !e.Paid && pay <= e.Row.DueOn).Select(e =>
            {
                var (need, own) = PlannedExpenses.AtCheck(e.Parts, pay, e.SpentCents);
                var earlier = need - own;
                var why = (own > 0 ? $"{PayEngine.Dollars(own)} from this check" + (earlier > 0 ? $", on top of {PayEngine.Dollars(earlier)} set aside by earlier checks." : ".")
                              : $"Set aside by earlier checks.")
                          + $" {e.Row.Name} costs {PayEngine.Dollars(e.Row.AmountCents)} on {e.Row.DueOn:MMM d}: {(e.Parts.Count == 1 ? "this check sets it aside" : $"{e.Parts.Count} checks set it aside")}. Keep it in checking until it's paid.";
                return new PlanItem($"setaside:{e.Row.Id}", "hold", $"{e.Row.Name}, due {e.Row.DueOn:MMM d}", need, keepIn, why, FromOthersCents: earlier);
            }).Where(x => x.Cents > 0).ToList();
            if (setAside.Count > 0)
            {
                var items = built.Items.ToList();
                items.InsertRange(items.FindLastIndex(x => x.Kind == "keep") + 1, setAside);
                built = new PaydayPlanResult(items, Math.Max(0, items.Sum(x => x.Cents - x.FromOthersCents) - net - coming.Sum(x => x.AmountCents)));
            }
            return (built, net, current, pay, next, coming,
                BonusPart(debtExtra) + BonusPart(savingsMonthly), friendsComing);
        }
        // What a check has to pay from its own money (not a friend's part of a bill, or card charges paid from spending money).
        static long Own(PaydayPlanResult p) => p.Items.Sum(x => x.Cents - x.FromOthersCents);
        var firstChecking = accounts.Where(a => a.Type == AccountType.Checking).OrderBy(a => a.CreatedUtc).Select(Label).FirstOrDefault() ?? "checking";

        var result = new List<PlanDto?>();
        for (var i = 0; i < checks.Count; i++)
        {
            if (Draft(i) is not { } draft) { result.Add(null); continue; }
            var (plan, net, current, pay, next, coming, bonusFunded, friendsComing) = draft;
            var bonusCents = coming.Sum(x => x.AmountCents);

            // Money carried between checks (DECISIONS.md 253). When the next check can't pay for everything planned for it
            // (a small check with rent due right after it), this one keeps the difference in the account, as far as it has
            // room after its own plan; and a check an earlier one keeps money for is short by that much less.
            var after = i + 1 < checks.Count ? Draft(i + 1) : null;
            var before = i > 0 ? Draft(i - 1) : null;
            var hold = after is { } later ? Math.Min(later.Plan.ShortCents, Math.Max(0, net + bonusCents - Own(plan))) : 0;
            var heldEarlier = before is { } earlier
                ? Math.Min(plan.ShortCents, Math.Max(0, earlier.NetCents + earlier.Bonuses.Sum(x => x.AmountCents) - Own(earlier.Plan))) : 0;
            if (hold > 0 && after is { } nextCheck)
            {
                var into = nextCheck.Plan.Items.FirstOrDefault(x => x.Kind == "keep")?.Destination
                           ?? plan.Items.FirstOrDefault(x => x.Kind == "keep")?.Destination ?? firstChecking;
                var withHold = plan.Items.ToList();
                withHold.Insert(withHold.FindLastIndex(x => x.Kind == "keep") + 1, new PlanItem($"hold:{nextCheck.Pay:yyyy-MM-dd}", "hold", $"bills after {nextCheck.Pay:MMM d}", hold, into,
                    $"Your {nextCheck.Pay:MMM d} check is {(landed.ContainsKey(nextCheck.Pay) ? "" : "about ")}{PayEngine.Dollars(nextCheck.NetCents)}"
                    + string.Concat(nextCheck.Bonuses.Select(x => $", plus {x.Name} {PayEngine.Dollars(x.AmountCents)} expected {x.ExpectedDate:MMM d}"))
                    + $", and {PayEngine.Dollars(Own(nextCheck.Plan))} has to come out of {(nextCheck.Bonuses.Count > 0 ? "them" : "it")} for its bills, card payments and savings. Leave {PayEngine.Dollars(hold)} of this check{string.Concat(coming.Select(x => $" and {x.Name}"))} in the account to cover the rest."
                    + (hold < nextCheck.Plan.ShortCents ? $" That check is still {PayEngine.Dollars(nextCheck.Plan.ShortCents - hold)} short after this." : "")));
                plan = plan with { Items = withHold };
            }

            // What happened since payday, for the check you're on now.
            var paidOn = Since(pay);
            var moves = current ? await MovesAsync(paidOn, next) : [];
            var cardPaid = moves.Where(m => m.Kind == TransactionKind.CardPayment && m.Type == AccountType.Checking && m.AmountCents > 0).Sum(m => m.AmountCents);
            // Per card (DECISIONS.md 244): the payment as the card's own account shows it, or as it left checking when the card
            // isn't synced (or its statement isn't uploaded yet). With none tied to a card, the checking side for all cards.
            var toCards = current ? await Debt.CardPayments.LoadAsync(db, householdId, Since(pay), next.AddDays(-1), ct) : [];
            long PaidTo(Guid? accountId) => toCards.Where(p => p.CardAccountId == accountId).Sum(p => p.Cents);
            var cardSideSeen = toCards.Count > 0;
            var allDebtLines = plan.Items.Where(x => x.Kind == "debt").Sum(x => x.Cents);
            var (intoSavings, intoBy) = IntoSavings(moves); // what reached each savings account whose side or transfer says so

            Account? AccountFor(PlanItem x) => accounts.FirstOrDefault(a => Label(a) == x.Destination || AccountNames.Imported(a) == x.Destination || a.Name == x.Destination);
            // What has gone toward each move to savings: the savings first, then last month's leftover sent to savings. With
            // it all going to one account, money into any savings account counts. Split between accounts (DECISIONS.md 314),
            // each takes what reached its own account, then money filed as savings without saying which account.
            var savingsGot = new Dictionary<string, long>();
            {
                var split = plan.Items.Count(x => x.Kind == "savings") > 1;
                var pots = split ? new Dictionary<Guid, long>(intoBy) : [];
                var loose = Math.Max(0, intoSavings - pots.Values.Sum());
                long Take(Account? a, long want)
                {
                    want = Math.Max(0, want);
                    var got = a is not null && pots.TryGetValue(a.Id, out var pot) ? Math.Min(want, Math.Max(0, pot)) : 0;
                    if (got > 0) pots[a!.Id] -= got;
                    var more = Math.Min(want - got, loose);
                    loose -= more;
                    return got + more;
                }
                foreach (var x in plan.Items.Where(x => x.Kind == "savings"))
                    savingsGot[x.Key] = Take(AccountFor(x), x.Cents);
                foreach (var x in plan.Items.Where(x => x.Kind == "sweep" && sweeps.FirstOrDefault(s => x.Key == $"sweep:{s.Id}")?.Kind == SweepKind.Savings))
                    savingsGot[x.Key] = Take(AccountFor(x), x.Cents);
            }
            // How far the account's balance from the bank falls short of a "keep" line, for the check you're on now. A friend's
            // part of a bill that hasn't arrived yet will be there when it's charged (DECISIONS.md 275), so it isn't counted.
            long ShortBy(PlanItem x) =>
                current && x.Kind == "keep" && AccountFor(x) is { PlaidAccountId: not null } a
                    ? Math.Max(0, x.Cents - (x.FromOthersCents - (x.Bills ?? []).Sum(b => b.ArrivedCents)) - Balance(a)) : 0;

            const string NoBalance = "Connect this account's bank, or import a CSV with its balance, and this checks itself.";
            long ArrivedForLater(PlanItem x) => after is { } an
                ? an.Plan.Items.Where(y => y.Kind == "keep" && y.Destination == x.Destination).SelectMany(y => y.Bills ?? []).Sum(b => b.ArrivedCents) : 0;
            // What an account needs to have for a "keep" line (less a friend's part still to arrive, DECISIONS.md 275), or for a
            // "hold" line on top of the bills it keeps and a friend's money already in for the later bills.
            long? Need(PlanItem x) => x.Kind switch
            {
                "keep" => x.Cents - (x.FromOthersCents - (x.Bills ?? []).Sum(b => b.ArrivedCents)),
                // Several holds in one account (one for the next check's bills, one for a planned expense) add up.
                "hold" => plan.Items.Where(y => y.Kind == "keep" && y.Destination == x.Destination)
                              .Sum(y => y.Cents - (y.FromOthersCents - (y.Bills ?? []).Sum(b => b.ArrivedCents)))
                          + plan.Items.Take(plan.Items.ToList().FindIndex(y => y.Key == x.Key) + 1).Where(y => y.Kind == "hold" && y.Destination == x.Destination).Sum(y => y.Cents)
                          + ArrivedForLater(x),
                _ => null,
            };
            var sentToward = SentToward();

            // How much has gone toward each move since payday (DECISIONS.md 313), for its progress bar: a card's payments go to
            // its minimum, then paying off what you charged, then last month's leftover; savings transfers to the savings line,
            // then the leftover. With no card's own side seen, the payments leaving checking count for the cards in order.
            Dictionary<string, long> SentToward()
            {
                var got = new Dictionary<string, long>();
                if (!current) return got;
                var lumped = cardPaid;
                var perCard = new Dictionary<Guid, long>();
                long FromCards(Guid? card, long want)
                {
                    if (want <= 0) return 0;
                    if (cardSideSeen && card is { } id)
                    {
                        var have = perCard.TryGetValue(id, out var h) ? h : PaidTo(id);
                        var take = Math.Min(want, have);
                        perCard[id] = have - take;
                        return take;
                    }
                    var t = Math.Min(want, lumped);
                    lumped -= t;
                    return t;
                }
                foreach (var kind in new[] { "debt", "cardspend", "savings", "sweep" })
                    foreach (var x in plan.Items.Where(i => i.Kind == kind))
                        got[x.Key] = kind switch
                        {
                            "debt" => FromCards(x.Key.StartsWith("debt:", StringComparison.Ordinal) ? debts.FirstOrDefault(d => x.Key == $"debt:{d.Id}")?.AccountId : null, x.Cents),
                            "cardspend" => FromCards(Guid.TryParse(x.Key["cardspend:".Length..], out var on) ? on : null, x.Cents),
                            "savings" => savingsGot.GetValueOrDefault(x.Key),
                            _ => sweeps.FirstOrDefault(s => x.Key == $"sweep:{s.Id}")?.Kind == SweepKind.Savings
                                ? savingsGot.GetValueOrDefault(x.Key)
                                : FromCards(AccountFor(x) is { Type: AccountType.CreditCard } card ? card.Id : null, x.Cents),
                        };
                return got;
            }

            (bool Auto, string? Status) Status(PlanItem x)
            {
                if (!current) return (false, null);
                switch (x.Kind)
                {
                    case "keep":
                        var acct = AccountFor(x);
                        if (acct is null || Known(acct) is null) return (false, NoBalance);
                        // A friend's part still to arrive will be there when the bill is charged (DECISIONS.md 275).
                        var coming = x.FromOthersCents - (x.Bills ?? []).Sum(b => b.ArrivedCents);
                        var withFriend = coming > 0 ? $" with the {PayEngine.Dollars(coming)} {everyoneSends} still to come" : "";
                        var gap = Math.Max(0, Need(x)!.Value - Known(acct)!.Value);
                        return gap == 0
                            ? (true, $"{Has(acct)}, enough for these{withFriend}.")
                            : (false, $"{Has(acct)}: {PayEngine.Dollars(gap)} short for these{withFriend}.");
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
                    {
                        if (x.Cents <= 0) return (true, null);
                        // Split between accounts (DECISIONS.md 314), each counts what reached it.
                        var (into, to) = x.Key == "savings" ? (intoSavings, "savings")
                            : (savingsGot.GetValueOrDefault(x.Key), AccountFor(x) is { } sa ? AccountNames.Short(sa) : "savings");
                        return into >= x.Cents ? (true, $"Moved {PayEngine.Dollars(into)} to {to} since payday.")
                            : (false, into > 0 ? $"Moved {PayEngine.Dollars(into)} of {PayEngine.Dollars(x.Cents)} so far." : $"No transfer to {to} seen since payday yet.");
                    }
                    case "hold":
                    {
                        // Done when the account covers this on top of the bills it's already keeping, and any friend's
                        // money that has arrived early for the later bills (it's in the balance, and spoken for).
                        var holds = AccountFor(x);
                        if (holds is null || Known(holds) is not { } has) return (false, NoBalance);
                        var billsHere = plan.Items.Where(y => y.Kind == "keep" && y.Destination == x.Destination)
                            .Sum(y => y.Cents - (y.FromOthersCents - (y.Bills ?? []).Sum(b => b.ArrivedCents)));
                        var arrivedForLater = ArrivedForLater(x);
                        var need = Need(x)!.Value;
                        var also = (billsHere > 0 ? " and the bills above" : "")
                                   + (arrivedForLater > 0 ? $", with the {PayEngine.Dollars(arrivedForLater)} already sent toward the later bills" : "");
                        return has >= need
                            ? (true, $"{Has(holds)}, enough for this{also}.")
                            : (false, $"{Has(holds)}: {PayEngine.Dollars(need - has)} short of this{also}.");
                    }
                    case "sweep":
                        var task = sweeps.FirstOrDefault(s => x.Key == $"sweep:{s.Id}");
                        if (task?.DoneUtc is not null) return (true, "Marked as sent.");
                        var swept = sentToward.GetValueOrDefault(x.Key);
                        return swept >= x.Cents ? (true, $"Sent {PayEngine.Dollars(swept)} since payday.")
                            : (false, swept > 0 ? $"Sent {PayEngine.Dollars(swept)} of {PayEngine.Dollars(x.Cents)} so far." : null);
                    default:
                        return (false, null);
                }
            }

            // A card payment's minimum: this check's share of the card's (or every card's) minimum this month.
            long Minimum(string key) => (key == "debt" ? debts : debts.Where(d => key == $"debt:{d.Id}")).Sum(d => MinimumShare(d, pay));

            var tasks = Affordable(plan.Items.Select(x =>
            {
                // Done is only what your accounts show (DECISIONS.md 313): nothing is ticked off by hand.
                var (auto, status) = Status(x);
                // Skipping "Pay off what you charged" leaves those charges on the card as debt (DECISIONS.md 195).
                var skippable = x.Kind == "cardspend" && current;
                var skipped = skippable && !auto && done.Any(d => d.PayDate == pay && d.Key == SkipPrefix + x.Key);
                var account = x.Kind is "keep" or "hold" && current ? AccountFor(x) : null;
                var have = account is null ? null : Known(account);
                return new PlanItemDto(x.Key, x.Kind, x.Label, x.Cents, x.Destination, x.Why, !skipped && auto, auto, skipped ? "Skipped: these charges stay on the card." : status,
                    x.Bills?.Select(b => new PlanBillDto(b.Name, b.Cents, b.Due)).ToList(), ShortBy(x), skippable, skipped,
                    SentCents: auto && x.Kind is not ("keep" or "hold") ? x.Cents : Math.Min(x.Cents, sentToward.GetValueOrDefault(x.Key)),
                    HaveCents: have, NeedCents: have is null ? null : Need(x),
                    MinimumCents: x.Kind == "debt" ? Math.Min(x.Cents, Minimum(x.Key)) : 0);
            }).ToList());

            // Never ask for a move checking can't make yet (DECISIONS.md 308). For the check you're on, what checking can
            // spare now is its balance (pending counted) less the bills it keeps and anything held for the next check. That
            // goes to the moves still open in order: card minimums, paying off what you charged, extra card payments, last
            // month's leftover, then savings. The rest of a move waits for money still to come: a bonus, or room in checking.
            List<PlanItemDto> Affordable(List<PlanItemDto> list)
            {
                var fromAccounts = accounts.Where(a => a.Type == AccountType.Checking).ToList();
                if (!current || fromAccounts.Count == 0 || fromAccounts.Any(a => a.PlaidAccountId is null)) return list;
                bool InChecking(PlanItem x) => AccountFor(x) is { Type: AccountType.Checking };
                var kept = plan.Items.Where(x => x.Kind == "keep" && InChecking(x)).Sum(x => x.Cents - (x.FromOthersCents - (x.Bills ?? []).Sum(b => b.ArrivedCents)))
                           + plan.Items.Where(x => x.Kind == "hold" && InChecking(x)).Sum(x => x.Cents);
                var free = Math.Max(0, fromAccounts.Sum(Balance) - kept);

                // What each open move still needs, after what's already gone toward it.
                var lumped = cardPaid; // with no card's own side seen, the checking side pays the card lines in order
                long Left(PlanItemDto t)
                {
                    if (t.Done || t.Skipped) return 0;
                    switch (t.Kind)
                    {
                        case "debt" when t.Key.StartsWith("debt:", StringComparison.Ordinal):
                            if (cardSideSeen && debts.FirstOrDefault(d => t.Key == $"debt:{d.Id}")?.AccountId is { } cardAccount)
                                return Math.Max(0, t.Cents - PaidTo(cardAccount));
                            var take = Math.Min(t.Cents, lumped);
                            lumped -= take;
                            return t.Cents - take;
                        case "debt": return Math.Max(0, t.Cents - cardPaid);
                        case "cardspend":
                            if (!cardSideSeen || !Guid.TryParse(t.Key["cardspend:".Length..], out var spentOn)) return t.Cents;
                            var line = list.Where(y => y.Kind == "debt" && y.Destination == t.Destination).Sum(y => y.Cents);
                            return Math.Max(0, t.Cents - Math.Max(0, PaidTo(spentOn) - line));
                        case "savings": return Math.Max(0, t.Cents - savingsGot.GetValueOrDefault(t.Key));
                        case "sweep": return t.Cents;
                        default: return 0;
                    }
                }
                var left = list.ToDictionary(t => t.Key, Left);
                var now = list.ToDictionary(t => t.Key, _ => 0L);
                void Give(PlanItemDto t, long upTo)
                {
                    var g = Math.Min(free, Math.Min(upTo, left[t.Key] - now[t.Key]));
                    if (g <= 0) return;
                    now[t.Key] += g;
                    free -= g;
                }
                foreach (var t in list.Where(t => t.Kind == "debt")) Give(t, Minimum(t.Key));
                foreach (var kind in new[] { "cardspend", "debt", "sweep", "savings" })
                    foreach (var t in list.Where(t => t.Kind == kind)) Give(t, long.MaxValue);

                var bonus = coming.OrderBy(x => x.ExpectedDate).FirstOrDefault();
                var waitFor = bonus is not null ? $"{bonus.Name} lands {bonus.ExpectedDate:MMM d}" : "checking has room";
                var afterBills = kept > 0 ? " after the bills it keeps" : "";
                return list.Select(t =>
                {
                    var wait = left[t.Key] - now[t.Key];
                    if (wait <= 0) return t;
                    var progress = t.Status is { } s && !s.StartsWith("No ", StringComparison.Ordinal) ? s + " " : "";
                    var room = now[t.Key] > 0
                        ? $"Checking can spare {PayEngine.Dollars(now[t.Key])} of this now{afterBills}; the other {PayEngine.Dollars(wait)} once {waitFor}."
                        : kept > 0 ? $"Checking needs what it has for the bills it keeps; this can go once {waitFor}."
                        : $"Checking can't spare this yet; it can go once {waitFor}.";
                    return t with { WaitCents = wait, NowCents = now[t.Key], WaitFor = waitFor, WaitDate = bonus?.ExpectedDate, WaitName = bonus?.Name, Status = progress + room };
                }).ToList();
            }

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
                    // A bonus still coming before the next payday will be there too (DECISIONS.md 263).
                    shortCents = Math.Min(shortCents, Math.Max(0, stillOut - paidFrom.Sum(a => Balance(a!)) - bonusCents));
                }
            }
            result.Add(new PlanDto(MergeSends(tasks), shortCents, heldEarlier, heldEarlier > 0 ? before!.Value.Pay : null,
                current ? Spare() : null,
                current ? coming.Select(x => new ComingDto($"{x.Name} lands {x.ExpectedDate:MMM d}", x.AmountCents, x.ExpectedDate))
                    .Concat(friendsComing).OrderBy(x => x.Date).ToList() : null));

            // What checking can spare before the next payday (DECISIONS.md 272, 276, 293): every checking balance, counting
            // what's pending both ways (Debit's Cash), less what has to come out of it: the bills due before payday (in full),
            // what this check still has to send to the cards and savings, what it keeps for the next check's bills, a friend's
            // money already in for those, and what you've charged to cards since payday (the next check's plan pays that from
            // here). Money still to land before payday (a bonus, a friend's part of a bill) counts from the day it's due, so
            // until then only what's due before it comes off. It's the lowest point along the way: a later bill that money
            // can't cover still holds some back now.
            long? Spare()
            {
                var checking = accounts.Where(a => a.Type == AccountType.Checking).ToList();
                if (checking.Count == 0 || checking.Any(a => a.PlaidAccountId is null)) return null;
                var all = tasks.ToList();
                // Each bill on its due date; a keep line without its bills is due now.
                var bills = plan.Items.Where(x => x.Kind == "keep")
                    .SelectMany(x => x.Bills is { Count: > 0 } list ? list.Select(b => (Due: b.Due, b.Cents)) : [(Due: c.Today, x.Cents)]).ToList();
                var held = all.Where(x => x.Kind == "hold").Sum(x => x.Cents);
                var toCards = Math.Max(0, all.Where(x => x.Kind is "debt" or "cardspend" && !x.Skipped).Sum(x => x.Cents) - cardPaid);
                var toSavings = all.Where(x => x.Kind == "savings").Sum(x => Math.Max(0, x.Cents - savingsGot.GetValueOrDefault(x.Key)));
                // The extra card payment and savings a bonus still to come pays for (DECISIONS.md 263) wait for it: they
                // don't come out of checking before it lands, and neither does the bonus count until then (274).
                var waitForBonus = Math.Min(bonusFunded, toCards + toSavings);
                var sweeps = all.Where(x => x.Kind == "sweep" && !x.Done).Sum(x => x.Cents);
                var arrivedForLater = after is { } an ? an.Plan.Items.Where(y => y.Kind == "keep").SelectMany(y => y.Bills ?? []).Sum(b => b.ArrivedCents) : 0;
                var chargedSince = Math.Max(0, charged.Where(t => t.Date >= paidOn && t.Date <= c.Today).Sum(t => t.AmountCents));
                var landing = coming.Where(x => x.ExpectedDate < next).Select(x => (Date: x.ExpectedDate, Cents: x.AmountCents))
                    .Concat(friendsComing.Where(x => x.Date < next).Select(x => (x.Date, x.Cents))).ToList();
                // The card payment and savings a bonus pays for go out when it lands.
                var firstBonus = coming.Where(x => x.ExpectedDate < next).Select(x => (DateOnly?)x.ExpectedDate).Min();
                var start = checking.Sum(Balance) - toCards - toSavings + waitForBonus - sweeps;
                // Just before money lands on a day: a bill due that day may come first, and that money isn't in yet.
                long Before(DateOnly day) => start - bills.Where(b => b.Due <= day).Sum(b => b.Cents)
                                             + landing.Where(x => x.Date < day).Sum(x => x.Cents) - (firstBonus < day ? waitForBonus : 0);
                var atPayday = start - bills.Sum(b => b.Cents) - held - arrivedForLater - chargedSince
                               + landing.Sum(x => x.Cents) - (firstBonus is null ? 0 : waitForBonus);
                return landing.Select(x => Before(x.Date)).Append(atPayday).Min();
            }
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
                Skipped: allSkipped, Parts: parts,
                // What has to wait (DECISIONS.md 308): the open parts' together.
                WaitCents: open.Sum(p => p.WaitCents), WaitFor: open.Select(p => p.WaitFor).FirstOrDefault(w => w is not null),
                WaitDate: open.Select(p => p.WaitDate).FirstOrDefault(w => w is not null), WaitName: open.Select(p => p.WaitName).FirstOrDefault(w => w is not null),
                SentCents: parts.Where(p => !p.Skipped).Sum(p => p.SentCents));
        }).ToList();
}
