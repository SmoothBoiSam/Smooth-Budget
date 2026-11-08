using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Pay;

namespace SmoothBudget.Api.Notifications;

// Phone notifications (DECISIONS.md 151): standard Web Push, signed with this app's VAPID keys. The keys come only from
// environment variables; without them the feature is off and the More screen says so.

public class PushOptions
{
    private readonly string? _publicKey, _privateKey;

    /// <summary>Base64url. Pasting quirks are forgiven: spaces, quotes, padding, or the whole "VAPID_PUBLIC_KEY=..." line.</summary>
    public string? PublicKey { get => _publicKey; init => _publicKey = Clean(value); }
    public string? PrivateKey { get => _privateKey; init => _privateKey = Clean(value); }
    /// <summary>Who push services can contact about this sender: the app's own address, never a person's email.</summary>
    public string Subject { get; init; } = "https://smoothbudget.onrender.com";

    /// <summary>Why notifications are off, in words for the More screen; null when they're on.</summary>
    public string? Problem =>
        string.IsNullOrEmpty(PublicKey) || string.IsNullOrEmpty(PrivateKey) ? "Notifications aren't set up on the server yet."
        : Bytes(PublicKey) is not { Length: 65 } p || p[0] != 4 ? "The server's VAPID_PUBLIC_KEY isn't a valid key. Copy it again from .env into Render."
        : Bytes(PrivateKey) is not { Length: 32 } ? "The server's VAPID_PRIVATE_KEY isn't a valid key. Copy it again from .env into Render."
        : null;

    public bool IsConfigured => Problem is null;

    public static string? Clean(string? key)
    {
        if (key is null) return null;
        var k = key.Trim().Trim('"', '\'', '`').Trim();
        if (k.StartsWith("VAPID_", StringComparison.OrdinalIgnoreCase) && k.IndexOf('=') is var eq and > 0) k = k[(eq + 1)..].Trim().Trim('"', '\'').Trim();
        return new string(k.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[]? Bytes(string base64Url)
    {
        var b64 = base64Url.Replace('-', '+').Replace('_', '/');
        b64 += new string('=', (4 - b64.Length % 4) % 4);
        try { return Convert.FromBase64String(b64); }
        catch (FormatException) { return null; }
    }
}

public record PushMessage(string Title, string Body, string Url, string Tag);

public interface IPushSender
{
    /// <returns>False when the push service says the device is gone (uninstalled, permission withdrawn).</returns>
    Task<bool> SendAsync(PushDevice device, PushMessage message, CancellationToken ct);
}

public class WebPushSender(HttpClient http, PushOptions options, ILogger<WebPushSender> log) : IPushSender
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> SendAsync(PushDevice device, PushMessage message, CancellationToken ct)
    {
        var client = new WebPush.WebPushClient(http);
        var vapid = new WebPush.VapidDetails(options.Subject, options.PublicKey, options.PrivateKey);
        try
        {
            await client.SendNotificationAsync(new WebPush.PushSubscription(device.Endpoint, device.P256dh, device.Auth),
                JsonSerializer.Serialize(message, Json), vapid, ct);
            return true;
        }
        catch (WebPush.WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return false;
        }
        catch (WebPush.WebPushException ex)
        {
            log.LogWarning("A push service refused a notification: {Status}", (int)ex.StatusCode);
            return true;
        }
    }
}

/// <summary>What's worth a buzz right now, each piece sent once: payday, bills due tomorrow, a category going over, and low cash.</summary>
public class NotificationService(
    AppDbContext db, TimeProvider clock, IPushSender sender, Home.HomeSummaryService home, Budget.BudgetSummaryService budget,
    Bills.RecurringService recurring, Payday.PaydayService payday)
{
    /// <summary>No buzzing at night: news waits for the morning.</summary>
    public const int FirstHour = 8, LastHour = 21;

    public async Task<int> CheckAsync(Guid householdId, CancellationToken ct = default)
    {
        var devices = await db.PushDevices.Where(d => d.HouseholdId == householdId).ToListAsync(ct);
        if (devices.Count == 0) return 0;
        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        var hour = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).Hour;
        if (hour < FirstHour || hour >= LastHour) return 0;

        var sent = (await db.NotificationLogs.Where(l => l.HouseholdId == householdId).Select(l => l.Key).ToListAsync(ct)).ToHashSet();
        var news = new List<(string Key, PushMessage Message)>();
        foreach (var n in await WhatsNewAsync(householdId, ct))
            if (!sent.Contains(n.Key)) news.Add(n);
        // Several over-budget categories at once arrive as one notification.
        var over = news.Where(n => n.Key.StartsWith("over:", StringComparison.Ordinal)).ToList();
        if (over.Count > 1)
        {
            news.RemoveAll(over.Contains);
            news.Add((string.Join("|", over.Select(o => o.Key)), new PushMessage("Over budget",
                string.Join(" ", over.Select(o => o.Message.Body)), "#/budget", "over")));
        }

        foreach (var (key, message) in news)
        {
            foreach (var device in devices.ToList())
                if (!await sender.SendAsync(device, message, ct))
                {
                    db.PushDevices.Remove(device);
                    devices.Remove(device);
                }
            foreach (var k in key.Split('|'))
                db.NotificationLogs.Add(new NotificationLog { HouseholdId = householdId, Key = k, SentUtc = clock.GetUtcNow().UtcDateTime });
        }
        await db.SaveChangesAsync(ct);
        return news.Count;
    }

