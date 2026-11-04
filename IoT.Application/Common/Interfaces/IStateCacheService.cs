using IoT.Domain.ValueObjects;

namespace IoT.Application.Common.Interfaces;

public sealed record LivePositionSnapshot
{
    public Guid DeviceId { get; init; }
    public string DeviceIdentifier { get; init; } = string.Empty;
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public float? SpeedKph { get; init; }
    public float? Heading { get; init; }
    public DateTime RecordedAt { get; init; }
    public DateTime ReceivedAt { get; init; }
    public long Version { get; init; }
}

public interface IStateCacheService
{
    Task<bool> TryUpdateLatestPositionAsync(Guid tenantId, Guid deviceId, string deviceIdentifier, GeoPosition position, DateTime recordedAt, CancellationToken cancellationToken = default);
    Task<LivePositionSnapshot?> GetLatestPositionAsync(Guid tenantId, Guid deviceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LivePositionSnapshot>> GetFleetSnapshotAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<bool> IsDuplicateMessageAsync(string deviceIdentifier, DateTime recordedAt, string payloadHash, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task<bool> IsStaleDeviceStateVersionAsync(Guid tenantId, Guid deviceId, long stateVersion, CancellationToken cancellationToken = default);
}
