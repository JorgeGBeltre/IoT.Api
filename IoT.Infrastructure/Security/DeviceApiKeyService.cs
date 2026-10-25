using System.Security.Cryptography;
using System.Text;
using IoT.Application.Common.Interfaces;

namespace IoT.Infrastructure.Security;

/// <summary>
/// Implementación de <see cref="IDeviceApiKeyService"/>.
/// - Clave: 32 bytes aleatorios (CSPRNG) en Base64Url, con prefijo "iotk_".
/// - Hash: SHA-256 en hex minúsculas.
/// - Huella: primeros 8 caracteres hex del hash (no reversible, identifica la versión de la clave).
/// </summary>
public sealed class DeviceApiKeyService : IDeviceApiKeyService
{
    private const string Prefix = "iotk_";

    public (string PlainKey, string Hash, string Fingerprint) Generate()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        string plainKey = Prefix + Base64UrlEncode(bytes);
        string hash = ComputeHash(plainKey);
        string fingerprint = hash[..8];
        return (plainKey, hash, fingerprint);
    }

    public bool Verify(string plainKey, string hash)
    {
        if (string.IsNullOrEmpty(plainKey) || string.IsNullOrEmpty(hash))
            return false;

        string computed = ComputeHash(plainKey);
        // Comparación en tiempo constante para no filtrar información por timing.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(hash));
    }

    public string ComputeHash(string plainKey)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(plainKey));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
