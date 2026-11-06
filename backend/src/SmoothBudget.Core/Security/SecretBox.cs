using System.Security.Cryptography;
using System.Text;

namespace SmoothBudget.Core.Security;

/// <summary>
/// Encrypts small secrets (Plaid access tokens) for storage with AES-256-GCM.
/// Output: base64 of [version 1][12-byte nonce][ciphertext][16-byte tag]. A wrong key or any
/// tampering fails to decrypt instead of returning garbage.
/// </summary>
public sealed class SecretBox
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    /// <param name="base64Key">A 32-byte key, base64-encoded (from the PLAID_TOKEN_KEY environment variable).</param>
    public SecretBox(string base64Key)
    {
        byte[] key;
        try { key = Convert.FromBase64String(base64Key ?? ""); }
        catch (FormatException) { throw new ArgumentException("The encryption key must be base64."); }
        if (key.Length != 32)
            throw new ArgumentException("The encryption key must be 32 bytes (base64 of 32 random bytes).");
        _key = key;
    }

    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public string Encrypt(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[1 + NonceSize + plain.Length + TagSize];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(1 + NonceSize, plain.Length), output.AsSpan(1 + NonceSize + plain.Length, TagSize));
        return Convert.ToBase64String(output);
    }

    public string Decrypt(string sealedText)
    {
        var data = Convert.FromBase64String(sealedText);
        if (data.Length < 1 + NonceSize + TagSize || data[0] != Version)
            throw new CryptographicException("Unrecognized encrypted value.");
        var cipherLength = data.Length - 1 - NonceSize - TagSize;
        var plain = new byte[cipherLength];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(data.AsSpan(1, NonceSize), data.AsSpan(1 + NonceSize, cipherLength), data.AsSpan(1 + NonceSize + cipherLength, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
