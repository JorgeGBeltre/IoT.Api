using IoT.Application.Common.Interfaces;
using IoT.Domain.Enums;
using IoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoT.Infrastructure.Services;

public sealed class EventQueryService : IEventQueryService
{
    private readonly ApplicationDbContext _context;

    public EventQueryService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<EventItemDto>> GetEventsAsync(
        Guid tenantId,
        Guid? deviceId = null,
        string? severity = null,
        string? eventType = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var clampedLimit = Math.Clamp(limit, 1, 1000);
        var events = new List<EventItemDto>();

        // Resuelve los dispositivos visibles del tenant (respeta RLS y filtros de subárbol)
        var devicesQuery = _context.Devices.AsNoTracking().Where(d => d.TenantId == tenantId);
        if (deviceId.HasValue)
        {
            devicesQuery = devicesQuery.Where(d => d.Id == deviceId.Value);
        }

        var visibleDevices = await devicesQuery
            .Select(d => new { d.Id, d.Name, d.ChipId })
            .ToDictionaryAsync(d => d.Id, cancellationToken);

        if (deviceId.HasValue && !visibleDevices.ContainsKey(deviceId.Value))
        {
            // El dispositivo no existe en el ámbito visible del usuario (fail-closed)
            return Array.Empty<EventItemDto>();
        }

        var shouldIncludeCommands = string.IsNullOrWhiteSpace(eventType) ||
                                    eventType.Equals("command", StringComparison.OrdinalIgnoreCase);

        var shouldIncludeAuditOrAlerts = string.IsNullOrWhiteSpace(eventType) ||
                                         !eventType.Equals("command", StringComparison.OrdinalIgnoreCase);

        // 1. Comandos de dispositivos
        if (shouldIncludeCommands)
        {
            var cmdQuery = _context.DeviceCommands.AsNoTracking().Where(c => c.TenantId == tenantId);

            if (deviceId.HasValue)
            {
                cmdQuery = cmdQuery.Where(c => c.DeviceId == deviceId.Value);
            }

            if (fromUtc.HasValue)
            {
                cmdQuery = cmdQuery.Where(c => (c.ConfirmedAt ?? c.DeliveredAt ?? c.SentAt ?? c.CreatedAt) >= fromUtc.Value);
            }

            if (toUtc.HasValue)
            {
                cmdQuery = cmdQuery.Where(c => (c.ConfirmedAt ?? c.DeliveredAt ?? c.SentAt ?? c.CreatedAt) <= toUtc.Value);
            }

            var commands = await cmdQuery
                .OrderByDescending(c => c.ConfirmedAt ?? c.DeliveredAt ?? c.SentAt ?? c.CreatedAt)
                .Take(clampedLimit)
                .ToListAsync(cancellationToken);

            foreach (var c in commands)
            {
                visibleDevices.TryGetValue(c.DeviceId, out var dev);

                var cmdSeverity = (c.Status == CommandStatus.Failed || c.Status == CommandStatus.TimedOut)
                    ? "critical"
                    : (c.Status == CommandStatus.Confirmed ? "info" : "warning");

                var time = c.ConfirmedAt ?? c.DeliveredAt ?? c.SentAt ?? c.CreatedAt;

                events.Add(new EventItemDto(
                    Id: c.Id,
                    DeviceId: c.DeviceId,
                    DeviceName: dev?.Name,
                    ChipId: dev?.ChipId,
                    EventType: "command",
                    Severity: cmdSeverity,
                    Title: $"Comando: {c.CommandName}",
                    Message: $"Comando '{c.CommandName}' ({c.Status}) solicitado por {c.RequestedBy}{(string.IsNullOrEmpty(c.ErrorMessage) ? "" : $": {c.ErrorMessage}")}",
                    DataJson: c.ResultJson ?? c.PayloadJson,
                    TimestampUtc: time
                ));
            }
        }

        // 2. Logs de auditoría y alertas operativas
        if (shouldIncludeAuditOrAlerts)
        {
            var auditQuery = _context.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenantId);

            if (fromUtc.HasValue)
            {
                auditQuery = auditQuery.Where(a => a.TimestampUtc >= fromUtc.Value);
            }

            if (toUtc.HasValue)
            {
                auditQuery = auditQuery.Where(a => a.TimestampUtc <= toUtc.Value);
            }

            // Si está acotado a un dispositivo, filtramos por su ChipId o Nombre
            if (deviceId.HasValue)
            {
                var targetDev = visibleDevices[deviceId.Value];
                auditQuery = auditQuery.Where(a =>
                    a.Username == targetDev.ChipId ||
                    a.Details.Contains(targetDev.ChipId) ||
                    a.Details.Contains(targetDev.Name));
            }

            var logs = await auditQuery
                .OrderByDescending(a => a.TimestampUtc)
                .Take(clampedLimit)
                .ToListAsync(cancellationToken);

            foreach (var a in logs)
            {
                var isAlert = a.Action.StartsWith("DeviceAlert", StringComparison.OrdinalIgnoreCase) ||
                              a.Action.StartsWith("Alert", StringComparison.OrdinalIgnoreCase);

                var logSeverity = "info";
                if (a.Action.Contains("Emergency", StringComparison.OrdinalIgnoreCase) ||
                    a.Action.Contains("Impact", StringComparison.OrdinalIgnoreCase) ||
                    a.Action.Contains("Rollover", StringComparison.OrdinalIgnoreCase) ||
                    a.Action.Contains("SOS", StringComparison.OrdinalIgnoreCase))
                {
                    logSeverity = "critical";
                }
                else if (a.Action.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
                         a.Action.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
                         a.Action.Contains("Suspended", StringComparison.OrdinalIgnoreCase) ||
                         a.Action.Contains("Alarm", StringComparison.OrdinalIgnoreCase))
                {
                    logSeverity = "warning";
                }

                // Identificar si corresponde a alguno de los dispositivos visibles
                Guid? matchedDeviceId = null;
                string? matchedName = null;
                string? matchedChipId = null;

                if (deviceId.HasValue)
                {
                    matchedDeviceId = deviceId.Value;
                    matchedName = visibleDevices[deviceId.Value].Name;
                    matchedChipId = visibleDevices[deviceId.Value].ChipId;
                }
                else
                {
                    foreach (var dev in visibleDevices.Values)
                    {
                        if (a.Username == dev.ChipId || a.Details.Contains(dev.ChipId))
                        {
                            matchedDeviceId = dev.Id;
                            matchedName = dev.Name;
                            matchedChipId = dev.ChipId;
                            break;
                        }
                    }
                }

                events.Add(new EventItemDto(
                    Id: a.Id,
                    DeviceId: matchedDeviceId,
                    DeviceName: matchedName,
                    ChipId: matchedChipId,
                    EventType: isAlert ? "alert" : "audit",
                    Severity: logSeverity,
                    Title: a.Action,
                    Message: a.Details,
                    DataJson: null,
                    TimestampUtc: a.TimestampUtc
                ));
            }
        }

        // Filtro por severidad si fue especificada
        IEnumerable<EventItemDto> filtered = events;
        if (!string.IsNullOrWhiteSpace(severity))
        {
            filtered = filtered.Where(e => e.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            filtered = filtered.Where(e => e.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase));
        }

        return filtered
            .OrderByDescending(e => e.TimestampUtc)
            .Take(clampedLimit)
            .ToList();
    }
}
