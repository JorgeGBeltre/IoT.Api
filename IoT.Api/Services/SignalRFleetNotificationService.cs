using IoT.Api.Hubs;
using IoT.Application.Common.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace IoT.Api.Services;

public sealed class SignalRFleetNotificationService : IFleetNotificationService
{
    private readonly IHubContext<FleetHub> _hubContext;
    private readonly ILogger<SignalRFleetNotificationService> _logger;

    public SignalRFleetNotificationService(
        IHubContext<FleetHub> hubContext,
        ILogger<SignalRFleetNotificationService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyPositionUpdatedAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        string deviceName,
        double latitude,
        double longitude,
        float? speedKph,
        float? heading,
        DateTime recordedAt,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            deviceId,
            chipId,
            name = deviceName,
            latitude,
            longitude,
            speedKph,
            heading,
            recordedAt
        };

        // Notify both the tenant-wide fleet view and individual device views
        string tenantGroup = $"tenant_{tenantId}";
        string deviceGroup = $"device_{deviceId}";

        await _hubContext.Clients.Group(tenantGroup).SendAsync("DevicePositionUpdated", payload, cancellationToken);
        await _hubContext.Clients.Group(deviceGroup).SendAsync("DevicePositionUpdated", payload, cancellationToken);
    }

    public async Task NotifyTelemetryReceivedAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        IReadOnlyDictionary<string, double> metrics,
        DateTime recordedAt,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            deviceId,
            chipId,
            metrics,
            recordedAt
        };

        string tenantGroup = $"tenant_{tenantId}";
        string deviceGroup = $"device_{deviceId}";

        await _hubContext.Clients.Group(tenantGroup).SendAsync("TelemetryReceived", payload, cancellationToken);
        await _hubContext.Clients.Group(deviceGroup).SendAsync("TelemetryReceived", payload, cancellationToken);
    }

    public async Task NotifyDeviceAlertAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        string alertType,
        string message,
        DateTime timestamp,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            deviceId,
            chipId,
            alertType,
            message,
            timestamp
        };

        string tenantGroup = $"tenant_{tenantId}";
        await _hubContext.Clients.Group(tenantGroup).SendAsync("DeviceAlertReceived", payload, cancellationToken);
    }

    public async Task NotifyCommandStatusUpdatedAsync(
        Guid tenantId,
        Guid deviceId,
        Guid commandId,
        string status,
        string? resultJson,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            deviceId,
            commandId,
            status,
            result = resultJson,
            updatedAt
        };

        string tenantGroup = $"tenant_{tenantId}";
        string deviceGroup = $"device_{deviceId}";

        await _hubContext.Clients.Group(tenantGroup).SendAsync("CommandStatusUpdated", payload, cancellationToken);
        await _hubContext.Clients.Group(deviceGroup).SendAsync("CommandStatusUpdated", payload, cancellationToken);
    }

    public async Task NotifyOtaProgressAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        string version,
        int currentChunk,
        int totalChunks,
        int percentage,
        string status,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            deviceId,
            chipId,
            version,
            currentChunk,
            totalChunks,
            percentage,
            status,
            errorMessage,
            timestamp = DateTime.UtcNow
        };

        string tenantGroup = $"tenant_{tenantId}";
        string deviceGroup = $"device_{deviceId}";

        await _hubContext.Clients.Group(tenantGroup).SendAsync("DeviceOtaProgressUpdated", payload, cancellationToken);
        await _hubContext.Clients.Group(deviceGroup).SendAsync("DeviceOtaProgressUpdated", payload, cancellationToken);
    }

    public async Task NotifyDeviceStatusChangedAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        string status,
        DateTime timestamp,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            deviceId,
            chipId,
            status,
            timestamp
        };

        string tenantGroup = $"tenant_{tenantId}";
        string deviceGroup = $"device_{deviceId}";

        await _hubContext.Clients.Group(tenantGroup).SendAsync("DeviceStatusUpdated", payload, cancellationToken);
        await _hubContext.Clients.Group(deviceGroup).SendAsync("DeviceStatusUpdated", payload, cancellationToken);
    }
}
