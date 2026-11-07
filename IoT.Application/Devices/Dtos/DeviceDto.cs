using System.Text.Json;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Domain.Enums;

namespace IoT.Application.Devices.Dtos;

public sealed record DeviceDto(
    Guid Id,
    Guid TenantId,
    string ChipId,
    string MacAddress,
    string Name,
    string Model,
    string Manufacturer,
    string AdminStatus,
    string OperationalStatus,
    DateTime? LastSeenAt,
    DateTime CreatedAt,
    IReadOnlyList<string> Capabilities,
    LivePositionSnapshot? LatestPosition,
    // Nodo dueño en la jerarquía Customer (ADR-013). Nullable: espeja Device.CustomerId
    // (nullable en el dominio durante la transición E8.1) y el contrato del frontend
    // (customerId?/customerName? en DeviceDetailPage). Null = "Sin asignar".
    Guid? CustomerId,
    string? CustomerName,
    IReadOnlyDictionary<string, double>? LatestMetrics = null,
    string? FirmwareVersion = null,
    int? ChannelCount = null,
    string? WifiSsid = null,
    string? LocalIp = null,
    string? Room = null,
    string? DisplayAs = null,
    bool LanControlEnabled = true,
    bool PushNoticeEnabled = true,
    bool OfflineAlertEnabled = true,
    IReadOnlyDictionary<string, string>? DeviceSettings = null);

public static class DeviceDisplayAsCatalog
{
    private static readonly HashSet<string> AllowedValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "default",
        "switch",
        "outlet",
        "light",
        "fan",
        "refrigerator",
        "curtain",
        "rice_cooker",
        "computer",
        "heater",
        "television",
        "air_conditioner"
    };

    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = (raw ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (normalized == "electric_heater")
            normalized = "heater";
        if (normalized == "tv")
            normalized = "television";
        if (normalized == "ac")
            normalized = "air_conditioner";
        return AllowedValues.Contains(normalized);
    }
}

public sealed record ResolvedDeviceAttributes(
    string? FirmwareVersion,
    int? ChannelCount,
    string? WifiSsid,
    string? LocalIp,
    string? Room,
    string? DisplayAs,
    bool LanControlEnabled,
    bool PushNoticeEnabled,
    bool OfflineAlertEnabled,
    IReadOnlyDictionary<string, string>? DeviceSettings);

