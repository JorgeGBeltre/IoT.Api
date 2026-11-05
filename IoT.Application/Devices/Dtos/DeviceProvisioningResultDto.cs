using System.Text.Json;
using IoT.Application.Common.Security;

namespace IoT.Application.Devices.Dtos;

/// <summary>
/// Paquetes listos para inyectar en el firmware según el transporte de adopción elegido
/// (Bluetooth BLE Schema A/B, SoftAP Wi-Fi HTTP, Red Local LAN /zeroconf/* plano o cifrado
/// AES-128-CBC, y Cable USB Web Serial AT+).
/// </summary>
public sealed record DeviceProvisioningBundlesDto(
    string BleSchemaAJson,
    string BleSchemaAKeyValue,
    string BleSchemaBJson,
    string SoftApPayloadJson,
    string LanZeroconfWifiPlainJson,
    string LanZeroconfConfigPlainJson,
    string LanZeroconfConfigEncryptedJson,
    IReadOnlyList<string> SerialAtCommands);

/// <summary>
/// Resultado del aprovisionamiento multi-modo. Devuelve las credenciales recién emitidas
/// CON la <c>ApiKey</c> en texto plano (única oportunidad para inyectarla al firmware),
/// el <c>DeviceKey</c> para cifrado LAN AES-128-CBC (<c>MD5(deviceKey)</c>), el <c>BindToken</c>
/// de reclamación (Device Claiming), los paquetes listos por transporte y una bandera que
/// indica si el dispositivo se dio de alta en esta llamada (<c>true</c>) o ya existía en el
/// tenant y se re-emparejó rotando su clave (<c>false</c>).
/// </summary>
public sealed record DeviceProvisioningResultDto(
    bool Created,
    DeviceCredentialsDto Credentials,
    string? DeviceKey = null,
    string? BindToken = null,
    string? PairingMode = null,
    DeviceProvisioningBundlesDto? Bundles = null);

