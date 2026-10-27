namespace IoT.Application.Common.Interfaces;

/// <summary>
/// Mensaje normalizado de telemetría para encolado y particionado secuencial por origen (ADR-016).
/// </summary>
public sealed record TelemetryQueueItem(
    string Topic,
    string Payload,
    string Source = "real",
    string? TransportProtocol = null,
    string? TransportDeviceIdentifier = null,
    string? TransportMessageId = null,
    Guid? CredentialTenantId = null,
    DateTimeOffset EnqueuedAt = default);

/// <summary>
/// Publicador hacia la cola o stream de telemetría particionado (ThingsBoard Pattern 2: Sequential by Originator).
/// </summary>
public interface ITelemetryQueuePublisher
{
    Task<bool> EnqueueAsync(
        TelemetryQueueItem item,
        CancellationToken cancellationToken = default);
}
