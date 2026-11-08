using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Hosting;
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

var connectionString = SmoothBudget.Api.Hosting.HostingSetup.ConnectionString(config.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Default (see .env.example)."));
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));

// Login cookies are signed with keys kept in the database, so a redeploy doesn't sign you out.
builder.Services.AddDataProtection().SetApplicationName("SmoothBudget").PersistKeysToDbContext<AppDbContext>();

// Behind a host's TLS proxy (Render): trust its X-Forwarded-Proto/For so cookies are Secure and rate limits see your IP.
var behindProxy = config.GetValue("Hosting:BehindProxy", false);
if (behindProxy)
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear(); // the proxy's address isn't fixed; only its last hop is trusted (ForwardLimit 1)
        o.KnownProxies.Clear();
    });

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
builder.Services.AddScoped<CategoryDiscoveryService>();
builder.Services.AddScoped<SmoothBudget.Api.Debt.DebtService>();
builder.Services.AddScoped<SmoothBudget.Api.Debt.CardInterestService>();
builder.Services.AddScoped<SmoothBudget.Api.Bills.RecurringService>();
builder.Services.AddScoped<SmoothBudget.Api.Savings.SavingsService>();
builder.Services.AddScoped<SmoothBudget.Api.Payday.PaydayService>();
builder.Services.AddSingleton<BudgetRefreshTracker>();
builder.Services.AddScoped<AutoBudgetService>();

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
// Phone notifications (DECISIONS.md 151). Off until the VAPID keys are set.
builder.Services.AddSingleton(new SmoothBudget.Api.Notifications.PushOptions
{
    PublicKey = config["VAPID_PUBLIC_KEY"],
    PrivateKey = config["VAPID_PRIVATE_KEY"],
    Subject = config["VAPID_SUBJECT"] is { Length: > 0 } subject ? subject : "https://smoothbudget.onrender.com",
});
builder.Services.AddHttpClient<SmoothBudget.Api.Notifications.IPushSender, SmoothBudget.Api.Notifications.WebPushSender>(http => http.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<SmoothBudget.Api.Notifications.NotificationService>();
builder.Services.AddHostedService<SmoothBudget.Api.Notifications.NotificationBackground>();
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

if (behindProxy) app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseSecurityHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    // Behind a proxy the host redirects HTTP to HTTPS itself, and its health checks arrive over plain HTTP.
    if (!behindProxy) app.UseHttpsRedirection();
}
app.UseWebsite();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseBudgetChangeTracking(); // any change you make refreshes the automatic budget

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();
app.MapImportEndpoints();
app.MapTransactionEndpoints();
app.MapBudgetEndpoints();
app.MapPlaidEndpoints();
app.MapPartnerEndpoints();
SmoothBudget.Api.Debt.DebtEndpoints.MapDebtEndpoints(app);
SmoothBudget.Api.Bills.RecurringEndpoints.MapRecurringEndpoints(app);
SmoothBudget.Api.Savings.SavingsEndpoints.MapSavingsEndpoints(app);
SmoothBudget.Api.Payday.PaydayEndpoints.MapPaydayEndpoints(app);
SmoothBudget.Api.Notifications.NotificationEndpoints.MapNotificationEndpoints(app);
app.MapGet("/api/home/summary", (HttpContext ctx, HomeSummaryService svc, CancellationToken ct) => svc.GetAsync(ctx.User.HouseholdId(), ct))
    .RequireAuthorization();

app.Run();

public partial class Program;