/// <summary>
/// Constructor determinista de paquetes de aprovisionamiento multi-transporte para todos
/// los firmwares ESP32 y ESP8266 de la plataforma.
/// </summary>
public static class DeviceProvisioningBundleBuilder
{
    public static DeviceProvisioningBundlesDto Build(
        DeviceCredentialsDto credentials,
        string deviceKey,
        string bindToken,
        string? wifiSsid = null,
        string? wifiPassword = null,
        IReadOnlyDictionary<string, string?>? deviceParameters = null)
    {
        string ssid = wifiSsid?.Trim() ?? string.Empty;
        string pass = wifiPassword ?? string.Empty;
        bool hasWifi = !string.IsNullOrWhiteSpace(ssid);
        string apiKey = credentials.MqttPasswordPlaintext ?? credentials.MqttUsername ?? string.Empty;
        string chipId = credentials.ChipId;
        string host = credentials.BrokerHost;
        int port = credentials.BrokerPort;

        // 1. BLE Schema A (Service 12345678-1234-1234-1234-123456789abc, Char ...0014)
        var bleSchemaA = new Dictionary<string, object?>
        {
            ["server"] = host,
            ["port"] = port,
            ["account"] = apiKey,
            ["mqtt_user"] = apiKey,
            ["mqtt_pass"] = apiKey,
            ["device_id"] = chipId,
            ["devicekey"] = deviceKey,
            ["bind_token"] = bindToken
        };
        if (hasWifi)
        {
            bleSchemaA["ssid"] = ssid;
            bleSchemaA["pass"] = pass;
            bleSchemaA["password"] = pass;
        }
        MergeNonSensitiveParameters(bleSchemaA, deviceParameters);
        string bleSchemaAJson = JsonSerializer.Serialize(bleSchemaA);

        var kvParts = new List<string>();
        if (hasWifi)
        {
            kvParts.Add($"ssid={SanitizeKv(ssid)}");
            kvParts.Add($"pass={SanitizeKv(pass)}");
        }
        kvParts.Add($"server={SanitizeKv(host)}");
        kvParts.Add($"port={port}");
        kvParts.Add($"account={SanitizeKv(apiKey)}");
        kvParts.Add($"bind_token={SanitizeKv(bindToken)}");
        if (deviceParameters != null && deviceParameters.TryGetValue("apn", out var apnVal) && !string.IsNullOrWhiteSpace(apnVal))
        {
            kvParts.Add($"apn={SanitizeKv(apnVal)}");
        }
        if (deviceParameters != null && deviceParameters.TryGetValue("apn_user", out var apnUserVal) && !string.IsNullOrWhiteSpace(apnUserVal))
        {
            kvParts.Add($"apn_user={SanitizeKv(apnUserVal)}");
        }
        string bleSchemaAKv = string.Join(";", kvParts);

        // 2. BLE Schema B (Service 4fafc201-1fb5-459e-8fcc-c5c9c331914b, Char beb5483e-...-ea07361b26a8)
        var bleSchemaB = new Dictionary<string, object?>
        {
            ["action"] = "provision",
            ["server"] = host,
            ["port"] = port,
            ["account"] = apiKey,
            ["apikey"] = apiKey,
            ["devicekey"] = deviceKey,
            ["bind_token"] = bindToken
        };
        if (hasWifi)
        {
            bleSchemaB["ssid"] = ssid;
            bleSchemaB["password"] = pass;
            bleSchemaB["pass"] = pass;
        }
        MergeNonSensitiveParameters(bleSchemaB, deviceParameters);
        string bleSchemaBJson = JsonSerializer.Serialize(bleSchemaB);

        // 3. SoftAP Compatible Payload (POST http://192.168.4.1/ap o http://10.10.7.1/ap)
        var softAp = new Dictionary<string, object?>
        {
            ["server"] = host,
            ["mqtt_broker"] = host,
            ["serverName"] = host,
            ["port"] = port,
            ["mqtt_port"] = port,
            ["account"] = apiKey,
            ["mqtt_user"] = apiKey,
            ["mqtt_pass"] = apiKey,
            ["apikey"] = apiKey,
            ["devicekey"] = deviceKey,
            ["bind_token"] = bindToken,
            ["token"] = bindToken,
            ["deviceid"] = chipId
        };
        if (hasWifi)
        {
            softAp["ssid"] = ssid;
            softAp["password"] = pass;
        }
        MergeNonSensitiveParameters(softAp, deviceParameters);
        string softApJson = JsonSerializer.Serialize(softAp);

        // 4. LAN Zeroconf (:8081 /zeroconf/wifi y /zeroconf/config)
        var lanWifiData = new Dictionary<string, object?>();
        if (hasWifi)
        {
            lanWifiData["ssid"] = ssid;
            lanWifiData["password"] = pass;
        }
        string lanWifiPlainJson = LanZeroconfCrypto.BuildPlainEnvelopeJson(chipId, lanWifiData);

        var lanConfigData = new Dictionary<string, object?>
        {
            ["server"] = host,
            ["port"] = port,
            ["account"] = apiKey,
            ["mqtt_pass"] = apiKey,
            ["devicekey"] = deviceKey,
            ["bind_token"] = bindToken
        };
        if (hasWifi)
        {
            lanConfigData["ssid"] = ssid;
            lanConfigData["password"] = pass;
        }
        MergeNonSensitiveParameters(lanConfigData, deviceParameters);
        string lanConfigPlainJson = LanZeroconfCrypto.BuildPlainEnvelopeJson(chipId, lanConfigData);
        string lanConfigInnerJson = JsonSerializer.Serialize(lanConfigData);
        string lanConfigEncryptedJson = LanZeroconfCrypto.BuildEncryptedEnvelopeJson(
            lanConfigInnerJson,
            deviceKey,
            chipId,
            selfApikey: apiKey);

        // 5. Comandos USB Serial (AT+ y JSON por UART)
        var serialCommands = new List<string>
        {
            "AT+VER",
            "AT+CHIPID?",
            "AT+DEVID?",
            "AT+DEVMODEL?",
            $"AT+PROV={softApJson}"
        };

        return new DeviceProvisioningBundlesDto(
            BleSchemaAJson: bleSchemaAJson,
            BleSchemaAKeyValue: bleSchemaAKv,
            BleSchemaBJson: bleSchemaBJson,
            SoftApPayloadJson: softApJson,
            LanZeroconfWifiPlainJson: lanWifiPlainJson,
            LanZeroconfConfigPlainJson: lanConfigPlainJson,
            LanZeroconfConfigEncryptedJson: lanConfigEncryptedJson,
            SerialAtCommands: serialCommands);
    }

    private static void MergeNonSensitiveParameters(
        IDictionary<string, object?> target,
        IReadOnlyDictionary<string, string?>? parameters)
    {
        if (parameters is null) return;

        foreach (var (key, rawValue) in parameters)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(rawValue))
                continue;

            string trimmedKey = key.Trim();
            if (Commands.ProvisionDeviceCommandHandler.IsSensitiveParameterKey(trimmedKey))
                continue;

            string trimmedVal = rawValue.Trim();

            if (bool.TryParse(trimmedVal, out bool bVal))
            {
                target[trimmedKey] = bVal;
            }
            else if (int.TryParse(trimmedVal, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int iVal) &&
                     !trimmedKey.Contains("phone", StringComparison.OrdinalIgnoreCase) &&
                     !trimmedKey.Contains("dni", StringComparison.OrdinalIgnoreCase) &&
                     !trimmedKey.Contains("code", StringComparison.OrdinalIgnoreCase))
            {
                target[trimmedKey] = iVal;
            }
            else if (double.TryParse(trimmedVal, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double dVal) &&
                     !trimmedKey.Contains("phone", StringComparison.OrdinalIgnoreCase) &&
                     !trimmedKey.Contains("dni", StringComparison.OrdinalIgnoreCase) &&
                     !trimmedKey.Contains("code", StringComparison.OrdinalIgnoreCase))
            {
                target[trimmedKey] = dVal;
            }
            else
            {
                target[trimmedKey] = trimmedVal;
            }
        }
    }

    private static string SanitizeKv(string value) =>
        value.Replace(";", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
}
