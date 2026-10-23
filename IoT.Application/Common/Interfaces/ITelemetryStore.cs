using IoT.Domain.ValueObjects;

namespace IoT.Application.Common.Interfaces;

public sealed record HistoricalPositionDto(
    DateTime RecordedAt,
    double Latitude,
    double Longitude,
    float? SpeedKph,
    float? Heading,
    float? AltitudeM,
    short? Satellites,
    bool IsValid,
    string Source = "real",
    string? MessageId = null);

public sealed record HistoricalTelemetryDto(
    DateTime RecordedAt,
    Dictionary<string, double> Metrics,
    string? RawPayload,
    string Source = "real",
    string? MessageId = null);

public interface ITelemetryStore
{
    Task StorePositionAsync(
        Guid tenantId,
        Guid deviceId,
        DateTime recordedAt,
        DateTime receivedAt,
        GeoPosition position,
        long version,
        string source = "real",
        string? messageId = null,
        CancellationToken cancellationToken = default);

    Task StoreTelemetryMetricsAsync(
        Guid tenantId,
        Guid deviceId,
        DateTime recordedAt,
        DateTime receivedAt,
        Dictionary<string, double> metrics,
        string? rawPayload,
        string source = "real",
        string? messageId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HistoricalPositionDto>> GetPositionsAsync(
        Guid tenantId,
        Guid deviceId,
        DateTime fromUtc,
        DateTime toUtc,
        int limit = 1000,
        string? source = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HistoricalTelemetryDto>> GetTelemetryAsync(
        Guid tenantId,
        Guid deviceId,
        DateTime fromUtc,
        DateTime toUtc,
        int limit = 1000,
        string? source = null,
        CancellationToken cancellationToken = default);

    Task<int> PurgePositionsOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);
    Task<int> PurgeTelemetryRecordsOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);
}
