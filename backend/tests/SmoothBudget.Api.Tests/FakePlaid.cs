using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmoothBudget.Api.Plaid;

namespace SmoothBudget.Api.Tests;

/// <summary>
/// An in-process stand-in for Plaid's API, plugged in as PlaidClient's HTTP handler. Tests script
/// accounts and /transactions/sync pages per access token; every request is recorded.
/// All data is invented.
/// </summary>
public sealed class FakePlaid : HttpMessageHandler
{
    public sealed record Account(string Id, string Name, string Mask, string Type, string Subtype, decimal Balance = 100m, decimal? Limit = null);

    /// <summary>Card details for /liabilities/get, by access token. A login without an entry answers like a bank that doesn't share them.</summary>
    public ConcurrentDictionary<string, List<(string AccountShortName, decimal Apr, decimal Minimum, string DueDate)>> Liabilities { get; } = new();

    private int _items;
    private readonly ConcurrentDictionary<string, List<Account>> _accounts = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<JsonObject>> _pages = new();
    private readonly ConcurrentDictionary<string, string> _publicTokens = new();
    public ConcurrentQueue<(string Path, string Body, string? ClientId, string? Secret)> Requests { get; } = new();
    public ConcurrentDictionary<string, string> SyncErrors { get; } = new(); // access token -> error_code

    /// <summary>
    /// Registers a bank login and returns the public token Link would hand the app. Like real Plaid,
    /// account and transaction ids are unique per login: tests use short names ("acc-card", "p1") and the
    /// fake appends a per-login suffix (see <see cref="Id"/>).
    /// </summary>
    public string NewLogin(params Account[] accounts)
    {
        var n = Interlocked.Increment(ref _items);
        var publicToken = $"public-sandbox-fake-{n}";
        var accessToken = $"access-sandbox-fake-{n}-{Guid.NewGuid():N}";
        _publicTokens[publicToken] = accessToken;
        _accounts[accessToken] = [.. accounts.Select(a => a with { Id = Id(accessToken, a.Id) })];
        _pages[accessToken] = new();
        return publicToken;
    }

    public string AccessTokenFor(string publicToken) => _publicTokens[publicToken];

    /// <summary>Changes an account's balance, as the bank would report it on the next sync.</summary>
    public void SetBalance(string accessToken, string shortName, decimal balance) =>
        _accounts[accessToken] = [.. _accounts[accessToken].Select(a => a.Id == Id(accessToken, shortName) ? a with { Balance = balance } : a)];
    public static string ItemIdFor(string accessToken) => "item-" + accessToken.Split('-')[3];

    /// <summary>The real id Plaid would use for a test's short name under this login.</summary>
    public static string Id(string accessToken, string shortName) => $"{shortName}-{accessToken.Split('-')[3]}";

    public void QueueSync(string accessToken, IEnumerable<JsonObject>? added = null, IEnumerable<JsonObject>? modified = null,
        IEnumerable<string>? removed = null, bool hasMore = false)
    {
        JsonNode Scope(JsonObject t)
        {
            var copy = JsonNode.Parse(t.ToJsonString())!.AsObject();
            foreach (var key in new[] { "transaction_id", "account_id", "pending_transaction_id" })
            {
                if (copy[key]?.GetValue<string>() is { } v)
                    copy[key] = Id(accessToken, v);
            }
            return copy;
        }
        var page = new JsonObject
        {
            ["added"] = new JsonArray([.. (added ?? []).Select(Scope)]),
            ["modified"] = new JsonArray([.. (modified ?? []).Select(Scope)]),
            ["removed"] = new JsonArray([.. (removed ?? []).Select(id => (JsonNode)new JsonObject { ["transaction_id"] = Id(accessToken, id) })]),
            ["has_more"] = hasMore,
        };
        _pages[accessToken].Enqueue(page);
    }