public static class DeviceAttributeProjection
{
    private static readonly HashSet<string> HardwareSettingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "startup",
        "pulse",
        "pulseWidth",
        "sledOnline"
    };

    public static ResolvedDeviceAttributes Resolve(
        Device device,
        IReadOnlyList<DeviceAttribute>? attributes,
        int? baseChannelCount,
        string? fallbackPeerIp = null)
    {
        string? firmwareVersion = string.IsNullOrWhiteSpace(device.FirmwareVersion) ? null : device.FirmwareVersion.Trim();
        int? channelCount = baseChannelCount;
        string? wifiSsid = null;
        string? localIp = null;
        string? room = null;
        string? displayAs = null;
        bool lanControlEnabled = true;
        bool pushNoticeEnabled = true;
        bool offlineAlertEnabled = true;
        Dictionary<string, string>? settings = null;

        if (attributes is not null)
        {
            foreach (var attr in attributes)
            {
                string cleanVal = UnquoteIfJsonString(attr.Value);
                if (string.IsNullOrWhiteSpace(cleanVal))
                    continue;

                if (string.Equals(attr.Key, "wifi_ssid", StringComparison.OrdinalIgnoreCase))
                {
                    wifiSsid = cleanVal;
                }
                else if (string.Equals(attr.Key, "local_ip", StringComparison.OrdinalIgnoreCase))
                {
                    localIp = cleanVal;
                }
                else if (string.Equals(attr.Key, "channel_count", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(cleanVal, out int attrCh) && attrCh is >= 1 and <= 16)
                {
                    channelCount = Math.Max(channelCount ?? 1, attrCh);
                }
                else if ((string.Equals(attr.Key, "firmware_version", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(attr.Key, "fwVersion", StringComparison.OrdinalIgnoreCase)) &&
                         string.IsNullOrWhiteSpace(firmwareVersion))
                {
                    firmwareVersion = cleanVal;
                }
                else if (attr.Scope == AttributeScope.Shared &&
                         string.Equals(attr.Key, "room", StringComparison.OrdinalIgnoreCase))
                {
                    room = cleanVal;
                }
                else if (attr.Scope == AttributeScope.Shared &&
                         string.Equals(attr.Key, "display_as", StringComparison.OrdinalIgnoreCase) &&
                         DeviceDisplayAsCatalog.TryNormalize(cleanVal, out string normDisplay))
                {
                    displayAs = normDisplay;
                }
                else if (attr.Scope == AttributeScope.Shared &&
                         string.Equals(attr.Key, "lan_control_enabled", StringComparison.OrdinalIgnoreCase) &&
                         TryParseBool(cleanVal, out bool lanVal))
                {
                    lanControlEnabled = lanVal;
                }
                else if (attr.Scope == AttributeScope.Shared &&
                         string.Equals(attr.Key, "push_notice_enabled", StringComparison.OrdinalIgnoreCase) &&
                         TryParseBool(cleanVal, out bool pushVal))
                {
                    pushNoticeEnabled = pushVal;
                }
                else if (attr.Scope == AttributeScope.Shared &&
                         string.Equals(attr.Key, "offline_alert_enabled", StringComparison.OrdinalIgnoreCase) &&
                         TryParseBool(cleanVal, out bool offlineVal))
                {
                    offlineAlertEnabled = offlineVal;
                }
                else if (HardwareSettingKeys.Contains(attr.Key))
                {
                    settings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string canonicalKey = NormalizeSettingKey(attr.Key);
                    if (attr.Scope == AttributeScope.Client || !settings.ContainsKey(canonicalKey))
                    {
                        settings[canonicalKey] = cleanVal;
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(localIp) && !string.IsNullOrWhiteSpace(fallbackPeerIp))
        {
            localIp = fallbackPeerIp.Trim();
        }

        return new ResolvedDeviceAttributes(
            firmwareVersion,
            channelCount,
            wifiSsid,
            localIp,
            room,
            displayAs,
            lanControlEnabled,
            pushNoticeEnabled,
            offlineAlertEnabled,
            settings);
    }

    public static string UnquoteIfJsonString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        string trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(trimmed)?.Trim() ?? string.Empty;
            }
            catch
            {
                return trimmed[1..^1].Trim();
            }
        }

        return trimmed;
    }

    private static bool TryParseBool(string raw, out bool result)
    {
        if (bool.TryParse(raw, out result))
            return true;
        if (raw is "1" or "on" or "ON")
        {
            result = true;
            return true;
        }
        if (raw is "0" or "off" or "OFF")
        {
            result = false;
            return true;
        }
        result = false;
        return false;
    }

    private static string NormalizeSettingKey(string key)
    {
        if (string.Equals(key, "pulseWidth", StringComparison.OrdinalIgnoreCase))
            return "pulseWidth";
        if (string.Equals(key, "sledOnline", StringComparison.OrdinalIgnoreCase))
            return "sledOnline";
        return key.ToLowerInvariant();
    }
}

public static class SwitchChannelCountResolver
{
    public static int? Resolve(Device device, IReadOnlyDictionary<string, double>? latestMetrics)
    {
        bool isSwitch = device.HasCapability(CapabilityType.Switch) ||
                        device.Model.Contains("relay", StringComparison.OrdinalIgnoreCase) ||
                        device.Model.Contains("switch", StringComparison.OrdinalIgnoreCase);

        int maxCh = 0;
        if (latestMetrics is not null)
        {
            if (latestMetrics.TryGetValue("channel_count", out double cc) && cc >= 1)
                maxCh = Math.Max(maxCh, (int)Math.Round(cc));
            if (latestMetrics.TryGetValue("devices_count", out double dc) && dc >= 1)
                maxCh = Math.Max(maxCh, (int)Math.Round(dc));

            for (int i = 1; i <= 16; i++)
            {
                if (latestMetrics.ContainsKey($"switch_{i}") ||
                    latestMetrics.ContainsKey($"relay_state_{i}") ||
                    latestMetrics.ContainsKey($"relay_{i}_state_binary") ||
                    latestMetrics.ContainsKey($"device_switch{i}_state_binary") ||
                    latestMetrics.ContainsKey($"{device.ChipId}-{i:D2}_state_binary"))
                {
                    maxCh = Math.Max(maxCh, i);
                }
            }
        }

        if (!isSwitch && maxCh == 0)
            return null;

        string modelLower = (device.Model ?? string.Empty).ToLowerInvariant();
        string nameLower = (device.Name ?? string.Empty).ToLowerInvariant();
        for (int n = 16; n >= 2; n--)
        {
            if (modelLower.Contains($"{n}ch", StringComparison.Ordinal) ||
                modelLower.Contains($"{n}-ch", StringComparison.Ordinal) ||
                modelLower.Contains($"{n}-gang", StringComparison.Ordinal) ||
                modelLower.Contains($"{n}gang", StringComparison.Ordinal) ||
                nameLower.Contains($"{n} canales", StringComparison.Ordinal))
            {
                maxCh = Math.Max(maxCh, n);
                break;
            }
        }

        if (maxCh < 2 && (modelLower.Contains("dual", StringComparison.Ordinal) ||
                          nameLower.Contains("doble", StringComparison.Ordinal) ||
                          nameLower.Contains("dual", StringComparison.Ordinal)))
        {
            maxCh = 2;
        }

        return Math.Clamp(maxCh > 0 ? maxCh : 1, 1, 16);
    }
}
