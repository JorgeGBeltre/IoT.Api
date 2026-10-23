using IoT.Domain.Common;

namespace IoT.Infrastructure.Persistence.Entities;

public sealed class TelemetryRecordEntity : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime RecordedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string MetricsJson { get; set; } = "{}";
    public string? RawPayload { get; set; }
    public string Source { get; set; } = "real";
    public string? MessageId { get; set; }
}
