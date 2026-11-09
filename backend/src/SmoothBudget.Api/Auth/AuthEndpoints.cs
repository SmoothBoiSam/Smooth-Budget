using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using SmoothBudget.Api.Data;

namespace SmoothBudget.Api.Auth;

/// <param name="Invite">A friend's invite (DECISIONS.md 205): signs up even while sign-up is closed, and links the two accounts.</param>
/// <param name="Name">Your name as your friend will see it (with an invite).</param>
public record RegisterRequest(string Email, string Password, string? Invite = null, string? Name = null);
public record LoginRequest(string Email, string Password);
public record MeResponse(string Email, Guid HouseholdId);

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        // Sign-up is closed in production once any account exists (the first one can still be created on a fresh database).
        auth.MapGet("/options", async (AppDbContext db, IConfiguration config, IWebHostEnvironment env) =>
            Results.Ok(new { signUpOpen = await SignUpOpenAsync(db, config, env) }));

        auth.MapPost("/register", async (RegisterRequest req, UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db, TimeProvider clock, IConfiguration config, IWebHostEnvironment env, SmoothBudget.Api.Shared.SharedService shared) =>
        {
            // A working invite from a friend opens sign-up for you alone.
            var invited = !string.IsNullOrWhiteSpace(req.Invite);
            if (invited)
            {
                if (await shared.CheckAsync(req.Invite!, default) is { } problem)
                    return Results.Problem(problem, statusCode: StatusCodes.Status403Forbidden);
                if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > 60)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Add your name (up to 60 characters), so your friend knows it's you."] });
            }
            else if (!await SignUpOpenAsync(db, config, env))
                return Results.Problem("Sign-up is closed on this app.", statusCode: StatusCodes.Status403Forbidden);
            await using var tx = await db.Database.BeginTransactionAsync();
            var household = new Household
            {
                Id = Guid.NewGuid(), Name = "My household", CreatedUtc = clock.GetUtcNow().UtcDateTime,
                AutoBudget = config.GetValue("Budget:AutoForNewHouseholds", true),
                DisplayName = invited ? req.Name!.Trim() : null,
            };
            db.Households.Add(household);
            await db.SaveChangesAsync();

            var user = new AppUser { Id = Guid.NewGuid(), UserName = req.Email, Email = req.Email, HouseholdId = household.Id };
            var result = await users.CreateAsync(user, req.Password);
            if (!result.Succeeded)
                return Results.ValidationProblem(result.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));

            if (invited && await shared.JoinAsync(household.Id, req.Invite!, default) is { } joinProblem)
                return Results.Problem(joinProblem, statusCode: StatusCodes.Status409Conflict); // rolls back: no half-made account

            await tx.CommitAsync();
            await signIn.SignInAsync(user, isPersistent: true);
            return Results.Ok(new MeResponse(user.Email!, household.Id));
        }).RequireRateLimiting(RateLimitPolicy);

        auth.MapPost("/login", async (LoginRequest req, SignInManager<AppUser> signIn, UserManager<AppUser> users) =>
        {
            var result = await signIn.PasswordSignInAsync(req.Email, req.Password, isPersistent: true, lockoutOnFailure: true);
            if (result.IsLockedOut)
                return Results.Problem("Too many failed attempts. Try again later.", statusCode: StatusCodes.Status423Locked);
            if (!result.Succeeded)
                return Results.Problem("Email or password is incorrect.", statusCode: StatusCodes.Status401Unauthorized);

            var user = await users.FindByEmailAsync(req.Email);
            return Results.Ok(new MeResponse(user!.Email!, user.HouseholdId));
        }).RequireRateLimiting(RateLimitPolicy);

        auth.MapPost("/logout", async (SignInManager<AppUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        auth.MapGet("/me", (HttpContext ctx) =>
            Results.Ok(new MeResponse(ctx.User.Identity!.Name!, ctx.User.HouseholdId())))
            .RequireAuthorization();
    }

    /// <summary>Auth:AllowSignUp (default: on everywhere except Production), or no account exists yet.</summary>
    public static async Task<bool> SignUpOpenAsync(AppDbContext db, IConfiguration config, IWebHostEnvironment env) =>
        config.GetValue("Auth:AllowSignUp", !env.IsProduction()) || !await db.Users.AnyAsync();
}
