using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Pay;

namespace SmoothBudget.Api.Payday;

public record DayDto(DateOnly Date, string Report, string Release, int CreditMinutes, int DutyMinutes, int Legs, string Route);
public record TripDto(string Key, string Code, DateOnly Start, DateOnly End, int CreditMinutes, int TafbMinutes, int RevenueLegs, bool Overtime, bool Dropped,
    long PayCents, long CreditPayCents, long BoardingCents, long PerDiemCents, IReadOnlyList<DayDto> Days);
public record EntryDto(string Key, string Code, DateTime Start, DateTime End, int CreditMinutes, bool Paid);

/// <param name="ShortfallMinutes">Below the line guarantee (information only: no top-up is assumed).</param>
/// <param name="OverrideMinutes">Above 87 hours: earns the override.</param>
public record ScheduleMonthDto(int Year, int Month, string Source, bool Reserve, DateTime? ImportedUtc, int CreditMinutes, int ShortfallMinutes,
    int OverrideMinutes, IReadOnlyList<TripDto> Trips, IReadOnlyList<EntryDto> Entries, IReadOnlyList<string> Checks, IReadOnlyList<string> Warnings);

/// <param name="Dropped">Trips you dropped after importing (left out of pay until the next import).</param>
public record ScheduleChoicesRequest(bool Reserve, List<string> Overtime, List<string> PaidEntries, List<string>? Dropped = null);
/// <param name="HireDate">When set, years of service follow from it and YearsOfService is ignored.</param>
public record ProfileRequest(int YearsOfService, bool FullTime, bool HolidayPay, int FlatNetPercent, DateOnly? HireDate = null);
/// <param name="Skipped">Set to skip (or un-skip) "Pay off what you charged" for that check (DECISIONS.md 195).</param>
public record ChecklistRequest(bool Done, bool? Skipped = null);

/// <param name="Prediction">What the app had predicted for this check, if it had.</param>
public record StubReviewDto(PayStubData Stub, IReadOnlyList<string> Checks, IReadOnlyList<string> Problems, bool Replaces);
public record StubSavedDto(Guid Id, long? PredictedNetCents, long ActualNetCents, long? VarianceCents);
public record StubListItemDto(Guid Id, DateOnly PayDate, DateOnly PeriodStart, DateOnly PeriodEnd, long GrossCents, long NetCents);

public record ModelDto(WithholdingModel? Model, bool Edited, IReadOnlyList<BackTest> BackTests, int StubCount);

/// <summary>SPEC 6.3: <c>/payday/forecast</c> and <c>/payday/plan</c> (one response), schedule, pay stubs, rules and profile.</summary>
public static class PaydayEndpoints
{
    public const long MaxFileBytes = 5 * 1024 * 1024;

    public static void MapPaydayEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/payday").RequireAuthorization();

        g.MapGet("", async (HttpContext ctx, PaydayService svc, Budget.AutoBudgetService auto, CancellationToken ct) =>
        {
            await auto.EnsureFreshAsync(ctx.User.HouseholdId(), ct); // the plan splits this month's budget across checks
            return await svc.GetAsync(ctx.User.HouseholdId(), ct);
        });

        // The Income page (DECISIONS.md 254): a year of income by month, and every income deposit since.
        g.MapGet("/income", async (HttpContext ctx, IncomeHistory income, CancellationToken ct) =>
            await income.SummaryAsync(ctx.User.HouseholdId(), ct));
        g.MapGet("/income/transactions", async (HttpContext ctx, IncomeHistory income, TimeProvider clock, CancellationToken ct) =>
            await income.ItemsAsync(ctx.User.HouseholdId(), DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddYears(-2), ct));

