using IoT.Domain.Enums;
using IoT.Protocols.Adapters;
using IoT.Protocols.Contracts;

namespace IoT.Protocols.Profiles;

public sealed class GlpPressureProfile : IDeviceProfile
{
    public string ProfileId => "pressure-level-sensor-v1";
    public string Manufacturer => "Custom IoT";
    public string Model => "MQTT-PressureLevelSensor";
    public string DisplayName => "Sensor de Presión/Nivel de Tanque Estacionario GLP";
    public string PrimaryTransport => "MQTT";

    // Dispositivo MQTT nativo: no entra por el gateway TCP, así que no tiene familia ni puerto.
    public string? TcpFamily => null;

    public IReadOnlyList<CapabilityType> SupportedCapabilities { get; } =
    [
        CapabilityType.LevelTelemetry,
        CapabilityType.Pressure,
        CapabilityType.Gps
    ];

    public IReadOnlyList<string> TelemetryTopicPatterns { get; } =
    [
        "glp/telemetria/{deviceId}",
        "glp/telemetria/#"
    ];

    public string CommandTopicPattern => "glp/config/{deviceId}";

    public IDeviceProtocolAdapter Adapter { get; }

    public GlpPressureProfile(IDeviceProtocolAdapter? adapter = null)
    {
        Adapter = adapter ?? new GlpPressureAdapter();
    }

    public bool SupportsTransport(string transportName) =>
        string.Equals(transportName, "MQTT", StringComparison.OrdinalIgnoreCase);
}
