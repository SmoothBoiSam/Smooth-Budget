using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Security;

namespace SmoothBudget.Api.Plaid;

public record LinkTokenRequest(Guid? ItemId);
public record ExchangeRequest(string PublicToken);
public record ConfirmAccountsRequest(List<AccountMappingRequest> Mappings);
public record SandboxLinkRequest(bool Dynamic);

public static class PlaidEndpoints
{
    /// <summary>"First Platypus Bank", Plaid's standard sandbox institution.</summary>
    public const string SandboxInstitution = "ins_109508";

    public static void MapPlaidEndpoints(this WebApplication app)
    {
        var plaidGroup = app.MapGroup("/api/plaid").RequireAuthorization();
        var sandboxShortcut = app.Environment.IsDevelopment();

        plaidGroup.MapGet("/config", (PlaidOptions o) => new
        {
            enabled = o.IsConfigured,
            environment = o.Environment,
            sandboxShortcut = o.IsConfigured && o.IsSandbox && sandboxShortcut,
            webhooks = !string.IsNullOrWhiteSpace(o.WebhookUrl),
        });

        plaidGroup.MapPost("/link-token", async (LinkTokenRequest req, HttpContext ctx, AppDbContext db, PlaidClient plaid, SecretBox secrets, CancellationToken ct) =>
            await Guard(async () =>
            {
                string? updateToken = null;
                if (req.ItemId is { } itemId)
                {
                    var item = await db.PlaidItems.SingleOrDefaultAsync(i => i.Id == itemId && i.HouseholdId == ctx.User.HouseholdId(), ct);
                    if (item is null) return Results.NotFound();
                    updateToken = secrets.Decrypt(item.AccessTokenEncrypted);
                }
                // The user's id (a random GUID), never an email, identifies the user to Plaid.
                var token = await plaid.CreateLinkTokenAsync(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!, updateToken, ct);
                return Results.Ok(new { linkToken = token });
            }));

        plaidGroup.MapPost("/exchange", async (ExchangeRequest req, HttpContext ctx, PlaidLinkService link, CancellationToken ct) =>
            await Guard(async () => Results.Ok(await link.ExchangeAsync(ctx.User.HouseholdId(), req.PublicToken, ct))));

        // Sandbox + Development only: link Plaid's test bank without the Link UI.
        plaidGroup.MapPost("/sandbox/link", async (SandboxLinkRequest req, HttpContext ctx, PlaidOptions o, PlaidClient plaid, PlaidLinkService link, CancellationToken ct) =>
        {
            if (!(o.IsSandbox && sandboxShortcut)) return Results.NotFound();
            return await Guard(async () =>
            {
                // user_transactions_dynamic is Plaid's documented test user whose pending transactions post on refresh.
                var publicToken = await plaid.SandboxCreatePublicTokenAsync(SandboxInstitution, req.Dynamic ? "user_transactions_dynamic" : null, ct);
                return Results.Ok(await link.ExchangeAsync(ctx.User.HouseholdId(), publicToken, ct));
            });
        });

        // Sandbox + Development only: ask Plaid to generate new activity (and post pending ones) for a test item.
        plaidGroup.MapPost("/sandbox/items/{id:guid}/refresh", async (Guid id, HttpContext ctx, PlaidOptions o, AppDbContext db, PlaidClient plaid, SecretBox secrets, CancellationToken ct) =>
        {
            if (!(o.IsSandbox && sandboxShortcut)) return Results.NotFound();
            var item = await db.PlaidItems.SingleOrDefaultAsync(i => i.Id == id && i.HouseholdId == ctx.User.HouseholdId(), ct);
            if (item is null) return Results.NotFound();
            return await Guard(async () =>
            {
                await plaid.RefreshTransactionsAsync(secrets.Decrypt(item.AccessTokenEncrypted), ct);
                return Results.NoContent();
            });
        });

        plaidGroup.MapPost("/items/{id:guid}/accounts", async (Guid id, ConfirmAccountsRequest req, HttpContext ctx, PlaidLinkService link, CancellationToken ct) =>
            await Guard(async () => Results.Ok(await link.ConfirmAccountsAsync(ctx.User.HouseholdId(), id, req.Mappings, ct))));

        plaidGroup.MapGet("/items", async (HttpContext ctx, PlaidLinkService link, CancellationToken ct) =>
            await link.ListAsync(ctx.User.HouseholdId(), ct));

        plaidGroup.MapDelete("/items/{id:guid}", async (Guid id, HttpContext ctx, PlaidLinkService link, CancellationToken ct) =>
            await Guard(async () => await link.RemoveAsync(ctx.User.HouseholdId(), id, ct) ? Results.NoContent() : Results.NotFound()));

        // After the user signs in again through Link's update mode.
        plaidGroup.MapPost("/items/{id:guid}/reconnected", async (Guid id, HttpContext ctx, AppDbContext db, PlaidSyncService sync, CancellationToken ct) =>
        {
            var item = await db.PlaidItems.SingleOrDefaultAsync(i => i.Id == id && i.HouseholdId == ctx.User.HouseholdId(), ct);
            if (item is null) return Results.NotFound();
            item.Status = PlaidItemStatus.Active;
            item.StatusMessage = null;
            await db.SaveChangesAsync(ct);
            return await Guard(async () => Results.Ok(await sync.SyncAsync(id, ct)));
        });

        // Pull to refresh (SPEC section 4.2): sync every active bank connection now.
        app.MapPost("/api/sync", async (HttpContext ctx, AppDbContext db, PlaidSyncService sync, PlaidOptions o, CancellationToken ct) =>
        {
            if (!o.IsConfigured) return Results.Ok(Array.Empty<SyncResultDto>());
            var ids = await db.PlaidItems
                .Where(i => i.HouseholdId == ctx.User.HouseholdId() && i.Status != PlaidItemStatus.PendingSetup)
                .Select(i => i.Id).ToListAsync(ct);
            var results = new List<SyncResultDto>();
            foreach (var id in ids)
                results.Add(await sync.SyncAsync(id, ct));
            return Results.Ok(results);
        }).RequireAuthorization();

        // Plaid calls this; it is not a user request, so no cookie. Every call must carry a valid signature.
        app.MapPost("/api/plaid/webhook", async (HttpContext ctx, PlaidWebhookVerifier verifier, AppDbContext db, PlaidSyncQueue queue, ILoggerFactory logs, CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct);
            var body = ms.ToArray();
            if (!await verifier.VerifyAsync(ctx.Request.Headers["Plaid-Verification"].ToString(), body, ct))
                return Results.Unauthorized();

            using var doc = JsonDocument.Parse(body);
            string? Get(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var (type, code, plaidItemId) = (Get("webhook_type"), Get("webhook_code"), Get("item_id"));
            logs.CreateLogger("PlaidWebhook").LogInformation("Plaid webhook {Type}/{Code}", type, code);

            var item = plaidItemId is null ? null : await db.PlaidItems.SingleOrDefaultAsync(i => i.PlaidItemId == plaidItemId, ct);
            if (item is null)
                return Results.Ok(); // unknown or removed item: acknowledge so Plaid stops retrying

            switch (type, code)
            {
                case ("TRANSACTIONS", "SYNC_UPDATES_AVAILABLE"):
                    if (item.Status == PlaidItemStatus.Active) queue.Enqueue(item.Id);
                    break;
                case ("ITEM", "ERROR"):
                    var errorCode = doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                        && e.TryGetProperty("error_code", out var ec) ? ec.GetString() : null;
                    item.Status = errorCode == "ITEM_LOGIN_REQUIRED" ? PlaidItemStatus.LoginRequired : PlaidItemStatus.Error;
                    item.StatusMessage = errorCode == "ITEM_LOGIN_REQUIRED" ? "Your bank needs you to sign in again." : $"Bank connection error ({errorCode}).";
                    break;
                case ("ITEM", "PENDING_EXPIRATION" or "PENDING_DISCONNECT"):
                    item.Status = PlaidItemStatus.LoginRequired;
                    item.StatusMessage = "Your bank connection is about to expire. Reconnect to keep syncing.";
                    break;
                case ("ITEM", "LOGIN_REPAIRED"):
                    item.Status = PlaidItemStatus.Active;
                    item.StatusMessage = null;
                    queue.Enqueue(item.Id);
                    break;
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        }).AllowAnonymous().DisableAntiforgery();
    }

    /// <summary>Turns Plaid and setup errors into readable 400s without leaking anything sensitive.</summary>
    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (PlaidException ex)
        {
            return Results.Problem(ex.DisplayMessage ?? $"Plaid error: {ex.ErrorCode}.", statusCode: StatusCodes.Status400BadRequest);
        }
        catch (PlaidSetupException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
