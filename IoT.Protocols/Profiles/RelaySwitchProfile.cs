using IoT.Domain.Enums;
using IoT.Protocols.Adapters;
using IoT.Protocols.Contracts;

namespace IoT.Protocols.Profiles;

public sealed class RelaySwitchProfile : IDeviceProfile
{
    public string ProfileId => "relay-switch";
    public string Manufacturer => "Custom IoT";
    public string Model => "MQTT-Relay-BLE";
    public string DisplayName => "Controlador de Relés y Actuadores (MQTT)";
    public string PrimaryTransport => "MQTT";

    public IReadOnlyList<CapabilityType> SupportedCapabilities { get; } =
    [
        CapabilityType.Switch
    ];

    public IReadOnlyList<string> TelemetryTopicPatterns { get; } =
    [
        "relays/devices/{deviceId}/action",
        "relays/{deviceId}/state"
    ];

    public string CommandTopicPattern => "relays/devices/{deviceId}/commands";

    public IDeviceProtocolAdapter Adapter { get; }

    public RelaySwitchProfile(IDeviceProtocolAdapter? adapter = null)
    {
        Adapter = adapter ?? new RelaySwitchAdapter();
    }

    public bool SupportsTransport(string transportName) =>
        string.Equals(transportName, "MQTT", StringComparison.OrdinalIgnoreCase);
}
