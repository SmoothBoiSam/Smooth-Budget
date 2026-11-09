using SmoothBudget.Api.Auth;

namespace SmoothBudget.Api.Shared;

/// <summary>Friends, and linking with their accounts (DECISIONS.md 205).</summary>
public static class SharedEndpoints
{
    public static void MapSharedEndpoints(this IEndpointRouteBuilder app)
    {
        // Who an invite is from, before signing up: no account needed, rate limited like sign-in.
        app.MapGet("/api/shared/invites/{token}", async (string token, SharedService shared, CancellationToken ct) =>
            Results.Ok(await shared.PeekAsync(token, ct))).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);

        var g = app.MapGroup("/api/shared").RequireAuthorization();

        g.MapGet("", async (HttpContext ctx, SharedService shared, CancellationToken ct) =>
            Results.Ok(await shared.OverviewAsync(ctx.User.HouseholdId(), ct)));

        g.MapPut("/name", async (SetNameRequest req, HttpContext ctx, SharedService shared, CancellationToken ct) =>
            await shared.SetNameAsync(ctx.User.HouseholdId(), req.Name ?? "", ct) is { } problem ? Invalid(problem) : Results.NoContent());

        g.MapPost("/friends", async (SetNameRequest req, HttpContext ctx, SharedService shared, CancellationToken ct) =>
        {
            var (friend, problem) = await shared.AddFriendAsync(ctx.User.HouseholdId(), req.Name ?? "", ct);
            return friend is null ? Invalid(problem!) : Results.Ok(friend);
        });

        g.MapPut("/friends/{id:guid}", async (Guid id, SetNameRequest req, HttpContext ctx, SharedService shared, CancellationToken ct) =>
            Respond(await shared.RenameFriendAsync(ctx.User.HouseholdId(), id, req.Name ?? "", ct), Invalid));

        g.MapDelete("/friends/{id:guid}", async (Guid id, HttpContext ctx, SharedService shared, CancellationToken ct) =>
            await shared.RemoveFriendAsync(ctx.User.HouseholdId(), id, ct) ? Results.NoContent() : Results.NotFound());

        g.MapPost("/friends/{id:guid}/invite", async (Guid id, HttpContext ctx, SharedService shared, CancellationToken ct) =>
        {
            var (invite, result) = await shared.CreateInviteAsync(ctx.User.HouseholdId(), id, ct);
            return invite is null ? Respond(result, Conflict) : Results.Ok(invite);
        });

        g.MapDelete("/friends/{id:guid}/invite", async (Guid id, HttpContext ctx, SharedService shared, CancellationToken ct) =>
        {
            await shared.CancelInviteAsync(ctx.User.HouseholdId(), id, ct);
            return Results.NoContent();
        });

        g.MapDelete("/friends/{id:guid}/link", async (Guid id, HttpContext ctx, SharedService shared, CancellationToken ct) =>
            Respond(await shared.UnlinkAsync(ctx.User.HouseholdId(), id, ct), Conflict));

        // Signed in already (an account made before the invite): join with the link.
        g.MapPost("/join", async (JoinRequest req, HttpContext ctx, SharedService shared, CancellationToken ct) =>
            await shared.JoinAsync(ctx.User.HouseholdId(), req.Token ?? "", ct) is { } problem ? Conflict(problem) : Results.NoContent())
            .RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
    }

    private static IResult Invalid(string problem) => Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [problem] });

    private static IResult Conflict(string problem) => Results.Problem(problem, statusCode: StatusCodes.Status409Conflict);

    private static IResult Respond(SharedResult r, Func<string, IResult> problem) =>
        !r.Found ? Results.NotFound() : r.Problem is { } p ? problem(p) : Results.NoContent();
}
