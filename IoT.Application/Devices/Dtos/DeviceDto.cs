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
    int? ChannelCount = null);

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
