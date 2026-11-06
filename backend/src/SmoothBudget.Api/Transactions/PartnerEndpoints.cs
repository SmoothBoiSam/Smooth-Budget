using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Transactions;

public record PartnerSettingsDto(string? PartnerName);
public record PartnerNameSuggestion(string Name, int Count, long TotalCents);

/// <summary>
/// The partner's name as it appears on her transfers (SPEC Appendix D: stored in Settings, never hard-coded).
/// Money in from that name is a partner contribution, kept apart from the owner's own income.
/// </summary>
public static class PartnerEndpoints
{
    public static void MapPartnerEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings/partner").RequireAuthorization();

        g.MapGet("", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            new PartnerSettingsDto(await db.Households.Where(h => h.Id == ctx.User.HouseholdId()).Select(h => h.PartnerName).SingleAsync(ct)));

        // Saving re-tags every incoming transfer: from that name it's partner money, otherwise your income.
        g.MapPut("", async (PartnerSettingsDto req, HttpContext ctx, AppDbContext db, CategorizationService categorization, CancellationToken ct) =>
        {
            var name = string.IsNullOrWhiteSpace(req.PartnerName) ? null : req.PartnerName.Trim();
            if (name is { Length: > 100 or < 2 })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["partnerName"] = ["Use 2 to 100 characters."] });
            var h = await db.Households.SingleAsync(x => x.Id == ctx.User.HouseholdId(), ct);
            h.PartnerName = name;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await categorization.RunAsync(h.Id, ct));
        });

        // Senders of incoming Zelle/Venmo-style transfers in the last year, most frequent first, to pick from.
        g.MapGet("/suggestions", async (HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var since = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddYears(-1);
            var incoming = await db.Transactions.AsNoTracking()
                .Where(t => t.HouseholdId == ctx.User.HouseholdId() && t.AmountCents < 0 && !t.IsTransfer && t.Date >= since
                            && (t.Kind == TransactionKind.Income || t.Kind == TransactionKind.PartnerContribution))
                .Select(t => new { t.Description, t.AmountCents })
                .ToListAsync(ct);
            return incoming
                .Select(t => (Name: PartnerDetector.SenderName(t.Description), t.AmountCents))
                .Where(x => x.Name is not null)
                .GroupBy(x => x.Name!)
                .Select(grp => new PartnerNameSuggestion(grp.Key, grp.Count(), grp.Sum(x => -x.AmountCents)))
                .OrderByDescending(s => s.Count).ThenByDescending(s => s.TotalCents)
                .Take(5)
                .ToList();
        });

        var p = app.MapGroup("/api/partner").RequireAuthorization();

        // Who owes whom from split purchases and paying back (SPEC 4.8), since the last time it was marked settled.
        p.MapGet("/settle-up", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await SettleUpAsync(db, ctx.User.HouseholdId(), ct));

        p.MapPost("/settle-up/settled", async (HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var h = await db.Households.SingleAsync(x => x.Id == ctx.User.HouseholdId(), ct);
            h.PartnerSettledThrough = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
            return await SettleUpAsync(db, h.Id, ct);
        });
    }

    public static async Task<SettleUpDto> SettleUpAsync(AppDbContext db, Guid householdId, CancellationToken ct)
    {
        var h = await db.Households.AsNoTracking().Where(x => x.Id == householdId).Select(x => new { x.PartnerName, x.PartnerSettledThrough }).SingleAsync(ct);
        var after = h.PartnerSettledThrough;
        var splits = await CategorizationService.SplitPurchasesAsync(db, householdId, after, ct);
        var transfers = await db.Transactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Kind == TransactionKind.PartnerSettleUp && (after == null || t.Date > after))
            .Select(t => new { t.Id, t.Date, t.AmountCents, t.Merchant, t.Kind })
            .ToListAsync(ct);
        var b = PartnerMoney.Balance(splits, transfers.Select(t => new PartnerTransfer(t.Id, t.Date, t.AmountCents, t.Kind)).ToList());

        var items = splits.Select(s => new SettleUpItemDto(s.Id, s.Date, s.Merchant, s.PartnerShareCents, "split"))
            .Concat(transfers.Select(t => new SettleUpItemDto(t.Id, t.Date, t.Merchant, t.AmountCents, t.AmountCents < 0 ? "paidByPartner" : "paidByYou")))
            .OrderByDescending(i => i.Date).Take(30).ToList();
        return new SettleUpDto(h.PartnerName, b.OwedToOwnerCents, b.SplitSharesCents, b.SettledByHerCents, b.SettledByOwnerCents, after, items);
    }
}

/// <param name="EffectCents">For "split": her share. For transfers: the amount (negative = she sent it).</param>
public record SettleUpItemDto(Guid TransactionId, DateOnly Date, string Merchant, long EffectCents, string Kind);

/// <param name="OwedToYouCents">Positive: your partner owes you. Negative: you owe her.</param>
public record SettleUpDto(
    string? PartnerName, long OwedToYouCents, long SplitSharesCents, long PaidBackByPartnerCents, long PaidBackByYouCents,
    DateOnly? SettledThrough, IReadOnlyList<SettleUpItemDto> Items);
