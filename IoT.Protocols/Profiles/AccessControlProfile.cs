using IoT.Domain.Enums;
using IoT.Protocols.Adapters;
using IoT.Protocols.Contracts;

namespace IoT.Protocols.Profiles;

public sealed class AccessControlProfile : IDeviceProfile
{
    public string ProfileId => "access-control-v1";
    public string Manufacturer => "Generic";
    public string Model => "MQTT-AccessControl-NFC-QR";
    public string DisplayName => "Control de Acceso & NFC/QR (MQTT)";
    public string PrimaryTransport => "MQTT";

    public IReadOnlyList<CapabilityType> SupportedCapabilities { get; } =
    [
        CapabilityType.NfcTicket,
        CapabilityType.QrReader,
        CapabilityType.Switch,
        CapabilityType.Gps,
        CapabilityType.Health,
        CapabilityType.Ota
    ];

    public IReadOnlyList<string> TelemetryTopicPatterns { get; } =
    [
        "access/telemetry/{deviceId}",
        "access/events/{deviceId}",
        "access/access/{deviceId}",
        "access/status/{deviceId}",
        "access/{channel}/{deviceId}"
    ];

    public string CommandTopicPattern => "access/commands/{deviceId}";

    public IDeviceProtocolAdapter Adapter { get; }

    public AccessControlProfile(IDeviceProtocolAdapter? adapter = null)
    {
        Adapter = adapter ?? new AccessControlAdapter();
    }

    public bool SupportsTransport(string transportName) =>
        string.Equals(transportName, "MQTT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(transportName, "HTTP", StringComparison.OrdinalIgnoreCase);
}
