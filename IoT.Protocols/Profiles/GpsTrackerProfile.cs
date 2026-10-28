using IoT.Domain.Enums;
using IoT.Protocols.Adapters;
using IoT.Protocols.Contracts;

namespace IoT.Protocols.Profiles;

public sealed class GpsTrackerProfile : IDeviceProfile
{
    public string ProfileId => "mqtt-gps-tracker";
    public string Manufacturer => "Generic";
    public string Model => "MQTT-GPS-Tracker";
    public string DisplayName => "Rastreador GPS Flota Urbana (MQTT)";
    public string PrimaryTransport => "MQTT";

    // Dispositivo MQTT nativo: no entra por el gateway TCP, así que no tiene familia ni puerto.
    public string? TcpFamily => null;

    public IReadOnlyList<CapabilityType> SupportedCapabilities { get; } =
    [
        CapabilityType.Gps
    ];

    public IReadOnlyList<string> TelemetryTopicPatterns { get; } =
    [
        "telemetry",
        "devices/{deviceId}/gps",
        "telemetry/gps/{deviceId}",
        "telemetry/{deviceId}/gps",
        "telemetry/{deviceId}/accidents",
        "devices/{deviceId}/telemetry"
    ];

    public string CommandTopicPattern => "devices/{deviceId}/commands";

    public IDeviceProtocolAdapter Adapter { get; }

    public GpsTrackerProfile(IDeviceProtocolAdapter? adapter = null)
    {
        Adapter = adapter ?? new GpsAccidentAdapter();
    }

    public bool SupportsTransport(string transportName) =>
        string.Equals(transportName, "MQTT", StringComparison.OrdinalIgnoreCase);
}
