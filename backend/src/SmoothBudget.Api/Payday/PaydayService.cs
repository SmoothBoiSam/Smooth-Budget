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
public record PlanItemDto(string Key, string Kind, string Label, long Cents, string Destination, string Why, bool Done);
public record PlanDto(IReadOnlyList<PlanItemDto> Items, long ShortCents);

/// <param name="HasSchedule">False until that month's schedule is imported: no amounts yet.</param>
public record CheckDto(
    DateOnly PayDate, DateOnly PeriodStart, DateOnly PeriodEnd, bool HasSchedule,
    long GrossCents, long TaxableCents, long NetCents, long LowNetCents, long HighNetCents,
    bool LowConfidence, IReadOnlyList<string> Reasons, IReadOnlyList<string> Notes,
    IReadOnlyList<LineDto> Lines, IReadOnlyList<AmountDto> Taxes, IReadOnlyList<AmountDto> Deductions, PlanDto? Plan);

/// <param name="Source">"deposit", "stub" or null (not landed yet).</param>
public record HistoryDto(DateOnly PayDate, long PredictedNetCents, long LowNetCents, long HighNetCents, long? ActualNetCents, long? VarianceCents, string? Source);

public record ProfileDto(int YearsOfService, long RateCents, bool FullTime, bool HolidayPay, int FlatNetPercent);

public record PaydayDto(
    ProfileDto Profile, int StubCount, bool ModelLearned, bool ModelEdited, IReadOnlyList<CheckDto> Checks, IReadOnlyList<HistoryDto> History,
    IReadOnlyList<string> ScheduleMonths);

