using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;

namespace SmoothBudget.Api.Plaid;

/// <summary>Items waiting for a background sync (from webhooks and the periodic sweep).</summary>
public class PlaidSyncQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public void Enqueue(Guid itemId) => _channel.Writer.TryWrite(itemId);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Hosted background worker (SPEC section 2): syncs items when a webhook says there's news, and sweeps
/// every active item every few hours as a fallback, which is what keeps data fresh locally where
/// Plaid can't reach the webhook URL.
/// </summary>
public class PlaidBackgroundSync(
    IServiceScopeFactory scopes, PlaidSyncQueue queue, PlaidOptions options, IConfiguration config, ILogger<PlaidBackgroundSync> log)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured)
        {
            log.LogInformation("Plaid is not configured; background sync is off.");
            return Task.CompletedTask;
        }
        var interval = TimeSpan.FromHours(config.GetValue("Plaid:SweepHours", 6.0));
        return Task.WhenAll(ProcessQueueAsync(stoppingToken), SweepAsync(interval, stoppingToken), PointWebhooksAsync(stoppingToken));
    }

    /// <summary>
    /// When hosted (PLAID_WEBHOOK_URL set), tells Plaid where to send each item's webhooks at startup, so banks linked
    /// before the app had a public address (or under an old one) still push updates.
    /// </summary>
    private async Task PointWebhooksAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.WebhookUrl)) return;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var plaid = scope.ServiceProvider.GetRequiredService<PlaidClient>();
            var secrets = scope.ServiceProvider.GetRequiredService<SmoothBudget.Core.Security.SecretBox>();
            var items = await db.PlaidItems.Where(i => i.Status == PlaidItemStatus.Active).ToListAsync(ct);
            foreach (var item in items)
            {
                try { await plaid.UpdateWebhookAsync(secrets.Decrypt(item.AccessTokenEncrypted), options.WebhookUrl!, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("Couldn't point item {ItemId}'s webhook at this app: {Error}", item.Id, ex.GetType().Name);
                }
                queue.Enqueue(item.Id); // and catch up after a deploy
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Pointing webhooks at this app failed: {Error}", ex.GetType().Name);
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await foreach (var itemId in queue.ReadAllAsync(ct))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PlaidSyncService>().SyncAsync(itemId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError("Background sync of item {ItemId} failed: {Error}", itemId, ex.GetType().Name);
            }
        }
    }

    private async Task SweepAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = await db.PlaidItems.Where(i => i.Status == PlaidItemStatus.Active).Select(i => i.Id).ToListAsync(ct);
            foreach (var id in ids)
                queue.Enqueue(id);
        }
    }
}
