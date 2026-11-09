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
public record PlanItemDto(string Key, string Kind, string Label, long Cents, string Destination, string Why, bool Done,
    bool Auto = false, string? Status = null, IReadOnlyList<PlanBillDto>? Bills = null, long ShortCents = 0);
public record PlanBillDto(string Name, long Cents, DateOnly Due);
public record PlanDto(IReadOnlyList<PlanItemDto> Items, long ShortCents);

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

public record PaydayDto(
    ProfileDto Profile, int StubCount, bool ModelLearned, bool ModelEdited, IReadOnlyList<CheckDto> Checks, IReadOnlyList<HistoryDto> History,
    IReadOnlyList<string> ScheduleMonths);

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
            c.Model, profile.RateCentsPerHour, c.Profile.FlatNetPercent, typicalMiss);
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
            parts.Add($"{p.PayDate:MMM d} {PayEngine.Dollars(cents)} ({how})");
        }
        return parts.Count == 0 ? null : (total, $"Your paychecks this budget month: {string.Join(", ", parts)}.");
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

    public async Task<PaydayDto> GetAsync(Guid householdId, CancellationToken ct)
    {
        var c = await ContextAsync(householdId, ct);
        await MatchActualsAsync(householdId, c, ct);
        var miss = await TypicalMissAsync(householdId, ct);
        // The check you're living on now (the latest one paid) and the next few.
        var current = PayCalendar.From(c.Today.AddDays(-45), c.Rules).TakeWhile(p => p.PayDate <= c.Today).LastOrDefault();
        var periods = PayCalendar.From(c.Today.AddDays(1), c.Rules).Take(ChecksShown).ToList();
        if (current is not null) periods.Insert(0, current);
        var forecasts = periods.Select(p => Forecast(c, p, miss)).ToList();

        // Keep each prediction as it stands until payday; after that it's frozen for predicted vs actual.
        var rows = await db.PayPeriodForecasts.Where(f => f.HouseholdId == householdId).ToListAsync(ct);
        foreach (var f in forecasts.Where(f => f.HasSchedule && f.Period.PayDate >= c.Today))
        {
            var row = rows.FirstOrDefault(r => r.PayDate == f.Period.PayDate);
            if (row is null)
            {
                row = new PayPeriodForecast { Id = Guid.NewGuid(), HouseholdId = householdId, PayDate = f.Period.PayDate };
                db.PayPeriodForecasts.Add(row);
                rows.Add(row);
            }
            (row.PeriodStart, row.PeriodEnd, row.PredictedGrossCents, row.PredictedNetCents, row.LowNetCents, row.HighNetCents, row.UpdatedUtc) =
                (f.Period.Start, f.Period.End, f.GrossCents, f.NetCents, f.LowNetCents, f.HighNetCents, clock.GetUtcNow().UtcDateTime);
        }
        await db.SaveChangesAsync(ct);

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
            c.Months.Keys.OrderByDescending(k => k).Select(k => $"{k.Year}-{k.Month:00}").ToList());
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
        var debtMonthly = categories.Where(x => x.Type == CategoryType.DebtPayment).Sum(x => x.MonthlyLimitCents)
                          + (debts.Count > 0 ? categories.Where(x => CardInterest.IsInterestCategory(x.Name, x.Type)).Sum(x => x.MonthlyLimitCents) : 0);
        var savingsMonthly = categories.Where(x => x.Type == CategoryType.SavingsTransfer).Sum(x => x.MonthlyLimitCents);
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
        var arrangements = await db.PartnerArrangements.AsNoTracking().Where(a => a.HouseholdId == householdId).ToListAsync(ct);
        var done = await db.PaydayChecklistItems.AsNoTracking().Where(x => x.HouseholdId == householdId).Select(x => new { x.PayDate, x.Key }).ToListAsync(ct);
        // What you put on each card lately (purchases less refunds, not payments): it already counts in its spending or bill
        // category, so the plan pays it off from the spending money left in checking, or the card would grow (DECISIONS.md 179).
        var cardAccounts = accounts.Where(a => a.Type == AccountType.CreditCard).ToList();
        var cardIds = cardAccounts.Select(a => a.Id).ToList();
        var since = c.Today.AddDays(-62);
        var charged = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && cardIds.Contains(t.AccountId) && t.Date >= since && !t.IsTransfer && t.Kind != TransactionKind.CardPayment)
            .Select(t => new { t.AccountId, t.Date, t.AmountCents }).ToListAsync(ct);
        DateOnly PaydayBefore(DateOnly pay) =>
            PayCalendar.From(pay.AddDays(-45), c.Rules).TakeWhile(p => p.PayDate < pay).Select(p => p.PayDate).LastOrDefault(pay.AddDays(-14));

        var result = new List<PlanDto?>();
        for (var i = 0; i < checks.Count; i++)
        {
            var f = checks[i];
            var pay = f.Period.PayDate;
            var landedNet = landed.TryGetValue(pay, out var l) ? l : (long?)null;
            if (!f.HasSchedule && landedNet is null) { result.Add(null); continue; }
            var current = pay <= c.Today;
            var next = PayCalendar.PeriodContaining(f.Period.End.AddDays(1), c.Rules).PayDate;
            var month = c.Calendar.MonthContaining(pay);
            var inMonth = PayCalendar.From(month.Start, c.Rules).TakeWhile(p => p.PayDate <= month.End).Select(p => p.PayDate).ToList();
            var index = Math.Max(0, inMonth.IndexOf(pay));
            var share = $"{(inMonth.Count == 2 ? "Half" : $"1/{inMonth.Count}")} of the month's";

            // Bills still to come before the next payday (for the current check: from today, since paid ones are done).
            // A bill your partner's fixed contribution goes toward (rent) still needs all of it in the account when it's
            // charged, since their money lands there too (DECISIONS.md 165); only your share comes out of this check.
            var from = current ? c.Today : pay;
            var offsetUsed = new HashSet<Guid>();
            // The name as it shows on their transfers ("ALEX RIVERA"): just the first name, in normal case.
            var partner = c.Household.PartnerName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } pn
                ? char.ToUpperInvariant(pn[0]) + pn[1..].ToLowerInvariant() : "Your partner";
            var bills = items.Where(b => b.NextExpected >= from && b.NextExpected < next).OrderBy(b => b.NextExpected).Select(b =>
            {
                var covered = b.CategoryId is { } cat && offsetUsed.Add(cat)
                    ? Math.Min(b.AmountCents, arrangements.Where(a => a.CategoryId == cat && a.StartsOn <= b.NextExpected).Sum(a => a.AmountCents)) : 0;
                return new PlanBill($"bill:{b.Key}", b.Name, b.AmountCents, b.NextExpected, b.AccountName,
                    covered > 0 ? $"{b.Name} counts in full: {partner} sends {PayEngine.Dollars(covered)} of it to this account, so {PayEngine.Dollars(b.AmountCents - covered)} is yours from this check." : null,
                    covered);
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

            var plan = PaydayPlan.Build(landedNet ?? f.NetCents, bills, transfers);

            // What happened since payday, for the check you're on now.
            var moves = current
                ? await db.Transactions.AsNoTracking()
                    .Where(t => t.HouseholdId == householdId && t.Date >= pay && t.Date < next && (t.Kind == TransactionKind.CardPayment || t.IsTransfer))
                    .Select(t => new { t.AmountCents, t.Kind, Type = t.Account.Type, t.CategoryId, t.AccountId }).ToListAsync(ct)
                : [];
            var cardPaid = moves.Where(m => m.Kind == TransactionKind.CardPayment && m.Type == AccountType.Checking && m.AmountCents > 0).Sum(m => m.AmountCents);
            // Per card: the payment as the card's own account shows it (a credit). Without those, the checking side for all.
            long PaidTo(Guid? accountId) => moves.Where(m => m.Type == AccountType.CreditCard && m.AccountId == accountId && m.AmountCents < 0).Sum(m => -m.AmountCents);
            var cardSideSeen = moves.Any(m => m.Type == AccountType.CreditCard && m.AmountCents < 0);
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
                                      + (x.FromOthersCents > 0 ? $" That counts the {PayEngine.Dollars(x.FromOthersCents)} {partner} sends, if it hasn't arrived yet." : ""));
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
                    case "sweep":
                        var task = sweeps.FirstOrDefault(s => x.Key == $"sweep:{s.Id}");
                        return task?.DoneUtc is not null ? (true, "Marked as sent.") : (false, null);
                    default:
                        return (false, null);
                }
            }

            result.Add(new PlanDto(plan.Items.Select(x =>
            {
                var (auto, status) = Status(x);
                var manual = done.Any(d => d.PayDate == pay && d.Key == x.Key);
                return new PlanItemDto(x.Key, x.Kind, x.Label, x.Cents, x.Destination, x.Why, manual || auto, auto, status,
                    x.Bills?.Select(b => new PlanBillDto(b.Name, b.Cents, b.Due)).ToList(), ShortBy(x));
            }).ToList(), plan.ShortCents));
        }
        return result;
    }
}