/// <summary>Everything the pay engine needs for one household.</summary>
public sealed record PayContext(
    PayRules Rules, PayProfile Profile, PayProfileInput ProfileInput, Dictionary<(int Year, int Month), ScheduleMonthInput> Months,
    List<(Guid Id, PayStubData Stub)> Stubs, WithholdingModel? LearnedModel, WithholdingModel? Model, DateOnly Today, Household Household, BudgetMonthCalendar Calendar);

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
        return new PayContext(rules, profile, new PayProfileInput(rules.RateFor(profile.YearsOfService), profile.FullTime, profile.HolidayPay),
            map, stubs, learned, edited ?? learned, today, household, calendar);
    }

    public static ScheduleMonthInput Input(ScheduleMonth m) => new(
        JsonSerializer.Deserialize<ParsedSchedule>(m.Json, Json)!, m.Reserve, Keys(m.OvertimeKeys), Keys(m.PaidEntryKeys));

    public static HashSet<string> Keys(string stored) => stored.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    public static ForecastCheck Forecast(PayContext c, PayPeriod p, long typicalMiss) =>
        PayForecast.Check(PayEngine.Gross(p, c.Months.GetValueOrDefault((p.Start.Year, p.Start.Month)), c.ProfileInput, c.Rules),
            c.Model, c.ProfileInput.RateCentsPerHour, c.Profile.FlatNetPercent, typicalMiss);

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
        var periods = PayCalendar.From(c.Today, c.Rules).Take(ChecksShown).ToList();
        var forecasts = periods.Select(p => Forecast(c, p, miss)).ToList();

        // Keep each prediction as it stands until payday; after that it's frozen for predicted vs actual.
        var rows = await db.PayPeriodForecasts.Where(f => f.HouseholdId == householdId).ToListAsync(ct);
        foreach (var f in forecasts.Where(f => f.HasSchedule))
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

        var plans = await PlansAsync(householdId, c, forecasts, ct);
        var checks = forecasts.Select((f, i) => new CheckDto(
            f.Period.PayDate, f.Period.Start, f.Period.End, f.HasSchedule, f.GrossCents, f.TaxableCents, f.NetCents, f.LowNetCents, f.HighNetCents,
            f.LowConfidence, f.Reasons, f.Notes, f.Lines.Select(l => new LineDto(l.Name, l.Cents, l.Note)).ToList(),
            f.Taxes.Select(t => new AmountDto(t.Name, t.CurrentCents)).ToList(), f.Deductions.Select(d => new AmountDto(d.Name, d.CurrentCents)).ToList(),
            plans[i])).ToList();

        var history = rows.Where(r => r.PayDate < c.Today || r.ActualNetCents is not null).OrderByDescending(r => r.PayDate).Take(12)
            .Select(r => new HistoryDto(r.PayDate, r.PredictedNetCents, r.LowNetCents, r.HighNetCents, r.ActualNetCents,
                r.ActualNetCents - r.PredictedNetCents, r.ActualSource)).ToList();

        return new PaydayDto(ProfileOf(c), c.Stubs.Count, c.LearnedModel is not null, c.Profile.ModelJson is not null, checks, history,
            c.Months.Keys.OrderByDescending(k => k).Select(k => $"{k.Year}-{k.Month:00}").ToList());
    }

    public static ProfileDto ProfileOf(PayContext c) =>
        new(c.Profile.YearsOfService, c.ProfileInput.RateCentsPerHour, c.Profile.FullTime, c.Profile.HolidayPay, c.Profile.FlatNetPercent);

    private async Task<long> TypicalMissAsync(Guid householdId, CancellationToken ct) =>
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

    /// <summary>The payday plan for each check (SPEC 4.6).</summary>
    private async Task<List<PlanDto?>> PlansAsync(Guid householdId, PayContext c, List<ForecastCheck> checks, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.HouseholdId == householdId && a.IncludedInBudget).ToListAsync(ct);
        var checking = accounts.Where(a => a.Type == AccountType.Checking).OrderBy(a => a.CreatedUtc).Select(a => a.Name).FirstOrDefault() ?? "Checking";
        var savingsAccount = accounts.Where(a => a.Type == AccountType.Savings).OrderBy(a => a.CreatedUtc).Select(a => a.Name).FirstOrDefault() ?? "savings";
        var cards = accounts.Where(a => a.Type == AccountType.CreditCard).Select(a => a.Name).ToHashSet();
        var categories = await db.Categories.AsNoTracking().Where(x => x.HouseholdId == householdId && !x.IsHidden).ToListAsync(ct);
        var debtMonthly = categories.Where(x => x.Type == CategoryType.DebtPayment).Sum(x => x.MonthlyLimitCents);
        var savingsMonthly = categories.Where(x => x.Type == CategoryType.SavingsTransfer).Sum(x => x.MonthlyLimitCents);
        var debts = await db.DebtAccounts.AsNoTracking().Where(d => d.HouseholdId == householdId && !d.IsHidden && d.BalanceCents > 0).ToListAsync(ct);
        var target = (c.Household.DebtStrategy == DebtStrategy.Snowball
            ? debts.OrderBy(d => d.BalanceCents)
            : debts.OrderByDescending(d => d.AprBps ?? 0).ThenByDescending(d => d.BalanceCents)).Select(d => d.Name).FirstOrDefault() ?? "your card";
        var items = (await recurring.SummaryAsync(householdId, ct)).Items
            .Where(i => i.Status is not ("possiblyCanceled" or "canceled") && !cards.Contains(i.AccountName)).ToList();
        var sweeps = await db.SweepTasks.AsNoTracking().Where(s => s.HouseholdId == householdId && s.DoneUtc == null).ToListAsync(ct);
        var arrangements = await db.PartnerArrangements.AsNoTracking().Where(a => a.HouseholdId == householdId).ToListAsync(ct);
        var done = await db.PaydayChecklistItems.AsNoTracking().Where(x => x.HouseholdId == householdId).Select(x => new { x.PayDate, x.Key }).ToListAsync(ct);

        var result = new List<PlanDto?>();
        for (var i = 0; i < checks.Count; i++)
        {
            var f = checks[i];
            if (!f.HasSchedule) { result.Add(null); continue; }
            var pay = f.Period.PayDate;
            var next = PayCalendar.PeriodContaining(f.Period.End.AddDays(1), c.Rules).PayDate;
            var month = c.Calendar.MonthContaining(pay);
            var inMonth = PayCalendar.From(month.Start, c.Rules).TakeWhile(p => p.PayDate <= month.End).Select(p => p.PayDate).ToList();
            var index = Math.Max(0, inMonth.IndexOf(pay));
            var share = $"{(inMonth.Count == 2 ? "Half" : $"1/{inMonth.Count}")} of the month's";

            // A bill your partner's fixed contribution goes toward (rent) only needs your share from this check.
            var offsetUsed = new HashSet<Guid>();
            var bills = items.Where(b => b.NextExpected >= pay && b.NextExpected < next).OrderBy(b => b.NextExpected).Select(b =>
            {
                var covered = b.CategoryId is { } cat && offsetUsed.Add(cat)
                    ? arrangements.Where(a => a.CategoryId == cat && a.StartsOn <= b.NextExpected).Sum(a => a.AmountCents) : 0;
                var mine = Math.Max(0, b.AmountCents - covered);
                return new PlanBill($"bill:{b.Key}:{b.NextExpected:yyyyMMdd}", b.Name, mine, b.NextExpected, $"Keep in {b.AccountName}",
                    covered > 0 ? $"{c.Household.PartnerName ?? "Your partner"} covers {PayEngine.Dollars(Math.Min(covered, b.AmountCents))} of {PayEngine.Dollars(b.AmountCents)}, so this is your share." : null);
            }).ToList();
            var transfers = new List<PlanTransfer>
            {
                new("debt", "Card payment", PaydayPlan.Share(debtMonthly, inMonth.Count, index), target,
                    inMonth.Count > 1 ? $"{share} {PayEngine.Dollars(debtMonthly)} card payment in your budget." : $"Your budget's {PayEngine.Dollars(debtMonthly)} card payment."),
            };
            if (i == 0)
                transfers.AddRange(sweeps.Select(s => new PlanTransfer($"sweep:{s.Id}", "Last month's leftover", s.AmountCents, s.Destination, "The rollover sweep from your last closed month.")));
            transfers.Add(new("savings", "Savings", PaydayPlan.Share(savingsMonthly, inMonth.Count, index), savingsAccount,
                inMonth.Count > 1 ? $"{share} {PayEngine.Dollars(savingsMonthly)} savings in your budget." : $"Your budget's {PayEngine.Dollars(savingsMonthly)} savings."));

            var plan = PaydayPlan.Build(f.NetCents, bills, transfers, checking);
            result.Add(new PlanDto(plan.Items.Select(x => new PlanItemDto(x.Key, x.Kind, x.Label, x.Cents, x.Destination, x.Why,
                done.Any(d => d.PayDate == pay && d.Key == x.Key))).ToList(), plan.ShortCents));
        }
        return result;
    }
}
