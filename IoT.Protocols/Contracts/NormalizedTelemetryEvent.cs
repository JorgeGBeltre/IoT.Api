using IoT.Domain.ValueObjects;

namespace IoT.Protocols.Contracts;

public sealed record NormalizedTelemetryEvent
{
    public string DeviceIdentifier { get; set; } = string.Empty;
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public GeoPosition? Position { get; set; }
    public Dictionary<string, double> NumericMetrics { get; set; } = [];
    public Dictionary<string, string> Attributes { get; set; } = [];
    public string? MessageId { get; set; }
    public string? RawPayload { get; set; }
}
