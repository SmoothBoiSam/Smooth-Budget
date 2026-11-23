using SmoothBudget.Api.Seeding;
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

/// <summary>One kind of notification, each with its own switch in More → Settings (DECISIONS.md 163).</summary>
public record NotificationTypeDto(string Id, string Title, string Detail, bool On);

/// <summary>
/// What's worth a buzz right now, each piece sent once: payday, bills due tomorrow, a category going over, Left to Spend
/// running low, low cash, bill price rises, a late partner contribution, a bank that needs you, and a weekly review nudge.
/// </summary>
public class NotificationService(
    AppDbContext db, TimeProvider clock, IPushSender sender, Home.HomeSummaryService home, Budget.BudgetSummaryService budget,
    Bills.RecurringService recurring, Payday.PaydayService payday)
{
    /// <summary>No buzzing at night: news waits for the morning.</summary>
    public const int FirstHour = 8, LastHour = 21;
    /// <summary>The weekly review reminder: Sunday, from this hour.</summary>
    public const int ReviewHour = 17;

    public static readonly (string Id, string Title, string Detail)[] Types =
    [
        ("payday", "Payday", "The day a paycheck lands, with about how much."),
        ("bills", "Bills due tomorrow", "The day before a bill or subscription is charged."),
        ("over", "Category over budget", "When a spending category goes over its limit, once a month each."),
        ("low", "Left to Spend running low", "When under 15% of this month's spending money is left, and again if it drops below zero."),
        ("cash", "Safe to Spend", "When checking can't cover Left to Spend before your next check, so it's adjusted."),
        ("price", "Bill price went up", "When a bill or subscription charges more than it used to."),
        ("partner", "Friend contribution late", "When a fixed contribution from a friend hasn't arrived by the end of its usual days."),
        ("bank", "Bank needs you", "When a bank connection stops syncing and needs you to sign in again. Weekly until it's fixed."),
        ("review", "Weekly review reminder", "Sunday evening, when purchases are waiting for you to confirm."),
    ];

    public async Task<List<NotificationTypeDto>> TypesAsync(Guid householdId, CancellationToken ct)
    {
        var off = await db.Households.Where(h => h.Id == householdId).Select(h => h.NotificationsOff).SingleAsync(ct);
        return Types.Select(t => new NotificationTypeDto(t.Id, t.Title, t.Detail, !off.Contains(t.Id))).ToList();
    }

    public async Task<bool> SetTypeAsync(Guid householdId, string id, bool on, CancellationToken ct)
    {
        if (!Types.Any(t => t.Id == id)) return false;
        var h = await db.Households.SingleAsync(x => x.Id == householdId, ct);
        h.NotificationsOff = on ? h.NotificationsOff.Where(x => x != id).ToList() : h.NotificationsOff.Append(id).Distinct().ToList();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> CheckAsync(Guid householdId, CancellationToken ct = default)
    {
        var devices = await db.PushDevices.Where(d => d.HouseholdId == householdId).ToListAsync(ct);
        if (devices.Count == 0) return 0;
        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone);
        if (local.Hour < FirstHour || local.Hour >= LastHour) return 0;

        var sent = (await db.NotificationLogs.Where(l => l.HouseholdId == householdId).Select(l => l.Key).ToListAsync(ct)).ToHashSet();
        var news = new List<(string Key, PushMessage Message)>();
        // A type you turned off isn't logged as sent either, so turning it back on picks up what's still true.
        foreach (var n in await WhatsNewAsync(household, local, ct))
            if (!sent.Contains(n.Key) && !household.NotificationsOff.Contains(n.Type)) news.Add((n.Key, n.Message));
        // Several over-budget categories (or price rises) at once arrive as one notification.
        Merge(news, "over:", "Over budget", "#/budget", "over");
        Merge(news, "price:", "Bill prices went up", "#/budget", "price");

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

    private static void Merge(List<(string Key, PushMessage Message)> news, string prefix, string title, string url, string tag)
    {
        var same = news.Where(n => n.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (same.Count < 2) return;
        news.RemoveAll(same.Contains);
        news.Add((string.Join("|", same.Select(o => o.Key)), new PushMessage(title, string.Join(" ", same.Select(o => o.Message.Body)), url, tag)));
    }

    private async Task<List<(string Type, string Key, PushMessage Message)>> WhatsNewAsync(Household household, DateTimeOffset local, CancellationToken ct)
    {
        var householdId = household.Id;
        var h = await home.GetAsync(householdId, ct);
        var today = h.Today;
        var list = new List<(string, string, PushMessage)>();

        var check = (await payday.GetAsync(householdId, ct)).Checks.FirstOrDefault(c => c.PayDate == today);
        if (check is not null)
            list.Add(("payday", $"payday:{today:yyyy-MM-dd}", new PushMessage("Payday",
                $"About {PayEngine.Dollars(check.NetCents)} lands today. Your plan says where it goes.", "#/tasks/payday", "payday")));

        var tomorrow = today.AddDays(1);
        var due = (await recurring.UpcomingAsync(householdId, 20, ct)).Where(b => b.Date == tomorrow).ToList();
        if (due.Count > 0)
            list.Add(("bills", $"bills:{tomorrow:yyyy-MM-dd}", new PushMessage(due.Count == 1 ? "Bill due tomorrow" : "Bills due tomorrow",
                string.Join(", ", due.Select(b => $"{b.Name} {PayEngine.Dollars(b.AmountCents)}")), "#/budget", "bills")));

        var b = await budget.GetAsync(householdId, ct);
        foreach (var line in b.Groups.Where(g => g.CountsTowardLeftToSpend).SelectMany(g => g.Categories).Where(c => c.RemainingCents < 0 && c.LimitCents > 0))
            list.Add(("over", $"over:{b.BudgetMonth.Start:yyyy-MM-dd}:{line.Id}", new PushMessage("Over budget",
                $"{line.Name} is {PayEngine.Dollars(-line.RemainingCents)} over this month.", "#/budget", "over")));

        // Left to Spend: once when it runs low, and once more if it goes below zero, each budget month.
        var month = $"{h.BudgetMonth.Start:yyyy-MM-dd}";
        if (h.Status == "overspent")
            list.Add(("low", $"overspent:{month}", new PushMessage("Overspent",
                $"Left to Spend is {PayEngine.Dollars(-h.LeftToSpendCents)} below zero with {Days(h.DaysRemaining)} left. What's spent beyond it comes out of next month.", "#/", "low")));
        else if (h.Status == "low")
            list.Add(("low", $"low:{month}", new PushMessage("Left to Spend is running low",
                $"{PayEngine.Dollars(h.LeftToSpendCents)} left with {Days(h.DaysRemaining)} to go: about {PayEngine.Dollars(h.SafeToSpendPerDayCents)} a day.", "#/", "low")));

        // Safe to Spend: checking can't cover Left to Spend until payday (DECISIONS.md 275, 276). Once per payday.
        if (h.SafeToSpend is { } bp)
            list.Add(("cash", $"cash:{bp.PayDate:yyyy-MM-dd}", new PushMessage("Safe to Spend",
                $"Checking can cover {PayEngine.Dollars(bp.SafeCents)} until {bp.Until}, less than Left to Spend.", "#/", "cash")));

        // A price rise you haven't said "Got it" to, once per new price.
        foreach (var i in (await recurring.SummaryAsync(householdId, ct)).Items.Where(i => i.Status == "priceUp"))
            list.Add(("price", $"price:{i.Key}:{i.AmountCents}", new PushMessage("Bill price went up",
                $"{i.Name} went up{(i.PreviousAmountCents is { } was ? $" from {PayEngine.Dollars(was)}" : "")} to {PayEngine.Dollars(i.AmountCents)}.", "#/budget", "price")));

        // Each friend's own, opening their page (DECISIONS.md 211).
        foreach (var c in (h.Contributions ?? []).Where(c => c.State == "late"))
            list.Add(("partner", $"partnerlate:{c.WindowStart:yyyy-MM-dd}:{c.ArrangementId}", new PushMessage("Contribution late",
                $"{c.FriendName ?? "Your friend"}'s {PayEngine.Dollars(c.ExpectedCents)} toward {c.CategoryName} hasn't arrived. Left to Spend counts without it for now.",
                c.FriendId is { } fid ? $"#/friends/{fid}" : "#/friends", "partner")));

        // A bank that stopped syncing: weekly until it's fixed (the week is part of the key).
        var week = System.Globalization.ISOWeek.GetYear(today.ToDateTime(TimeOnly.MinValue)) * 100 + System.Globalization.ISOWeek.GetWeekOfYear(today.ToDateTime(TimeOnly.MinValue));
        foreach (var item in await db.PlaidItems.AsNoTracking()
                     .Where(i => i.HouseholdId == householdId && (i.Status == PlaidItemStatus.LoginRequired || i.Status == PlaidItemStatus.Error)).ToListAsync(ct))
            list.Add(("bank", $"bank:{item.Id}:{week}", new PushMessage($"{item.InstitutionName ?? "Your bank"} needs you",
                item.Status == PlaidItemStatus.LoginRequired
                    ? $"{item.InstitutionName ?? "Your bank"} wants you to sign in again before it can keep syncing. Tap Reconnect in More."
                    : $"Syncing {item.InstitutionName ?? "your bank"} stopped working. Open More to reconnect it.", "#/more", "bank")));

        var waiting = h.ReviewCount + h.UncategorizedCount;
        if (local.DayOfWeek == DayOfWeek.Sunday && local.Hour >= ReviewHour && waiting > 0)
            list.Add(("review", $"review:{today:yyyy-MM-dd}", new PushMessage("A few minutes of review",
                $"{waiting} {(waiting == 1 ? "purchase is" : "purchases are")} waiting for you to confirm this month.", "#/tasks/transactions", "review")));
        return list;
    }

    private static string Days(int n) => n == 1 ? "1 day" : $"{n} days";

    public async Task SendTestAsync(Guid householdId, CancellationToken ct)
    {
        foreach (var device in await db.PushDevices.Where(d => d.HouseholdId == householdId).ToListAsync(ct))
            if (!await sender.SendAsync(device, new PushMessage("Smooth Budget", "Notifications are on. Choose which kinds you get in More → Settings.", "#/", "test"), ct))
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
public record NotificationTypeRequest(bool On);

public static class NotificationEndpoints
{
    // Only the browsers' own push services: the server posts to this address, so it must never be somewhere else.
    private static readonly string[] PushHosts = ["fcm.googleapis.com", "push.services.mozilla.com", "push.apple.com", "notify.windows.com"];

    public static bool IsPushServiceUrl(string endpoint) =>
        endpoint.Length <= 1000 && Uri.TryCreate(endpoint, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort
        && PushHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    public static void MapNotificationEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/push").RequireAuthorization().NotInDemo(); // no notifications from a demo copy (DECISIONS.md 213)
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

        // Each kind of notification has its own switch, for the whole household (DECISIONS.md 163).
        g.MapGet("/types", async (HttpContext ctx, NotificationService svc, CancellationToken ct) => await svc.TypesAsync(ctx.User.HouseholdId(), ct));
        g.MapPut("/types/{id}", async (string id, NotificationTypeRequest req, HttpContext ctx, NotificationService svc, CancellationToken ct) =>
            await svc.SetTypeAsync(ctx.User.HouseholdId(), id, req.On, ct) ? Results.NoContent() : Results.NotFound());

        g.MapPost("/test", async (HttpContext ctx, NotificationService svc, PushOptions o, CancellationToken ct) =>
        {
            if (!o.IsConfigured) return Results.Problem(o.Problem, statusCode: 409);
            await svc.SendTestAsync(ctx.User.HouseholdId(), ct);
            return Results.NoContent();
        });
    }
}
