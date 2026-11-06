using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmoothBudget.Api.Plaid;

/// <summary>Where verification keys come from (Plaid's /webhook_verification_key/get; a fake in tests).</summary>
public interface IPlaidWebhookKeySource
{
    Task<PlaidJwk> GetKeyAsync(string keyId, CancellationToken ct);
}

public class PlaidApiKeySource(PlaidClient plaid) : IPlaidWebhookKeySource
{
    public Task<PlaidJwk> GetKeyAsync(string keyId, CancellationToken ct) => plaid.GetWebhookKeyAsync(keyId, ct);
}

/// <summary>
/// Verifies the Plaid-Verification header on incoming webhooks, following Plaid's documented scheme:
/// an ES256 JWT whose key id is looked up from Plaid, signed no more than 5 minutes ago, carrying the
/// SHA-256 of the exact request body. Anything else is rejected, so nobody can fake a webhook.
/// </summary>
public class PlaidWebhookVerifier(IPlaidWebhookKeySource keys, TimeProvider clock, ILogger<PlaidWebhookVerifier> log)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, PlaidJwk> Cache = new();

    public async Task<bool> VerifyAsync(string? jwt, byte[] body, CancellationToken ct)
    {
        try
        {
            var parts = jwt?.Split('.');
            if (parts is not { Length: 3 })
                return false;

            using var header = JsonDocument.Parse(B64Url(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "ES256")
                return false;
            var kid = header.RootElement.GetProperty("kid").GetString();
            if (string.IsNullOrEmpty(kid))
                return false;

            if (!Cache.TryGetValue(kid, out var jwk))
            {
                jwk = await keys.GetKeyAsync(kid, ct);
                Cache[kid] = jwk;
            }
            if (jwk.ExpiredAt is not null)
                return false;

            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = B64Url(jwk.X), Y = B64Url(jwk.Y) },
            });
            var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            if (!ecdsa.VerifyData(signed, B64Url(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return false;

            using var payload = JsonDocument.Parse(B64Url(parts[1]));
            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(payload.RootElement.GetProperty("iat").GetInt64());
            var age = clock.GetUtcNow() - issuedAt;
            if (age > MaxAge || age < -MaxAge)
                return false;

            var claimed = payload.RootElement.GetProperty("request_body_sha256").GetString() ?? "";
            var actual = Convert.ToHexStringLower(SHA256.HashData(body));
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(claimed), Encoding.ASCII.GetBytes(actual));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException
                                       or CryptographicException or PlaidException or ArgumentException)
        {
            log.LogWarning("Rejected a Plaid webhook: {Reason}", ex.GetType().Name);
            return false;
        }
    }

    public static byte[] B64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }
}
