using SmoothBudget.Api.Auth;

namespace SmoothBudget.Api.Bills;

/// <summary>SPEC section 9: <c>GET/PUT /recurring</c>, plus making a detected item its own bill line.</summary>
public static class RecurringEndpoints
{
    public static void MapRecurringEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/recurring").RequireAuthorization();

        g.MapGet("", async (HttpContext ctx, RecurringService svc, CancellationToken ct) =>
            await svc.SummaryAsync(ctx.User.HouseholdId(), ct));

        g.MapPut("", async (RecurringUpdateRequest req, HttpContext ctx, RecurringService svc, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(req.Key) || req.Key.Length > 200) errors["key"] = ["Unknown item."];
            if (req.Name is { Length: > 120 }) errors["name"] = ["Use up to 120 characters."];
            if (req.DueDay is < 1 or > 31) errors["dueDay"] = ["Pick a day from 1 to 31."];
            if (req.Kind is { } k && !Enum.IsDefined(k)) errors["kind"] = ["Bill or Subscription."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            return await svc.UpdateAsync(ctx.User.HouseholdId(), req, ct) ? Results.NoContent() : Results.NotFound();
        });

        g.MapPost("/track-as-bill", async (TrackAsBillRequest req, HttpContext ctx, RecurringService svc, CancellationToken ct) =>
            await svc.TrackAsBillAsync(ctx.User.HouseholdId(), req.Key, ct) is { } r
                ? Results.Ok(new { r.CategoryId, r.Name })
                : Results.NotFound());

        // File a bill under a different category (DECISIONS.md 146).
        g.MapPost("/category", async (MoveBillRequest req, HttpContext ctx, RecurringService svc, CancellationToken ct) =>
            await svc.MoveToCategoryAsync(ctx.User.HouseholdId(), req.Key ?? "", req.CategoryId, ct) is { } r
                ? Results.Ok(new { r.CategoryId, r.Name })
                : Results.NotFound());
    }
}
