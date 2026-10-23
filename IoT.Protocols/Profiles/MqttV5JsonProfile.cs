using IoT.Domain.Enums;
using IoT.Protocols.Adapters;
using IoT.Protocols.Contracts;

namespace IoT.Protocols.Profiles;

public sealed class MqttV5JsonProfile : IDeviceProfile
{
    public string ProfileId => "mqttv5-json";
    public string Manufacturer => "Generic";
    public string Model => "MQTTv5-JSON-Generic";
    public string DisplayName => "Dispositivo MQTT 5.0 (JSON nativo)";
    public string PrimaryTransport => "MQTT";

    public IReadOnlyList<CapabilityType> SupportedCapabilities { get; } =
    [
        CapabilityType.Health,
        CapabilityType.Ota,
        CapabilityType.LevelTelemetry
    ];

    public IReadOnlyList<string> TelemetryTopicPatterns { get; } =
    [
        "mqttv5/telemetry",
        "mqttv5/status",
        "mqttv5/{deviceId}/telemetry",
        "mqttv5/{deviceId}/status",
        "mqttv5/+"
    ];

    public string CommandTopicPattern => "mqttv5/cmd/{deviceId}";

    public IDeviceProtocolAdapter Adapter { get; }

    public MqttV5JsonProfile(IDeviceProtocolAdapter? adapter = null)
    {
        Adapter = adapter ?? new MqttV5JsonAdapter();
    }

    public bool SupportsTransport(string transportName) =>
        string.Equals(transportName, "MQTT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(transportName, "HTTP", StringComparison.OrdinalIgnoreCase);
}
