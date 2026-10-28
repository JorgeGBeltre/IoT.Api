using IoT.Domain.Common;

namespace IoT.Application.Common.Interfaces;

/// <summary>
/// Servicio de operaciones de flota expuesto hacia herramientas de IA supervisada (Model Context Protocol).
/// </summary>
public interface IMcpFleetService
{
    Task<object> ListFleetStatusAsync(Guid tenantId, string? statusFilter = null, CancellationToken cancellationToken = default);
    Task<object?> GetDeviceTelemetryAsync(Guid tenantId, string deviceIdOrChipId, CancellationToken cancellationToken = default);
    Task<object> GetDeviceEventsAsync(Guid tenantId, Guid? deviceId = null, int limit = 20, CancellationToken cancellationToken = default);
    Task<Result<Guid>> SendDeviceCommandAsync(Guid tenantId, Guid deviceId, string commandName, string? payloadJson = null, CancellationToken cancellationToken = default);
}
