using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IoT.Application.Common.Security;

/// <summary>
/// Criptografía de transporte LAN Zeroconf (:8081 /zeroconf/*) compatible con los firmwares
/// ESP32 y ESP8266 de la flota.
/// <para>
/// Especificación de protocolo verificada en firmware (<c>CryptoUtils.cpp</c>):
/// <list type="bullet">
///   <item><description>Derivación de clave simétrica de 128 bits: <c>Key = MD5(UTF8(deviceKey))</c>.</description></item>
///   <item><description>Cifrado de bloque: <c>AES-128-CBC</c> con relleno estándar <c>PKCS#7</c>.</description></item>
///   <item><description>Vector de inicialización (<c>iv</c>): 16 bytes aleatorios codificados en Base64.</description></item>
///   <item><description>Carga cifrada (<c>data</c>): texto cifrado codificado en Base64 dentro del sobre JSON.</description></item>
/// </list>
/// </para>
/// </summary>
public static class LanZeroconfCrypto
{
    /// <summary>
    /// Deriva la clave AES-128 de 16 bytes a partir del <paramref name="deviceKey"/> usando MD5
    /// según el contrato del servidor HTTP local :8081 de los dispositivos ESP32/ESP8266.
    /// </summary>
    public static byte[] DeriveKey(string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
            throw new ArgumentException("El deviceKey es obligatorio para derivar la clave LAN.", nameof(deviceKey));

#pragma warning disable CA5351 // MD5 es exigido por el contrato de hardware del protocolo LAN /zeroconf/*
        return MD5.HashData(Encoding.UTF8.GetBytes(deviceKey.Trim()));
#pragma warning restore CA5351
    }

    /// <summary>
    /// Cifra el JSON de comando/configuración (<paramref name="plainJsonData"/>) con AES-128-CBC + PKCS#7
    /// y devuelve la tupla <c>(IvBase64, DataBase64)</c>.
    /// </summary>
    public static (string IvBase64, string DataBase64) EncryptPayload(
        string plainJsonData,
        string deviceKey,
        byte[]? customIv = null)
    {
        if (plainJsonData is null)
            throw new ArgumentNullException(nameof(plainJsonData));

        byte[] key = DeriveKey(deviceKey);
        byte[] iv = customIv is { Length: 16 } ? customIv : RandomNumberGenerator.GetBytes(16);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainJsonData);

        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;

        using var encryptor = aes.CreateEncryptor();
        byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        return (Convert.ToBase64String(iv), Convert.ToBase64String(cipherBytes));
    }

    /// <summary>
    /// Descifra un bloque <c>data</c> en Base64 usando el <c>iv</c> en Base64 y el <paramref name="deviceKey"/>.
    /// </summary>
    public static string DecryptPayload(string base64Cipher, string base64Iv, string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(base64Cipher))
            throw new ArgumentException("El payload cifrado en Base64 no puede estar vacío.", nameof(base64Cipher));
        if (string.IsNullOrWhiteSpace(base64Iv))
            throw new ArgumentException("El IV en Base64 no puede estar vacío.", nameof(base64Iv));

        byte[] key = DeriveKey(deviceKey);
        byte[] iv = Convert.FromBase64String(base64Iv.Trim());
        if (iv.Length != 16)
            throw new CryptographicException("El IV de /zeroconf/* debe tener exactamente 16 bytes.");

        byte[] cipherBytes = Convert.FromBase64String(base64Cipher.Trim());
        if (cipherBytes.Length == 0 || cipherBytes.Length % 16 != 0)
            throw new CryptographicException("La longitud del texto cifrado AES-128-CBC debe ser múltiplo de 16 bytes.");

        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>
    /// Construye el sobre JSON completo cifrado con AES-128-CBC listo para enviar por HTTP POST
    /// a <c>http://&lt;ip&gt;:8081/zeroconf/*</c>.
    /// </summary>
    public static string BuildEncryptedEnvelopeJson(
        string plainJsonData,
        string deviceKey,
        string deviceId,
        string? selfApikey = null,
        long? sequence = null,
        byte[]? customIv = null)
    {
        var (ivB64, dataB64) = EncryptPayload(plainJsonData, deviceKey, customIv);
        string seq = (sequence ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();

        var envelope = new Dictionary<string, object?>
        {
            ["sequence"] = seq,
            ["deviceid"] = deviceId,
            ["selfApikey"] = string.IsNullOrWhiteSpace(selfApikey) ? "00000000-0000-0000-0000-000000000000" : selfApikey,
            ["iv"] = ivB64,
            ["encrypt"] = true,
            ["data"] = dataB64
        };

        return JsonSerializer.Serialize(envelope);
    }

    /// <summary>
    /// Construye el sobre JSON en texto claro para <c>http://&lt;ip&gt;:8081/zeroconf/*</c>.
    /// </summary>
    public static string BuildPlainEnvelopeJson(
        string deviceId,
        object dataObject,
        long? sequence = null)
    {
        string seq = (sequence ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
        var envelope = new Dictionary<string, object?>
        {
            ["sequence"] = seq,
            ["deviceid"] = deviceId,
            ["encrypt"] = false,
            ["data"] = dataObject
        };

        return JsonSerializer.Serialize(envelope);
    }

    /// <summary>
    /// Extrae o descifra el objeto <c>data</c> de una petición o respuesta <c>/zeroconf/*</c>,
    /// soportando tanto sobres cifrados (<c>"encrypt": true</c>) como texto plano.
    /// </summary>
    public static bool TryExtractDataJson(
        string envelopeJson,
        string? deviceKey,
        out string innerDataJson,
        out string? errorMessage)
    {
        innerDataJson = "{}";
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(envelopeJson))
        {
            errorMessage = "El sobre JSON está vacío.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(envelopeJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errorMessage = "El sobre JSON debe ser un objeto.";
                return false;
            }

            bool isEncrypted = root.TryGetProperty("encrypt", out var encProp) &&
                               (encProp.ValueKind == JsonValueKind.True ||
                                (encProp.ValueKind == JsonValueKind.String &&
                                 string.Equals(encProp.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

            if (isEncrypted)
            {
                if (string.IsNullOrWhiteSpace(deviceKey))
                {
                    errorMessage = "El sobre está cifrado pero no se proporcionó deviceKey.";
                    return false;
                }

                string? ivB64 = root.TryGetProperty("iv", out var ivProp) ? ivProp.GetString() : null;
                string? dataB64 = root.TryGetProperty("data", out var dataProp) ? dataProp.GetString() : null;

                if (string.IsNullOrWhiteSpace(ivB64) || string.IsNullOrWhiteSpace(dataB64))
                {
                    errorMessage = "El sobre cifrado no contiene 'iv' o 'data' válidos.";
                    return false;
                }

                innerDataJson = DecryptPayload(dataB64, ivB64, deviceKey);
                return true;
            }

            if (root.TryGetProperty("data", out var plainDataProp))
            {
                innerDataJson = plainDataProp.ValueKind == JsonValueKind.String
                    ? (plainDataProp.GetString() ?? "{}")
                    : plainDataProp.GetRawText();
                return true;
            }

            innerDataJson = root.GetRawText();
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}

