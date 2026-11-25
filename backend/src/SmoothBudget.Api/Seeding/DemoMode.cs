using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;

namespace SmoothBudget.Api.Seeding;

/// <summary>
/// "Try the demo" on the sign-in screen (DECISIONS.md 213). Each visitor gets their own copy of the fake demo data, always
/// starting from the same state, so people can tick things off without changing what the next person sees. A copy is a
/// household like any other, marked with when it expires; it has no password (only the browser that started it gets in)
/// and is deleted a day later. Connecting banks, inviting or joining friends, and notifications are off in a copy.
/// </summary>
public static class DemoMode
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    /// <summary>Most copies at once and started per hour, so the button can't be used to fill the database.</summary>
    public const int MaxAlive = 200, MaxPerHour = 60;
    public const string EmailDomain = "demo.smoothbudget.invalid";
    public const string Blocked = "That's turned off in the demo. Everything else works; it's all made-up data.";

    public static void MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/demo", async (AppDbContext db, UserManager<AppUser> users, SignInManager<AppUser> signIn, DemoSeeder seeder,
            TimeProvider clock, IConfiguration config, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!config.GetValue("Demo:Enabled", true))
                return Results.NotFound();
            var now = clock.GetUtcNow().UtcDateTime;
            await DeleteExpiredAsync(db, now, ct);

            var alive = await db.Households.CountAsync(h => h.DemoUntilUtc != null, ct);
            var lastHour = await db.Households.CountAsync(h => h.DemoUntilUtc != null && h.CreatedUtc > now.AddHours(-1), ct);
            if (alive >= MaxAlive || lastHour >= MaxPerHour)
                return Results.Problem("Lots of people are trying the demo right now. Try again in a little while.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var household = new Household { Id = Guid.NewGuid(), Name = "Demo", CreatedUtc = now, DemoUntilUtc = now + Lifetime };
            db.Households.Add(household);
            await db.SaveChangesAsync(ct);
            var email = $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}@{EmailDomain}";
            var user = new AppUser { Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = true, HouseholdId = household.Id };
            var created = await users.CreateAsync(user); // no password: nobody can sign in to it, only this browser's cookie
            if (!created.Succeeded)
            {
                await DeleteHouseholdAsync(db, household.Id, ct);
                return Results.Problem("Couldn't start the demo. Try again.", statusCode: StatusCodes.Status500InternalServerError);
            }
            await seeder.SeedHouseholdAsync(household.Id, ct);
            await signIn.SignInAsync(user, isPersistent: false); // gone when the browser closes, like the copy a day later
            logs.CreateLogger("Demo").LogInformation("Started a demo copy ({Alive} alive)", alive + 1);
            return Results.Ok(new MeResponse(email, household.Id, Demo: true));
        }).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
    }

    /// <summary>For endpoints a demo copy can't use (banks, invites, notifications): refused with a plain explanation.</summary>
    public static RouteHandlerBuilder NotInDemo(this RouteHandlerBuilder b) => b.AddEndpointFilter(Filter);
    public static RouteGroupBuilder NotInDemo(this RouteGroupBuilder g) => g.AddEndpointFilter(Filter);

    private static async ValueTask<object?> Filter(EndpointFilterInvocationContext c, EndpointFilterDelegate next)
    {
        var http = c.HttpContext;
        if (http.User.Identity?.IsAuthenticated == true)
        {
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var id = http.User.HouseholdId();
            if (await db.Households.AnyAsync(h => h.Id == id && h.DemoUntilUtc != null, http.RequestAborted))
                return Results.Problem(Blocked, statusCode: StatusCodes.Status403Forbidden);
        }
        return await next(c);
    }

    public static Task<bool> IsDemoAsync(AppDbContext db, Guid householdId, CancellationToken ct) =>
        db.Households.AnyAsync(h => h.Id == householdId && h.DemoUntilUtc != null, ct);

    /// <summary>Deletes demo copies past their day (a few at a time, as people start new ones).</summary>
    public static async Task DeleteExpiredAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var expired = await db.Households.Where(h => h.DemoUntilUtc != null && h.DemoUntilUtc < now)
            .OrderBy(h => h.DemoUntilUtc).Select(h => h.Id).Take(20).ToListAsync(ct);
        foreach (var id in expired)
            await DeleteHouseholdAsync(db, id, ct);
    }

    /// <summary>Everything a household has, then the household and its login. Only ever called for demo copies.</summary>
    public static async Task DeleteHouseholdAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Transactions.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.PartnerArrangements.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.FriendInvites.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.Friends.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.HouseholdLinks.Where(x => x.HouseholdAId == id || x.HouseholdBId == id).ExecuteDeleteAsync(ct);
        await db.SweepTasks.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.BudgetMonths.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.SavingsGoals.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.DebtAccounts.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.PlannedExpenses.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.RecurringItems.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.ImportBatches.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.Categories.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.Accounts.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.PlaidItems.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.PayProfiles.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.ScheduleMonths.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.PayStubs.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.PayPeriodForecasts.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.ExpectedIncomes.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.PaydayChecklistItems.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.PushDevices.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.NotificationLogs.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.Users.Where(x => x.HouseholdId == id).ExecuteDeleteAsync(ct);
        await db.Households.Where(x => x.Id == id && x.DemoUntilUtc != null).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }
}
