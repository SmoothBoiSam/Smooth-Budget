using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Home;
using SmoothBudget.Api.Import;
using SmoothBudget.Api.Plaid;
using SmoothBudget.Core.Security;
using SmoothBudget.Api.Seeding;
using SmoothBudget.Api.Transactions;

// Local development reads secrets from the git-ignored .env at the repo root. In CI and hosting,
// real environment variables are used instead and no .env file exists.
DotNetEnv.Env.TraversePath().NoClobber().Load();

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var connectionString = config.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Default (see .env.example).");
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));

builder.Services.AddSingleton(TimeProvider.System);

builder.Services
    .AddIdentity<AppUser, IdentityRole<Guid>>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<HouseholdClaimsFactory>();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "sb_auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;
    // This is a JSON API: answer 401/403 instead of redirecting to a login page.
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();

var authPermitPerMinute = config.GetValue("RateLimits:AuthPermitPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPermitPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddScoped<CurrentMonthLoader>();
builder.Services.AddScoped<HomeSummaryService>();
builder.Services.AddScoped<BudgetSummaryService>();
builder.Services.AddScoped<BudgetMonthService>();
builder.Services.AddScoped<BudgetGeneratorService>();
builder.Services.AddScoped<DemoSeeder>();
builder.Services.AddScoped<TransferDetectionService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<CategorizationService>();

// Plaid (milestone 3). Credentials and the token-encryption key come only from environment variables.
var plaidOptions = new PlaidOptions
{
    ClientId = config["PLAID_CLIENT_ID"],
    Secret = config["PLAID_SECRET"],
    Environment = config["PLAID_ENV"] ?? "sandbox",
    WebhookUrl = config["PLAID_WEBHOOK_URL"],
    TokenKey = config["PLAID_TOKEN_KEY"],
};
builder.Services.AddSingleton(plaidOptions);
builder.Services.AddSingleton(sp => new SecretBox(sp.GetRequiredService<PlaidOptions>().TokenKey
    ?? throw new InvalidOperationException("Set PLAID_TOKEN_KEY (see .env.example).")));
builder.Services.AddHttpClient<PlaidClient>((sp, http) =>
{
    http.BaseAddress = sp.GetRequiredService<PlaidOptions>().BaseAddress;
    http.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton<PlaidItemLocks>();
builder.Services.AddSingleton<PlaidSyncQueue>();
builder.Services.AddScoped<PlaidSyncService>();
builder.Services.AddScoped<PlaidLinkService>();
builder.Services.AddScoped<IPlaidWebhookKeySource, PlaidApiKeySource>();
builder.Services.AddScoped<PlaidWebhookVerifier>();
builder.Services.AddHostedService<PlaidBackgroundSync>();
// Leave headroom over the 5 MB CSV limit for multipart framing.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = ImportService.MaxFileBytes + 64 * 1024);

var app = builder.Build();

// `dotnet run -- seed` : migrate the database, create/reset the demo login, load fake data, exit.
if (args.Contains("seed"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    var password = config["SEED_DEMO_PASSWORD"]
        ?? throw new InvalidOperationException("Set SEED_DEMO_PASSWORD in .env (see .env.example).");
    await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedDemoUserAsync(password);
    Console.WriteLine($"Seeded fake data. Log in as {DemoSeeder.DemoEmail} with SEED_DEMO_PASSWORD from .env.");
    return;
}

if (config.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment()))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();
app.MapImportEndpoints();
app.MapTransactionEndpoints();
app.MapBudgetEndpoints();
app.MapPlaidEndpoints();
app.MapPartnerEndpoints();
app.MapGet("/api/home/summary", (HttpContext ctx, HomeSummaryService svc, CancellationToken ct) => svc.GetAsync(ctx.User.HouseholdId(), ct))
    .RequireAuthorization();

app.Run();

public partial class Program;