    private async Task<List<(string Key, PushMessage Message)>> WhatsNewAsync(Guid householdId, CancellationToken ct)
    {
        var h = await home.GetAsync(householdId, ct);
        var today = h.Today;
        var list = new List<(string, PushMessage)>();

        var check = (await payday.GetAsync(householdId, ct)).Checks.FirstOrDefault(c => c.PayDate == today);
        if (check is not null)
            list.Add(($"payday:{today:yyyy-MM-dd}", new PushMessage("Payday",
                $"About {PayEngine.Dollars(check.NetCents)} lands today. Your plan says where it goes.", "#/payday", "payday")));

        var tomorrow = today.AddDays(1);
        var due = (await recurring.UpcomingAsync(householdId, 20, ct)).Where(b => b.Date == tomorrow).ToList();
        if (due.Count > 0)
            list.Add(($"bills:{tomorrow:yyyy-MM-dd}", new PushMessage(due.Count == 1 ? "Bill due tomorrow" : "Bills due tomorrow",
                string.Join(", ", due.Select(b => $"{b.Name} {PayEngine.Dollars(b.AmountCents)}")), "#/review/bills", "bills")));

        var b = await budget.GetAsync(householdId, ct);
        foreach (var line in b.Groups.Where(g => g.CountsTowardLeftToSpend).SelectMany(g => g.Categories).Where(c => c.RemainingCents < 0 && c.LimitCents > 0))
            list.Add(($"over:{b.BudgetMonth.Start:yyyy-MM-dd}:{line.Id}", new PushMessage("Over budget",
                $"{line.Name} is {PayEngine.Dollars(-line.RemainingCents)} over this month.", "#/budget", "over")));

        foreach (var w in h.CashWarnings ?? [])
            list.Add(($"cash:{(w.NextPayDate ?? h.BudgetMonth.End):yyyy-MM-dd}:{w.Account}", new PushMessage("Low on cash",
                $"{w.Account} is {PayEngine.Dollars(w.ShortCents)} short for the bills due before payday.", "#/payday", "cash")));
        return list;
    }

    public async Task SendTestAsync(Guid householdId, CancellationToken ct)
    {
        foreach (var device in await db.PushDevices.Where(d => d.HouseholdId == householdId).ToListAsync(ct))
            if (!await sender.SendAsync(device, new PushMessage("Smooth Budget", "Notifications are on. Paydays, bills due tomorrow, "
                    + "categories going over and low cash will show up here.", "#/", "test"), ct))
                db.PushDevices.Remove(device);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Checks every household with a device every half hour.</summary>
public class NotificationBackground(IServiceScopeFactory scopes, PushOptions options, ILogger<NotificationBackground> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured)
        {
            log.LogInformation("Notifications are off: set VAPID_PUBLIC_KEY and VAPID_PRIVATE_KEY to turn them on.");
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            List<Guid> households;
            using (var scope = scopes.CreateScope())
                households = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PushDevices
                    .Select(d => d.HouseholdId).Distinct().ToListAsync(stoppingToken);
            foreach (var id in households)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<NotificationService>().CheckAsync(id, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError("Notification check for a household failed: {Error}", ex.GetType().Name);
                }
            }
        }
    }
}

public record PushKeys(string P256dh, string Auth);
public record PushSubscribeRequest(string Endpoint, PushKeys Keys);
public record PushUnsubscribeRequest(string Endpoint);
public record PushStatusDto(bool Enabled, string? PublicKey, string? Problem = null);

public static class NotificationEndpoints
{
    // Only the browsers' own push services: the server posts to this address, so it must never be somewhere else.
    private static readonly string[] PushHosts = ["fcm.googleapis.com", "push.services.mozilla.com", "push.apple.com", "notify.windows.com"];

    public static bool IsPushServiceUrl(string endpoint) =>
        endpoint.Length <= 1000 && Uri.TryCreate(endpoint, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort
        && PushHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    public static void MapNotificationEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/push").RequireAuthorization();
        g.MapGet("", (PushOptions o) => new PushStatusDto(o.IsConfigured, o.IsConfigured ? o.PublicKey : null, o.Problem));

        g.MapPost("/subscribe", async (PushSubscribeRequest req, HttpContext ctx, AppDbContext db, PushOptions o, TimeProvider clock, CancellationToken ct) =>
        {
            if (!o.IsConfigured) return Results.Problem(o.Problem, statusCode: 409);
            if (!IsPushServiceUrl(req.Endpoint ?? "") || req.Keys is null || string.IsNullOrWhiteSpace(req.Keys.P256dh) || req.Keys.P256dh.Length > 200
                || string.IsNullOrWhiteSpace(req.Keys.Auth) || req.Keys.Auth.Length > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["endpoint"] = ["That isn't a browser push address."] });
            var householdId = ctx.User.HouseholdId();
            var device = await db.PushDevices.SingleOrDefaultAsync(d => d.Endpoint == req.Endpoint, ct);
            if (device is null)
                db.PushDevices.Add(device = new PushDevice
                {
                    HouseholdId = householdId, Endpoint = req.Endpoint!, P256dh = req.Keys.P256dh, Auth = req.Keys.Auth,
                    CreatedUtc = clock.GetUtcNow().UtcDateTime,
                });
            else
                (device.HouseholdId, device.P256dh, device.Auth) = (householdId, req.Keys.P256dh, req.Keys.Auth);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/unsubscribe", async (PushUnsubscribeRequest req, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var householdId = ctx.User.HouseholdId();
            await db.PushDevices.Where(d => d.HouseholdId == householdId && d.Endpoint == req.Endpoint).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/test", async (HttpContext ctx, NotificationService svc, PushOptions o, CancellationToken ct) =>
        {
            if (!o.IsConfigured) return Results.Problem(o.Problem, statusCode: 409);
            await svc.SendTestAsync(ctx.User.HouseholdId(), ct);
            return Results.NoContent();
        });
    }
}
