using System.Net;
using System.Text;
using System.Text.Json;
using IoT.Application.Common.Interfaces;
using IoT.Application.Common.Security;
using IoT.Domain.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Services;

/// <summary>
/// Conector HTTP de Red Local (inspirado en <c>thingsboard-gateway</c> REST/LAN Connector)
/// para descubrimiento, aprovisionamiento y control local de dispositivos ESP32 y ESP8266
/// en modo SoftAP (<c>:80/device</c>, <c>:80/ap</c>) y LAN Zeroconf (<c>:8081/zeroconf/*</c>
/// en texto claro o cifrado <c>AES-128-CBC</c> con <c>Key = MD5(deviceKey)</c>).
/// </summary>
public sealed class LanZeroconfConnectorService : ILanZeroconfConnectorService
{
    private static readonly HashSet<string> AllowedZeroconfEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "ap",
        "info",
        "wifi",
        "config",
        "switch",
        "switches",
        "startup",
        "pulse",
        "sled",
        "timers",
        "signal_strength",
        "ota_unlock",
        "ota_flash"
    };

    private static readonly HashSet<int> AllowedLanPorts = new()
    {
        80,
        8080,
        8081,
        8088
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LanZeroconfConnectorService> _logger;
    private readonly Microsoft.Extensions.Configuration.IConfiguration? _configuration;

    public LanZeroconfConnectorService(
        IHttpClientFactory httpClientFactory,
        ILogger<LanZeroconfConnectorService> logger,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _configuration = configuration;
    }

    public Result ValidateLanTargetHost(string host, int port)
    {
        if (_configuration != null && !_configuration.GetValue<bool>("LanConnector:Enabled", true))
        {
            return Result.Failure(new Error("LanConnector.Disabled", "El conector LAN del servidor está deshabilitado en este entorno."));
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return Result.Failure(new Error("LanConnector.InvalidHost", "La dirección IP o hostname local del dispositivo es obligatoria."));
        }

        if (!AllowedLanPorts.Contains(port))
        {
            return Result.Failure(new Error("LanConnector.InvalidPort", "Sólo se permiten puertos locales de aprovisionamiento/Zeroconf (80, 8080, 8081, 8088)."));
        }

        string trimmed = host.Trim();
        if (trimmed.Contains('/') || trimmed.Contains(':') || trimmed.Contains('@') || trimmed.Contains(' '))
        {
            return Result.Failure(new Error("LanConnector.InvalidHost", "El host local no debe incluir esquema, ruta ni credenciales."));
        }

        // Permitir nombres mDNS .local (p.ej. VESIS_A1B2C3.local)
        if (trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && trimmed.Length <= 64)
        {
            return Result.Success();
        }

        if (!IPAddress.TryParse(trimmed, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return Result.Failure(new Error(
                "LanConnector.InvalidHost",
                "Sólo se permiten direcciones IPv4 privadas de red local (192.168.x.x, 10.x.x.x, 172.16-31.x.x) o nombres mDNS '.local'."));
        }

        byte[] octets = ip.GetAddressBytes();
        bool isPrivateRfc1918 =
            octets[0] == 10 ||
            (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31) ||
            (octets[0] == 192 && octets[1] == 168);

        if (!isPrivateRfc1918)
        {
            return Result.Failure(new Error(
                "LanConnector.DisallowedIpRange",
                "Por seguridad (anti-SSRF), el conector LAN sólo se comunica con direcciones IPv4 privadas RFC1918 (10.x.x.x, 172.16-31.x.x, 192.168.x.x)."));
        }

        return Result.Success();
    }

    public async Task<Result<LanDeviceProbeResultDto>> ProbeDeviceAsync(
        string host,
        int port,
        string mode,
        string? deviceKey = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateLanTargetHost(host, port);
        if (validation.IsFailure)
        {
            return Result<LanDeviceProbeResultDto>.Failure(validation.Error);
        }

        string normalizedMode = string.Equals(mode, "softap", StringComparison.OrdinalIgnoreCase)
            ? "softap"
            : "lan_zeroconf";

        var client = _httpClientFactory.CreateClient("IoT.HttpDeviceTransport");
        string cleanHost = host.Trim();

        try
        {
            HttpResponseMessage response;
            if (normalizedMode == "softap")
            {
                string url = $"http://{cleanHost}:{port}/device";
                response = await client.GetAsync(url, cancellationToken);
            }
            else
            {
                string url = $"http://{cleanHost}:{port}/zeroconf/info";
                string reqBody = "{\"deviceid\":\"\",\"data\":{}}";
                using var content = new StringContent(reqBody, Encoding.UTF8, "application/json");
                response = await client.PostAsync(url, content, cancellationToken);
            }

            string rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Result<LanDeviceProbeResultDto>.Success(new LanDeviceProbeResultDto(
                    Reachable: false,
                    Host: cleanHost,
                    Port: port,
                    Mode: normalizedMode,
                    RawResponseJson: null,
                    ErrorMessage: $"HTTP {(int)response.StatusCode}"));
            }

            return Result<LanDeviceProbeResultDto>.Success(
                ParseProbeResponse(cleanHost, port, normalizedMode, rawJson, deviceKey));
        }
        catch (Exception ex)
        {
            _logger.LogInformation("No se pudo alcanzar el equipo local en {Host}:{Port} ({Mode}): {Message}",
                cleanHost, port, normalizedMode, ex.Message);

            return Result<LanDeviceProbeResultDto>.Success(new LanDeviceProbeResultDto(
                Reachable: false,
                Host: cleanHost,
                Port: port,
                Mode: normalizedMode,
                ErrorMessage: ex.Message));
        }
    }

    public async Task<Result<LanCommandExecutionResultDto>> SendLanCommandAsync(
        string host,
        int port,
        string endpoint,
        string innerDataJson,
        string? deviceId = null,
        string? deviceKey = null,
        bool encrypt = false,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateLanTargetHost(host, port);
        if (validation.IsFailure)
        {
            return Result<LanCommandExecutionResultDto>.Failure(validation.Error);
        }

        string cleanEndpoint = (endpoint ?? string.Empty).Trim().TrimStart('/');
        if (cleanEndpoint.StartsWith("zeroconf/", StringComparison.OrdinalIgnoreCase))
        {
            cleanEndpoint = cleanEndpoint["zeroconf/".Length..];
        }

        if (!AllowedZeroconfEndpoints.Contains(cleanEndpoint))
        {
            return Result<LanCommandExecutionResultDto>.Failure(new Error(
                "LanConnector.InvalidEndpoint",
                $"El endpoint local '{cleanEndpoint}' no está permitido."));
        }

        string normalizedData = string.IsNullOrWhiteSpace(innerDataJson) ? "{}" : innerDataJson.Trim();
        string requestBody;
        string url;

        if (string.Equals(cleanEndpoint, "ap", StringComparison.OrdinalIgnoreCase))
        {
            url = $"http://{host.Trim()}:{port}/ap";
            requestBody = normalizedData;
        }
        else
        {
            url = $"http://{host.Trim()}:{port}/zeroconf/{cleanEndpoint}";
            if (encrypt)
            {
                if (string.IsNullOrWhiteSpace(deviceKey))
                {
                    return Result<LanCommandExecutionResultDto>.Failure(new Error(
                        "LanConnector.MissingDeviceKey",
                        "Se requiere deviceKey para cifrar la petición LAN con AES-128-CBC."));
                }

                requestBody = LanZeroconfCrypto.BuildEncryptedEnvelopeJson(
                    normalizedData,
                    deviceKey,
                    deviceId ?? string.Empty);
            }
            else
            {
                using var dataDoc = JsonDocument.Parse(normalizedData);
                requestBody = LanZeroconfCrypto.BuildPlainEnvelopeJson(
                    deviceId ?? string.Empty,
                    dataDoc.RootElement.Clone());
            }
        }

        var client = _httpClientFactory.CreateClient("IoT.HttpDeviceTransport");
        try
        {
            using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(url, content, cancellationToken);
            string rawResp = await response.Content.ReadAsStringAsync(cancellationToken);

            int deviceError = 0;
            string? decryptedData = null;

            if (!string.IsNullOrWhiteSpace(rawResp))
            {
                try
                {
                    using var respDoc = JsonDocument.Parse(rawResp);
                    if (respDoc.RootElement.TryGetProperty("error", out var errProp) && errProp.TryGetInt32(out int parsedErr))
                    {
                        deviceError = parsedErr;
                    }

                    if (LanZeroconfCrypto.TryExtractDataJson(rawResp, deviceKey, out var extracted, out _))
                    {
                        decryptedData = extracted;
                    }
                }
                catch
                {
                    // Conservar respuesta cruda si el equipo no devuelve JSON estándar
                }
            }

            bool ok = response.IsSuccessStatusCode && deviceError == 0;
            return Result<LanCommandExecutionResultDto>.Success(new LanCommandExecutionResultDto(
                Success: ok,
                HttpStatusCode: (int)response.StatusCode,
                DeviceErrorCode: deviceError,
                Encrypted: encrypt,
                DecryptedDataJson: ok ? decryptedData : null,
                RawResponseJson: response.IsSuccessStatusCode ? rawResp : null,
                ErrorMessage: ok ? null : $"HTTP {(int)response.StatusCode}, error={deviceError}"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo al enviar comando LAN {Endpoint} a {Host}:{Port}", cleanEndpoint, host, port);
            return Result<LanCommandExecutionResultDto>.Success(new LanCommandExecutionResultDto(
                Success: false,
                HttpStatusCode: 0,
                DeviceErrorCode: -1,
                Encrypted: encrypt,
                ErrorMessage: ex.Message));
        }
    }

    private static LanDeviceProbeResultDto ParseProbeResponse(
        string host,
        int port,
        string mode,
        string rawJson,
        string? deviceKey)
    {
        try
        {
            string targetJson = rawJson;
            if (LanZeroconfCrypto.TryExtractDataJson(rawJson, deviceKey, out var innerJson, out _) &&
                !string.IsNullOrWhiteSpace(innerJson) &&
                innerJson != "{}")
            {
                targetJson = innerJson;
            }

            using var doc = JsonDocument.Parse(targetJson);
            var root = doc.RootElement;

            string? chipId = GetStringAny(root, "chipid", "chip_id", "deviceid", "device_id", "deviceId");
            string? mac = GetStringAny(root, "mac", "staMac", "bssid");
            string? model = GetStringAny(root, "device_type", "model", "device");
            string? fwVersion = GetStringAny(root, "fwVersion", "version", "fw_version");
            string? key = GetStringAny(root, "devicekey", "device_key", "apikey");
            string? ssid = GetStringAny(root, "ssid", "wifi_ssid");
            string? switchState = GetStringAny(root, "switch", "state");
            string? startup = GetStringAny(root, "startup", "configure");

            bool? pulseEnabled = null;
            if (root.TryGetProperty("pulse", out var pulseProp))
            {
                if (pulseProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    pulseEnabled = pulseProp.GetBoolean();
                else if (pulseProp.ValueKind == JsonValueKind.String)
                    pulseEnabled = string.Equals(pulseProp.GetString(), "on", StringComparison.OrdinalIgnoreCase);
            }

            int? pulseWidth = root.TryGetProperty("pulseWidth", out var pwProp) && pwProp.TryGetInt32(out int pw)
                ? pw
                : null;

            bool? sledOnline = null;
            if (root.TryGetProperty("sledOnline", out var sledProp))
            {
                if (sledProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    sledOnline = sledProp.GetBoolean();
                else if (sledProp.ValueKind == JsonValueKind.String)
                    sledOnline = string.Equals(sledProp.GetString(), "on", StringComparison.OrdinalIgnoreCase);
            }

            int? rssi = root.TryGetProperty("signalStrength", out var sigProp) && sigProp.TryGetInt32(out int sig)
                ? sig
                : (root.TryGetProperty("rssi", out var rssiProp) && rssiProp.TryGetInt32(out int r) ? r : null);

            return new LanDeviceProbeResultDto(
                Reachable: true,
                Host: host,
                Port: port,
                Mode: mode,
                ChipId: chipId,
                MacAddress: mac,
                Model: model,
                FirmwareVersion: fwVersion,
                DeviceKey: key,
                CurrentSsid: ssid,
                SwitchState: switchState,
                StartupMode: startup,
                PulseEnabled: pulseEnabled,
                PulseWidthMs: pulseWidth,
                SledOnline: sledOnline,
                RssiDbm: rssi,
                RawResponseJson: rawJson);
        }
        catch (Exception ex)
        {
            return new LanDeviceProbeResultDto(
                Reachable: true,
                Host: host,
                Port: port,
                Mode: mode,
                RawResponseJson: rawJson,
                ErrorMessage: $"Respuesta recibida pero no se pudo decodificar JSON: {ex.Message}");
        }
    }

    private static string? GetStringAny(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                string? val = prop.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                    return val.Trim();
            }
        }
        return null;
    }
}