        // Income you expect apart from your paychecks, like a bonus (DECISIONS.md 256).
        g.MapGet("/expected", async (HttpContext ctx, AppDbContext db, Budget.BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var (_, today, _) = await months.ContextAsync(householdId, ct);
            return await ExpectedIncomes.ListAsync(db, householdId, today, ct);
        });
        g.MapPost("/expected", async (ExpectedIncomeRequest req, HttpContext ctx, AppDbContext db, PaydayService payday, Budget.BudgetMonthService months,
            TimeProvider clock, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var (_, today, _) = await months.ContextAsync(householdId, ct);
            var name = string.IsNullOrWhiteSpace(req.Name) ? "Bonus" : req.Name.Trim();
            if (name.Length > 100 || req.AmountCents <= 0 || req.AmountCents > 1_000_000_00)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = ["Enter what you expect to take home, more than $0."] });
            if (req.ExpectedDate < today.AddDays(-ExpectedIncomes.LateDays) || req.ExpectedDate > today.AddYears(1))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["date"] = ["Pick a day from the last week or so to a year from now."] });
            db.ExpectedIncomes.Add(new ExpectedIncome
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, Name = name, AmountCents = req.AmountCents, ExpectedDate = req.ExpectedDate,
                CreatedUtc = clock.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync(ct);
            await payday.MatchDepositsAsync(householdId, ct); // it may have landed already
            return Results.Ok(await ExpectedIncomes.ListAsync(db, householdId, today, ct));
        });
        g.MapDelete("/expected/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, Budget.BudgetMonthService months, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var row = await db.ExpectedIncomes.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == householdId, ct);
            if (row is null) return Results.NotFound();
            db.ExpectedIncomes.Remove(row);
            await db.SaveChangesAsync(ct);
            var (_, today, _) = await months.ContextAsync(householdId, ct);
            return Results.Ok(await ExpectedIncomes.ListAsync(db, householdId, today, ct));
        });

        g.MapPut("/plan/{payDate}/{key}", async (DateOnly payDate, string key, ChecklistRequest req, HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            // A check's tasks open on its payday (DECISIONS.md 221): before then the money isn't there yet. Unticking is fine.
            if (req.Done || req.Skipped == true)
            {
                var zone = await db.Households.Where(h => h.Id == householdId).Select(h => h.TimeZoneId).SingleAsync(ct);
                if (payDate > SmoothBudget.Api.Home.HomeSummaryService.TodayIn(zone, clock))
                    return Results.Problem($"These open on payday, {payDate:MMM d}.", statusCode: StatusCodes.Status409Conflict);
            }
            if (req.Skipped is { } skip)
            {
                if (!key.StartsWith("cardspend:", StringComparison.Ordinal))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["skipped"] = ["Only paying off what you charged can be skipped."] });
                var skipKey = PaydayService.SkipPrefix + key[..Math.Min(key.Length, 190)];
                var skipRow = await db.PaydayChecklistItems.SingleOrDefaultAsync(x => x.HouseholdId == householdId && x.PayDate == payDate && x.Key == skipKey, ct);
                if (skip && skipRow is null)
                    db.PaydayChecklistItems.Add(new PaydayChecklistItem { Id = Guid.NewGuid(), HouseholdId = householdId, PayDate = payDate, Key = skipKey, CreatedUtc = clock.GetUtcNow().UtcDateTime });
                else if (!skip && skipRow is not null)
                    db.PaydayChecklistItems.Remove(skipRow);
                if (skip && await db.PaydayChecklistItems.SingleOrDefaultAsync(x => x.HouseholdId == householdId && x.PayDate == payDate && x.Key == key, ct) is { } doneRow)
                    db.PaydayChecklistItems.Remove(doneRow);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            }
            var row = await db.PaydayChecklistItems.SingleOrDefaultAsync(x => x.HouseholdId == householdId && x.PayDate == payDate && x.Key == key, ct);
            if (req.Done && row is null)
                db.PaydayChecklistItems.Add(new PaydayChecklistItem { Id = Guid.NewGuid(), HouseholdId = householdId, PayDate = payDate, Key = key[..Math.Min(key.Length, 200)], CreatedUtc = clock.GetUtcNow().UtcDateTime });
            else if (!req.Done && row is not null)
                db.PaydayChecklistItems.Remove(row);
            // The leftover sweep is the same thing as the "send $X to …" item on Home: checking one marks the other.
            if (key.StartsWith("sweep:", StringComparison.Ordinal) && Guid.TryParse(key[6..], out var sweepId)
                && await db.SweepTasks.SingleOrDefaultAsync(s => s.Id == sweepId && s.HouseholdId == householdId, ct) is { } sweep)
                sweep.DoneUtc = req.Done ? clock.GetUtcNow().UtcDateTime : null;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Profile and rules.
        g.MapGet("/profile", async (HttpContext ctx, PaydayService svc, CancellationToken ct) => PaydayService.ProfileOf(await svc.ContextAsync(ctx.User.HouseholdId(), ct)));
        g.MapPut("/profile", async (ProfileRequest req, HttpContext ctx, PaydayService svc, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (req.YearsOfService is < 0 or > 60 || req.FlatNetPercent is < 1 or > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["profile"] = ["Years of service is 0 to 60, and the flat estimate 1 to 100%."] });
            if (req.HireDate is { } hd && (hd > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(1) || hd.Year < 1960))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["hireDate"] = ["Enter your InFlight date of hire."] });
            var p = await svc.ProfileAsync(ctx.User.HouseholdId(), ct);
            (p.YearsOfService, p.FullTime, p.HolidayPay, p.FlatNetPercent, p.UpdatedUtc) = (req.YearsOfService, req.FullTime, req.HolidayPay, req.FlatNetPercent, clock.GetUtcNow().UtcDateTime);
            p.HireDate = req.HireDate;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapGet("/rules", async (HttpContext ctx, PaydayService svc, CancellationToken ct) =>
        {
            var p = await svc.ProfileAsync(ctx.User.HouseholdId(), ct);
            return Results.Json(new { rules = PaydayService.RulesOf(p), edited = p.RulesJson is not null }, PaydayService.Json);
        });
        g.MapPut("/rules", async (HttpContext ctx, PaydayService svc, AppDbContext db, CancellationToken ct) =>
        {
            PayRules? rules;
            try { rules = await JsonSerializer.DeserializeAsync<PayRules>(ctx.Request.Body, PaydayService.Json, ct); }
            catch (JsonException) { rules = null; }
            if (rules is null || RulesProblem(rules) is { } problem)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["rules"] = [rules is null ? "Couldn't read the rules." : RulesProblem(rules)!] });
            var p = await svc.ProfileAsync(ctx.User.HouseholdId(), ct);
            p.RulesJson = JsonSerializer.Serialize(rules, PaydayService.Json);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        g.MapDelete("/rules", async (HttpContext ctx, PaydayService svc, AppDbContext db, CancellationToken ct) =>
        {
            (await svc.ProfileAsync(ctx.User.HouseholdId(), ct)).RulesJson = null;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Schedule import (SPEC 5.2): preview the grouped trips, then confirm. Re-importing replaces the month.
        g.MapPost("/schedule", async (IFormFile file, [Microsoft.AspNetCore.Mvc.FromForm] bool? confirm, HttpContext ctx, PaydayService svc, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (file.Length is 0 or > MaxFileBytes) return Results.Problem("Choose a schedule file up to 5 MB.", statusCode: 400);
            string text;
            using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                text = await reader.ReadToEndAsync(ct);
            var householdId = ctx.User.HouseholdId();
            var c = await svc.ContextAsync(householdId, ct);
            ParsedSchedule parsed;
            try
            {
                parsed = file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Flight,", StringComparison.Ordinal)
                    ? ScheduleCsvParser.Parse(text, c.Rules, ScheduleCsvParser.FindZone)
                    : ScheduleTextParser.Parse(text, c.Rules);
            }
            catch (Exception ex) when (ex is ScheduleParseException or FormatException)
            {
                return Results.Problem(ex is ScheduleParseException ? ex.Message : "Some times or durations in the file couldn't be read.", statusCode: 400);
            }
            var existing = await db.ScheduleMonths.SingleOrDefaultAsync(m => m.HouseholdId == householdId && m.Year == parsed.Year && m.Month == parsed.Month, ct);
            if (confirm != true)
            {
                var keep = existing is null ? null : PaydayService.Input(existing);
                var preview = new ScheduleMonthInput(parsed, existing?.Reserve ?? false, keep?.OvertimeKeys ?? new HashSet<string>(), keep?.PaidEntryKeys ?? new HashSet<string>(), keep?.DroppedKeys);
                return Results.Ok(new { month = MonthDto(preview, null, c), replaces = existing is not null });
            }
            if (existing is null)
            {
                existing = new ScheduleMonth { Id = Guid.NewGuid(), HouseholdId = householdId, Year = parsed.Year, Month = parsed.Month, Source = parsed.Source, Json = "" };
                db.ScheduleMonths.Add(existing);
            }
            // Your overtime and paid-entry choices survive a re-import when the same pairing or entry is still there.
            existing.OvertimeKeys = string.Join('\n', PaydayService.Keys(existing.OvertimeKeys).Where(k => parsed.Pairings.Any(p => p.Key == k)));
            existing.PaidEntryKeys = string.Join('\n', PaydayService.Keys(existing.PaidEntryKeys).Where(k => parsed.Entries.Any(e => e.Key == k)));
            existing.DroppedKeys = string.Join('\n', PaydayService.Keys(existing.DroppedKeys).Where(k => parsed.Pairings.Any(p => p.Key == k)));
            (existing.Source, existing.Json, existing.ImportedUtc) = (parsed.Source, JsonSerializer.Serialize(parsed, PaydayService.Json), clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { month = MonthDto(PaydayService.Input(existing), existing.ImportedUtc, c), replaces = false });
        }).DisableAntiforgery();

        g.MapGet("/schedule", async (HttpContext ctx, PaydayService svc, AppDbContext db, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var c = await svc.ContextAsync(householdId, ct);
            var rows = await db.ScheduleMonths.AsNoTracking().Where(m => m.HouseholdId == householdId).OrderByDescending(m => m.Year).ThenByDescending(m => m.Month).ToListAsync(ct);
            return rows.Select(r => MonthDto(PaydayService.Input(r), r.ImportedUtc, c)).ToList();
        });

        g.MapPut("/schedule/{year:int}/{month:int}", async (int year, int month, ScheduleChoicesRequest req, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var m = await db.ScheduleMonths.SingleOrDefaultAsync(x => x.HouseholdId == ctx.User.HouseholdId() && x.Year == year && x.Month == month, ct);
            if (m is null) return Results.NotFound();
            var parsed = PaydayService.Input(m).Schedule;
            m.Reserve = req.Reserve;
            m.OvertimeKeys = string.Join('\n', req.Overtime.Where(k => parsed.Pairings.Any(p => p.Key == k)).Distinct());
            m.PaidEntryKeys = string.Join('\n', req.PaidEntries.Where(k => parsed.Entries.Any(e => e.Key == k)).Distinct());
            m.DroppedKeys = string.Join('\n', (req.Dropped ?? []).Where(k => parsed.Pairings.Any(p => p.Key == k)).Distinct());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapDelete("/schedule/{year:int}/{month:int}", async (int year, int month, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var n = await db.ScheduleMonths.Where(x => x.HouseholdId == ctx.User.HouseholdId() && x.Year == year && x.Month == month).ExecuteDeleteAsync(ct);
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });

        // Pay stubs (SPEC 5.3): parse in memory and show a review; nothing is saved until you confirm. The file is never stored.
        g.MapPost("/paystubs", async (IFormFile file, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            if (file.Length is 0 or > MaxFileBytes) return Results.Problem("Choose a pay stub PDF up to 5 MB.", statusCode: 400);
            if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && file.ContentType != "application/pdf")
                return Results.Problem("Upload the pay stub as a PDF. Photos of stubs aren't supported yet.", statusCode: 400);
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            try
            {
                var parsed = PayStubParser.Parse(PdfWords.FirstPage(ms.ToArray()));
                var replaces = await db.PayStubs.AnyAsync(s => s.HouseholdId == ctx.User.HouseholdId() && s.PayDate == parsed.Stub.PayDate, ct);
                return Results.Json(new StubReviewDto(parsed.Stub, parsed.Checks, parsed.Problems, replaces), PaydayService.Json);
            }
            catch (PayStubParseException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
        }).DisableAntiforgery();

        g.MapPost("/paystubs/confirm", async (HttpContext ctx, PaydayService svc, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            PayStubData? stub;
            try { stub = await JsonSerializer.DeserializeAsync<PayStubData>(ctx.Request.Body, PaydayService.Json, ct); }
            catch (JsonException) { stub = null; }
            if (stub is null || stub.Earnings is null || stub.Taxes is null || stub.PreTax is null || stub.AfterTax is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["stub"] = ["Couldn't read the pay stub."] });
            var problems = PayStubParser.Validate(stub).Problems;
            if (problems.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["stub"] = problems.ToArray() });

            var householdId = ctx.User.HouseholdId();
            var row = await db.PayStubs.SingleOrDefaultAsync(s => s.HouseholdId == householdId && s.PayDate == stub.PayDate, ct);
            if (row is null)
            {
                row = new PayStub { Id = Guid.NewGuid(), HouseholdId = householdId, Json = "" };
                db.PayStubs.Add(row);
            }
            (row.PayDate, row.PeriodStart, row.PeriodEnd, row.GrossCents, row.NetCents, row.Json, row.CreatedUtc) =
                (stub.PayDate, stub.PeriodStart, stub.PeriodEnd, stub.GrossCents, stub.NetCents, JsonSerializer.Serialize(stub, PaydayService.Json), clock.GetUtcNow().UtcDateTime);

            // Compare with what was predicted for this check, store the variance, and recalibrate (a new stub refits the model).
            var forecast = await db.PayPeriodForecasts.SingleOrDefaultAsync(f => f.HouseholdId == householdId && f.PayDate == stub.PayDate, ct);
            if (forecast is not null) (forecast.ActualGrossCents, forecast.ActualNetCents, forecast.ActualSource) = (stub.GrossCents, stub.NetCents, "stub");
            (await svc.ProfileAsync(householdId, ct)).ModelJson = null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new StubSavedDto(row.Id, forecast?.PredictedNetCents, stub.NetCents, forecast is null ? null : stub.NetCents - forecast.PredictedNetCents));
        });

        g.MapGet("/paystubs", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await db.PayStubs.AsNoTracking().Where(s => s.HouseholdId == ctx.User.HouseholdId()).OrderByDescending(s => s.PayDate)
                .Select(s => new StubListItemDto(s.Id, s.PayDate, s.PeriodStart, s.PeriodEnd, s.GrossCents, s.NetCents)).ToListAsync(ct));

        g.MapDelete("/paystubs/{id:guid}", async (Guid id, HttpContext ctx, PaydayService svc, AppDbContext db, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            var n = await db.PayStubs.Where(s => s.Id == id && s.HouseholdId == householdId).ExecuteDeleteAsync(ct);
            if (n == 0) return Results.NotFound();
            (await svc.ProfileAsync(householdId, ct)).ModelJson = null;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // The withholding model: learned from the stubs, visible and editable (SPEC 5.3). A new stub refits it.
        g.MapGet("/model", async (HttpContext ctx, PaydayService svc, CancellationToken ct) =>
        {
            var c = await svc.ContextAsync(ctx.User.HouseholdId(), ct);
            var stubs = c.Stubs.Select(s => s.Stub).ToList();
            return Results.Json(new ModelDto(c.Model, c.Profile.ModelJson is not null, c.Model is null ? [] : Withholding.Test(c.Model, stubs), stubs.Count), PaydayService.Json);
        });
        g.MapPut("/model", async (HttpContext ctx, PaydayService svc, AppDbContext db, CancellationToken ct) =>
        {
            WithholdingModel? m;
            try { m = await JsonSerializer.DeserializeAsync<WithholdingModel>(ctx.Request.Body, PaydayService.Json, ct); }
            catch (JsonException) { m = null; }
            if (m is null || m.Taxes is null || m.NonTaxable is null || m.PreTax is null || m.AfterTax is null || m.RecurringEarnings is null || m.LeftOut is null || m.Notes is null
                || m.PtoHoursPerCheck is < 0 or > 40 || m.Taxes.Any(t => t.Rate is < 0 or > 1))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["model"] = ["Couldn't read the model (rates are 0 to 100%)."] });
            (await svc.ProfileAsync(ctx.User.HouseholdId(), ct)).ModelJson = JsonSerializer.Serialize(m, PaydayService.Json);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        g.MapDelete("/model", async (HttpContext ctx, PaydayService svc, AppDbContext db, CancellationToken ct) =>
        {
            (await svc.ProfileAsync(ctx.User.HouseholdId(), ct)).ModelJson = null;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static string? RulesProblem(PayRules r)
    {
        if (r.Rates is null || r.Rates.Count == 0 || r.Rates.Any(x => x.CentsPerHour is <= 0 or > 1_000_00 || x.Years < 0)) return "Add at least one hourly rate.";
        if (r.Airports is null || r.Holidays is null || r.Lines is null) return "The rules are incomplete.";
        if (r.Airports.Any(a => ScheduleCsvParser.FindZone(a.TimeZoneId) is null)) return $"Unknown time zone: {r.Airports.First(a => ScheduleCsvParser.FindZone(a.TimeZoneId) is null).TimeZoneId}.";
        if (r.MinDayCreditMinutes < 0 || r.DutyHoursPerCreditHour <= 0 || r.TafbHoursPerCreditHour <= 0 || r.PerDiemCentsPerHour < 0) return "Credit rules must be positive.";
        if (r.FirstHalfPayDay is < 16 or > 28 || r.SecondHalfPayDay is < 1 or > 15) return "Pay days: the first half is paid between the 16th and 28th, the second between the 1st and 15th.";
        return null;
    }

    private static ScheduleMonthDto MonthDto(ScheduleMonthInput m, DateTime? imported, PayContext c)
    {
        var s = m.Schedule;
        var credit = PayEngine.MonthCreditMinutes(m);
        var guarantee = (c.Profile.FullTime ? c.Rules.LineGuaranteeHours : c.Rules.PartTimeGuaranteeHours) * 60;
        return new ScheduleMonthDto(s.Year, s.Month, s.Source, m.Reserve, imported, credit, m.Reserve ? 0 : Math.Max(0, guarantee - credit),
            Math.Max(0, credit - c.Rules.OverrideAboveHours * 60),
            s.Pairings.Select(p =>
            {
                var pay = PayEngine.Trip(p, m.OvertimeKeys.Contains(p.Key), c.InputOn(DateOnly.FromDateTime(p.Start)), c.Rules);
                return new TripDto(p.Key, p.Code, DateOnly.FromDateTime(p.Start), p.Days[^1].Date, p.CreditMinutes, p.TafbMinutes, p.RevenueLegs,
                    m.OvertimeKeys.Contains(p.Key), m.DroppedKeys?.Contains(p.Key) == true, pay.TotalCents, pay.CreditCents + pay.OvertimeCents, pay.BoardingCents, pay.PerDiemCents,
                    p.Days.Select(d => new DayDto(d.Date, d.Report.ToString("HH:mm"), d.Release.ToString("HH:mm"), d.CreditMinutes, d.DutyMinutes, d.Legs.Count,
                        string.Join("–", d.Legs.Select(l => l.Origin).Append(d.Legs.LastOrDefault()?.Destination ?? "")))).ToList());
            }).ToList(),
            s.Entries.Select(e => new EntryDto(e.Key, e.Code, e.Start, e.End, e.CreditMinutes, m.PaidEntryKeys.Contains(e.Key))).ToList(),
            s.Checks, s.Warnings);
    }
}
