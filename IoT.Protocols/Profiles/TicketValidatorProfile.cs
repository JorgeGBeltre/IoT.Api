using IoT.Domain.Enums;
using IoT.Protocols.Adapters;
using IoT.Protocols.Contracts;

namespace IoT.Protocols.Profiles;

public sealed class TicketValidatorProfile : IDeviceProfile
{
    public string ProfileId => "ticket-validator-v1";
    public string Manufacturer => "Generic";
    public string Model => "MQTT-LTE-TicketValidator";
    public string DisplayName => "Validador de Tickets NFC & Telemetría Vehicular (LTE)";
    public string PrimaryTransport => "MQTT";

    public IReadOnlyList<CapabilityType> SupportedCapabilities { get; } =
    [
        CapabilityType.Gps,
        CapabilityType.NfcTicket,
        CapabilityType.Ota,
        CapabilityType.Health,
        CapabilityType.LevelTelemetry
    ];

    public IReadOnlyList<string> TelemetryTopicPatterns { get; } =
    [
        "telemetry/{deviceId}",
        "events/{deviceId}",
        "status/{deviceId}",
        "TicketValidationRequest",
        "DeviceConfigValidationRequest",
        "ErrorUpdateFirmwareDevice"
    ];

    public string CommandTopicPattern => "commands/{deviceId}";

    public IDeviceProtocolAdapter Adapter { get; }

    public TicketValidatorProfile(IDeviceProtocolAdapter? adapter = null)
    {
        Adapter = adapter ?? new TicketValidatorAdapter();
    }

    public bool SupportsTransport(string transportName) =>
        string.Equals(transportName, "MQTT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(transportName, "HTTP", StringComparison.OrdinalIgnoreCase);
}
