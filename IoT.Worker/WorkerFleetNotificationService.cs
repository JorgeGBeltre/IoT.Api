using IoT.Application.Common.Interfaces;

namespace IoT.Worker;

/// <summary>
/// Implementación de <see cref="IFleetNotificationService"/> para el proceso de fondo (IoT.Worker).
/// El worker no aloja hubs SignalR (los aloja IoT.Api); las notificaciones son no-op o delegan
/// la actualización de estado a través de la persistencia y Redis.
/// </summary>
public sealed class WorkerFleetNotificationService : IFleetNotificationService
{
    public Task NotifyPositionUpdatedAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        string deviceName,
        double latitude,
        double longitude,
        float? speedKph,
        float? heading,
        DateTime recordedAt,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyTelemetryReceivedAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        IReadOnlyDictionary<string, double> metrics,
        DateTime recordedAt,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyDeviceAlertAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        string alertType,
        string message,
        DateTime timestamp,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyCommandStatusUpdatedAsync(
        Guid tenantId,
        Guid deviceId,
        Guid commandId,
        string status,
        string? resultJson,
        DateTime updatedAt,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyOtaProgressAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        string version,
        int currentChunk,
        int totalChunks,
        int percentage,
        string status,
        string? errorMessage = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyDeviceStatusChangedAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        string status,
        DateTime timestamp,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
