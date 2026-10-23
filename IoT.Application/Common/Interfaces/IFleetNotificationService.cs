namespace IoT.Application.Common.Interfaces;

public interface IFleetNotificationService
{
    Task NotifyPositionUpdatedAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        string deviceName,
        double latitude,
        double longitude,
        float? speedKph,
        float? heading,
        DateTime recordedAt,
        CancellationToken cancellationToken = default);

    Task NotifyTelemetryReceivedAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        IReadOnlyDictionary<string, double> metrics,
        DateTime recordedAt,
        CancellationToken cancellationToken = default);

    Task NotifyDeviceAlertAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        string alertType,
        string message,
        DateTime timestamp,
        CancellationToken cancellationToken = default);

    Task NotifyCommandStatusUpdatedAsync(
        Guid tenantId,
        Guid deviceId,
        Guid commandId,
        string status,
        string? resultJson,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);
}
