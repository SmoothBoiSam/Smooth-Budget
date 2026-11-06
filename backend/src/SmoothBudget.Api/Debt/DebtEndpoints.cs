using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Debt;

namespace SmoothBudget.Api.Debt;

/// <summary>SPEC 4.4 and section 9: <c>GET /debt</c> (summary and projection; <c>?extra=</c> and <c>?strategy=</c> for the what-if slider).</summary>
public static class DebtEndpoints
{
    public const int MaxAprBps = 10_000; // 100%
    public const long MaxCents = 100_000_000_00; // $100M: anything bigger is a typo

    public static void MapDebtEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/debt").RequireAuthorization();

        g.MapGet("", async (long? extra, DebtStrategy? strategy, HttpContext ctx, DebtService debts, CancellationToken ct) =>
            extra is < 0 or > MaxCents
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["extra"] = ["Use 0 or more."] })
                : Results.Ok(await debts.SummaryAsync(ctx.User.HouseholdId(), extra, strategy, ct)));

        // A loan or other debt entered by hand (cards are added automatically from their accounts).
        g.MapPost("", async (DebtRequest req, HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (Validate(req, manual: true) is { } problem) return problem;
            var balance = req.BalanceCents!.Value;
            var d = new DebtAccount
            {
                Id = Guid.NewGuid(), HouseholdId = ctx.User.HouseholdId(), Name = req.Name.Trim(), BalanceCents = balance,
                AprBps = req.AprBps, MinimumPaymentCents = req.MinimumCents, DueDay = req.DueDay, CreditLimitCents = req.CreditLimitCents,
                StartingBalanceCents = req.StartingBalanceCents ?? balance, StartedOn = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime),
                CreatedUtc = clock.GetUtcNow().UtcDateTime,
            };
            db.DebtAccounts.Add(d);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { d.Id });
        });

        g.MapPut("/{id:guid}", async (Guid id, DebtRequest req, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var d = await db.DebtAccounts.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == ctx.User.HouseholdId(), ct);
            if (d is null) return Results.NotFound();
            var bankBalance = d.AccountId is not null && await db.Accounts.AnyAsync(a => a.Id == d.AccountId && a.PlaidAccountId != null, ct);
            if (Validate(req, manual: !bankBalance) is { } problem) return problem;
            if (d.AprBps != req.AprBps || d.MinimumPaymentCents != req.MinimumCents || d.DueDay != req.DueDay)
                d.FromBank = false; // yours now: the bank won't overwrite it
            d.Name = req.Name.Trim();
            d.AprBps = req.AprBps;
            d.MinimumPaymentCents = req.MinimumCents;
            d.DueDay = req.DueDay;
            d.IsHidden = req.IsHidden;
            if (!bankBalance)
            {
                if (d.StartingBalanceCents == 0 && req.StartingBalanceCents is null)
                    d.StartingBalanceCents = req.BalanceCents!.Value; // first time you enter it: that's where you started
                d.BalanceCents = req.BalanceCents!.Value; // a bank-linked card's balance comes from its account
            }
            if (req.CreditLimitCents is not null || d.AccountId is null)
                d.CreditLimitCents = req.CreditLimitCents;
            if (req.StartingBalanceCents is { } start)
                d.StartingBalanceCents = start;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Cards can only be hidden (they come back with their account); loans can be removed.
        g.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var d = await db.DebtAccounts.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == ctx.User.HouseholdId(), ct);
            if (d is null) return Results.NotFound();
            if (d.AccountId is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["id"] = ["A card follows its account. Hide it instead."] });
            db.DebtAccounts.Remove(d);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPut("/settings", async (DebtSettingsRequest req, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            if (!Enum.IsDefined(req.Strategy) || req.ExtraCents is < 0 or > MaxCents)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["extraCents"] = ["Use 0 or more."] });
            var h = await db.Households.SingleAsync(x => x.Id == ctx.User.HouseholdId(), ct);
            h.DebtStrategy = req.Strategy;
            h.ExtraDebtPaymentCents = req.ExtraCents;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/from-bank", async (HttpContext ctx, DebtService debts, CancellationToken ct) =>
            await debts.FromBankAsync(ctx.User.HouseholdId(), ct));
    }

    private static IResult? Validate(DebtRequest r, bool manual)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 120) errors["name"] = ["Give it a name (up to 120 characters)."];
        if (manual && r.BalanceCents is null or < 0 or > MaxCents) errors["balanceCents"] = ["Enter what you owe."];
        if (r.AprBps is < 0 or > MaxAprBps) errors["aprBps"] = ["APR is between 0% and 100%."];
        if (r.MinimumCents is < 0 or > MaxCents) errors["minimumCents"] = ["Enter the minimum payment in dollars."];
        if (r.DueDay is < 1 or > 31) errors["dueDay"] = ["Pick a day from 1 to 31."];
        if (r.CreditLimitCents is < 0 or > MaxCents) errors["creditLimitCents"] = ["Enter the limit in dollars."];
        if (r.StartingBalanceCents is < 0 or > MaxCents) errors["startingBalanceCents"] = ["Enter what you owed when you started."];
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }
}
