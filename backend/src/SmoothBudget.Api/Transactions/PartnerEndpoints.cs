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
    }
}
