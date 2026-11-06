using Microsoft.AspNetCore.Identity;
using SmoothBudget.Api.Data;

namespace SmoothBudget.Api.Auth;

public record RegisterRequest(string Email, string Password);
public record LoginRequest(string Email, string Password);
public record MeResponse(string Email, Guid HouseholdId);

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/register", async (RegisterRequest req, UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db, TimeProvider clock) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var household = new Household { Id = Guid.NewGuid(), Name = "My household", CreatedUtc = clock.GetUtcNow().UtcDateTime };
            db.Households.Add(household);
            await db.SaveChangesAsync();

            var user = new AppUser { Id = Guid.NewGuid(), UserName = req.Email, Email = req.Email, HouseholdId = household.Id };
            var result = await users.CreateAsync(user, req.Password);
            if (!result.Succeeded)
                return Results.ValidationProblem(result.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));

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
}
