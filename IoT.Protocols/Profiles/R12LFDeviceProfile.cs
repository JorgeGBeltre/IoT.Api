using IoT.Domain.Enums;
using IoT.Protocols.Adapters;
using IoT.Protocols.Contracts;

namespace IoT.Protocols.Profiles;

public sealed class R12LFDeviceProfile : IDeviceProfile
{
    public string ProfileId => "r12l-f-gps-tracker";
    public string Manufacturer => "SEEWORLD";
    public string Model => "R12L-F";
    public string DisplayName => "SEEWORLD R12L-F Rastreador Satelital 4G (TCP Binario)";
    public string PrimaryTransport => "TCP-R12LF";

    public IReadOnlyList<CapabilityType> SupportedCapabilities { get; } =
    [
        CapabilityType.Gps,
        CapabilityType.Health
    ];

    public IReadOnlyList<string> TelemetryTopicPatterns { get; } =
    [
        "tcp/r12lf/{deviceId}",
        "r12lf/{deviceId}/telemetry",
        "devices/{deviceId}/gps",
        "devices/{deviceId}/heartbeat",
        "devices/{deviceId}/login"
    ];

    public string CommandTopicPattern => "devices/{deviceId}/commands";

    public IDeviceProtocolAdapter Adapter { get; }

    public R12LFDeviceProfile(IDeviceProtocolAdapter? adapter = null)
    {
        Adapter = adapter ?? new R12LFProtocolAdapter();
    }

    public bool SupportsTransport(string transportName) =>
        string.Equals(transportName, "TCP-R12LF", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(transportName, "TCP", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(transportName, "MQTT", StringComparison.OrdinalIgnoreCase);
}