    public static JsonObject Txn(string id, string accountId, decimal amount, string date, string name, bool pending = false,
        string? pendingId = null, string primary = "FOOD_AND_DRINK", string detailed = "FOOD_AND_DRINK_RESTAURANT", string? authorized = null) => new()
    {
        ["transaction_id"] = id,
        ["account_id"] = accountId,
        ["amount"] = amount,
        ["iso_currency_code"] = "USD",
        ["date"] = date,
        ["authorized_date"] = authorized,
        ["name"] = name,
        ["merchant_name"] = null,
        ["pending"] = pending,
        ["pending_transaction_id"] = pendingId,
        ["personal_finance_category"] = new JsonObject { ["primary"] = primary, ["detailed"] = detailed },
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue((path, body,
            request.Headers.TryGetValues("PLAID-CLIENT-ID", out var c) ? c.Single() : null,
            request.Headers.TryGetValues("PLAID-SECRET", out var s) ? s.Single() : null));
        var req = JsonNode.Parse(body)!.AsObject();
        string? Str(string name) => req[name]?.GetValue<string>();

        switch (path)
        {
            case "link/token/create":
                return Ok(new JsonObject { ["link_token"] = "link-sandbox-fake", ["expiration"] = "2030-01-01T00:00:00Z" });
            case "item/public_token/exchange":
                var access = _publicTokens.GetValueOrDefault(Str("public_token") ?? "");
                return access is null ? Error("INVALID_INPUT", "INVALID_PUBLIC_TOKEN")
                    : Ok(new JsonObject { ["access_token"] = access, ["item_id"] = ItemIdFor(access) });
            case "accounts/get":
                return Ok(new JsonObject
                {
                    ["accounts"] = AccountsJson(Str("access_token")!),
                    ["item"] = new JsonObject { ["item_id"] = ItemIdFor(Str("access_token")!), ["institution_id"] = "ins_fake" },
                });
            case "institutions/get_by_id":
                return Ok(new JsonObject { ["institution"] = new JsonObject { ["name"] = "Fake Bank" } });
            case "item/remove":
                return Ok(new JsonObject());
            case "transactions/sync":
                var token = Str("access_token")!;
                if (SyncErrors.TryRemove(token, out var code))
                    return Error("ITEM_ERROR", code);
                var cursor = Str("cursor") ?? "";
                var page = _pages[token].TryDequeue(out var p) ? p : new JsonObject
                {
                    ["added"] = new JsonArray(), ["modified"] = new JsonArray(), ["removed"] = new JsonArray(), ["has_more"] = false,
                };
                page["next_cursor"] = cursor + "+";
                page["accounts"] = AccountsJson(token);
                return Ok(page);
            case "liabilities/get":
                var lt = Str("access_token")!;
                if (!Liabilities.TryGetValue(lt, out var cards))
                    return Error("INVALID_REQUEST", "PRODUCTS_NOT_SUPPORTED");
                return Ok(new JsonObject
                {
                    ["liabilities"] = new JsonObject
                    {
                        ["credit"] = new JsonArray([.. cards.Select(c => (JsonNode)new JsonObject
                        {
                            ["account_id"] = Id(lt, c.AccountShortName),
                            ["aprs"] = new JsonArray(new JsonObject { ["apr_percentage"] = c.Apr, ["apr_type"] = "purchase_apr" }),
                            ["minimum_payment_amount"] = c.Minimum,
                            ["next_payment_due_date"] = c.DueDate,
                        })]),
                    },
                });
            default:
                return Error("INVALID_REQUEST", "UNKNOWN_FAKE_PATH");
        }
    }

    private JsonArray AccountsJson(string accessToken) => new([.. _accounts[accessToken].Select(a => (JsonNode)new JsonObject
    {
        ["account_id"] = a.Id,
        ["balances"] = new JsonObject { ["current"] = a.Balance, ["available"] = a.Balance, ["iso_currency_code"] = "USD", ["limit"] = a.Limit },
        ["mask"] = a.Mask, ["name"] = a.Name, ["official_name"] = null, ["type"] = a.Type, ["subtype"] = a.Subtype,
    })]);

    private static HttpResponseMessage Ok(JsonObject o) => new(HttpStatusCode.OK) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(string type, string code) => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(new JsonObject
        {
            ["error_type"] = type, ["error_code"] = code, ["error_message"] = "fake error", ["display_message"] = null,
        }.ToJsonString(), Encoding.UTF8, "application/json"),
    };
}

/// <summary>Signs webhooks the way Plaid does, with a test key the verifier is told about.</summary>
public sealed class FakeWebhookKeys : IPlaidWebhookKeySource, IDisposable
{
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string KeyId { get; } = "test-key-" + Guid.NewGuid().ToString("N")[..8];

    public Task<PlaidJwk> GetKeyAsync(string keyId, CancellationToken ct)
    {
        if (keyId != KeyId) throw new KeyNotFoundException();
        var p = Key.ExportParameters(false);
        return Task.FromResult(new PlaidJwk("ES256", "P-256", KeyId, "EC", B64Url(p.Q.X!), B64Url(p.Q.Y!), null));
    }

    public string Sign(byte[] body, DateTimeOffset issuedAt, ECDsa? signWith = null, string alg = "ES256")
    {
        var header = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg, kid = KeyId, typ = "JWT" }));
        var payload = B64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = issuedAt.ToUnixTimeSeconds(),
            request_body_sha256 = Convert.ToHexStringLower(SHA256.HashData(body)),
        }));
        var sig = (signWith ?? Key).SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{header}.{payload}.{B64Url(sig)}";
    }

    public static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => Key.Dispose();
}
