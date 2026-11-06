using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmoothBudget.Api.Plaid;

/// <summary>Plaid settings from environment variables (PLAID_*). Secrets never go in appsettings.</summary>
public class PlaidOptions
{
    public string? ClientId { get; set; }
    public string? Secret { get; set; }
    /// <summary>"sandbox" or "production".</summary>
    public string Environment { get; set; } = "sandbox";
    /// <summary>Public HTTPS URL of /api/plaid/webhook. Empty locally, where Plaid can't reach the app.</summary>
    public string? WebhookUrl { get; set; }
    /// <summary>Base64 32-byte key that encrypts access tokens at rest.</summary>
    public string? TokenKey { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(Secret) && !string.IsNullOrWhiteSpace(TokenKey);
    public bool IsSandbox => string.Equals(Environment, "sandbox", StringComparison.OrdinalIgnoreCase);

    public Uri BaseAddress => Environment.ToLowerInvariant() switch
    {
        "sandbox" => new Uri("https://sandbox.plaid.com/"),
        "production" => new Uri("https://production.plaid.com/"),
        _ => throw new InvalidOperationException($"Unknown PLAID_ENV '{Environment}'. Use sandbox or production."),
    };
}

/// <summary>An error response from Plaid. Holds Plaid's codes and messages only, never the request body.</summary>
public class PlaidException(string errorType, string errorCode, string message, string? displayMessage)
    : Exception($"Plaid {errorType}/{errorCode}: {message}")
{
    public string ErrorType { get; } = errorType;
    public string ErrorCode { get; } = errorCode;
    public string? DisplayMessage { get; } = displayMessage;
    public bool LoginRequired => ErrorCode is "ITEM_LOGIN_REQUIRED" or "PENDING_EXPIRATION" or "ACCESS_NOT_GRANTED";
}

#pragma warning disable CA1812 // instantiated by System.Text.Json
public record PlaidBalances(decimal? Current, decimal? Available, string? IsoCurrencyCode);
public record PlaidAccount(string AccountId, PlaidBalances Balances, string? Mask, string Name, string? OfficialName, string Type, string? Subtype);
public record PlaidCategory(string? Primary, string? Detailed, string? ConfidenceLevel = null);
public record PlaidTransaction(
    string TransactionId, string AccountId, decimal Amount, string? IsoCurrencyCode, DateOnly Date, DateOnly? AuthorizedDate,
    string Name, string? MerchantName, bool Pending, string? PendingTransactionId, PlaidCategory? PersonalFinanceCategory);
public record PlaidRemoved(string TransactionId);
public record TransactionsSyncResponse(List<PlaidTransaction> Added, List<PlaidTransaction> Modified, List<PlaidRemoved> Removed,
    string NextCursor, bool HasMore, List<PlaidAccount>? Accounts);
public record PlaidItemInfo(string ItemId, string? InstitutionId);
public record AccountsGetResponse(List<PlaidAccount> Accounts, PlaidItemInfo Item);
public record ExchangeResponse(string AccessToken, string ItemId);
public record PlaidJwk(string Alg, string Crv, string Kid, string Kty, string X, string Y, long? ExpiredAt);
#pragma warning restore CA1812

/// <summary>
/// A thin client for the handful of Plaid endpoints the app uses (Plaid has no official .NET SDK).
/// Request bodies carry the client id, secret and access token, so they're never logged.
/// </summary>
public class PlaidClient(HttpClient http, PlaidOptions options)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<string> CreateLinkTokenAsync(string clientUserId, string? accessTokenForUpdate, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["client_name"] = "Smooth Budget",
            ["user"] = new { client_user_id = clientUserId },
            ["country_codes"] = new[] { "US" },
            ["language"] = "en",
            ["webhook"] = string.IsNullOrWhiteSpace(options.WebhookUrl) ? null : options.WebhookUrl,
        };
        if (accessTokenForUpdate is null)
        {
            body["products"] = new[] { "transactions" };
            body["transactions"] = new { days_requested = 730 };
        }
        else
        {
            body["access_token"] = accessTokenForUpdate; // update mode: re-authenticate an existing item
        }
        var r = await PostAsync<JsonElement>("link/token/create", body, ct);
        return r.GetProperty("link_token").GetString()!;
    }

    public Task<ExchangeResponse> ExchangePublicTokenAsync(string publicToken, CancellationToken ct) =>
        PostAsync<ExchangeResponse>("item/public_token/exchange", new { public_token = publicToken }, ct);

    public Task<AccountsGetResponse> GetAccountsAsync(string accessToken, CancellationToken ct) =>
        PostAsync<AccountsGetResponse>("accounts/get", new { access_token = accessToken }, ct);

    public async Task<string?> GetInstitutionNameAsync(string institutionId, CancellationToken ct)
    {
        var r = await PostAsync<JsonElement>("institutions/get_by_id", new { institution_id = institutionId, country_codes = new[] { "US" } }, ct);
        return r.GetProperty("institution").GetProperty("name").GetString();
    }

    public Task<TransactionsSyncResponse> SyncTransactionsAsync(string accessToken, string? cursor, CancellationToken ct) =>
        PostAsync<TransactionsSyncResponse>("transactions/sync", new { access_token = accessToken, cursor, count = 500 }, ct);

    public Task RemoveItemAsync(string accessToken, CancellationToken ct) =>
        PostAsync<JsonElement>("item/remove", new { access_token = accessToken }, ct);

    public async Task<PlaidJwk> GetWebhookKeyAsync(string keyId, CancellationToken ct)
    {
        var r = await PostAsync<JsonElement>("webhook_verification_key/get", new { key_id = keyId }, ct);
        return r.GetProperty("key").Deserialize<PlaidJwk>(Json)!;
    }

    // Sandbox only.
    public async Task<string> SandboxCreatePublicTokenAsync(string institutionId, string? overrideUsername, CancellationToken ct)
    {
        var r = await PostAsync<JsonElement>("sandbox/public_token/create", new
        {
            institution_id = institutionId,
            initial_products = new[] { "transactions" },
            options = new
            {
                override_username = overrideUsername,
                override_password = overrideUsername is null ? null : "pass_good", // Plaid's published sandbox password
                webhook = string.IsNullOrWhiteSpace(options.WebhookUrl) ? null : options.WebhookUrl,
            },
        }, ct);
        return r.GetProperty("public_token").GetString()!;
    }

    public Task RefreshTransactionsAsync(string accessToken, CancellationToken ct) =>
        PostAsync<JsonElement>("transactions/refresh", new { access_token = accessToken }, ct);

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        if (!options.IsConfigured)
            throw new InvalidOperationException("Plaid is not configured. Set PLAID_CLIENT_ID, PLAID_SECRET and PLAID_TOKEN_KEY.");

        // Credentials go in headers (Plaid supports PLAID-CLIENT-ID / PLAID-SECRET), not the logged URL.
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        req.Headers.Add("PLAID-CLIENT-ID", options.ClientId);
        req.Headers.Add("PLAID-SECRET", options.Secret);
        req.Headers.Add("Plaid-Version", "2020-09-14");

        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            JsonElement err = default;
            try { err = await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct); } catch (JsonException) { }
            string? Get(string name) => err.ValueKind == JsonValueKind.Object && err.TryGetProperty(name, out var v) ? v.GetString() : null;
            throw new PlaidException(Get("error_type") ?? "HTTP", Get("error_code") ?? ((int)res.StatusCode).ToString(),
                Get("error_message") ?? res.ReasonPhrase ?? "Request failed", Get("display_message"));
        }
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }
}
