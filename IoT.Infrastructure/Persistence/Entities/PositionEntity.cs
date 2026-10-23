using IoT.Domain.Common;
using NetTopologySuite.Geometries;

namespace IoT.Infrastructure.Persistence.Entities;

public sealed class PositionEntity : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime RecordedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public Point Location { get; set; } = null!;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public float? SpeedKph { get; set; }
    public float? Heading { get; set; }
    public float? AltitudeM { get; set; }
    public short? Satellites { get; set; }
    public bool IsValid { get; set; }
    public long Version { get; set; }
    public string Source { get; set; } = "real";
    public string? MessageId { get; set; }
}
