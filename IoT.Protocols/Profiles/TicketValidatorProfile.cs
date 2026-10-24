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

    // Dispositivo MQTT nativo: no entra por el gateway TCP, así que no tiene familia ni puerto.
    public string? TcpFamily => null;

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
        // Reportes ya con ámbito por dispositivo (MqttTopics.h de Fligo y Toour).
        "devices/{deviceId}/ticket-validation",
        "devices/{deviceId}/config-validation",
        "devices/{deviceId}/ota-error",
        // Tópicos fijos del firmware ANTERIOR: se mantienen para no perder las tramas de los
        // equipos que todavía no se han re-flasheado.
        "TicketValidationRequest",
        "DeviceConfigValidationRequest",
        "ErrorUpdateFirmwareDevice"
    ];

    // El firmware se suscribe a "event/<clientId>" (MqttV5Adapter.cpp:12,42), NO a
    // "commands/<id>", que es donde PUBLICA. Con el patrón anterior los comandos de la
    // plataforma se publicaban en un tópico que el equipo no escuchaba: no llegaba ninguno.
    public string CommandTopicPattern => "event/{deviceId}";

    public IDeviceProtocolAdapter Adapter { get; }

    public TicketValidatorProfile(IDeviceProtocolAdapter? adapter = null)
    {
        Adapter = adapter ?? new TicketValidatorAdapter();
    }

    public bool SupportsTransport(string transportName) =>
        string.Equals(transportName, "MQTT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(transportName, "HTTP", StringComparison.OrdinalIgnoreCase);
}
