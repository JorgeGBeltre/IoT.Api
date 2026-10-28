namespace IoT.Application.Common.Interfaces;

public sealed record EventItemDto(
    Guid Id,
    Guid? DeviceId,
    string? DeviceName,
    string? ChipId,
    string EventType,      // "alert", "command", "audit", "system"
    string Severity,       // "critical", "warning", "info"
    string Title,
    string Message,
    string? DataJson,
    DateTime TimestampUtc
);

public interface IEventQueryService
{
    Task<IReadOnlyList<EventItemDto>> GetEventsAsync(
        Guid tenantId,
        Guid? deviceId = null,
        string? severity = null,
        string? eventType = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        int limit = 100,
        CancellationToken cancellationToken = default);
}
