using System.Text.Json;
using System.Text.Json.Nodes;
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

    // El firmware de Toour/Fligo sólo procesa comandos con el sobre {"EventType":...,"Details":...}
    // (References/Dispositivos/Toour_IoT/src/src/Infrastructure/Mqtt/CommandParser.cpp:19-39); todo
    // lo demás lo descarta. Mapeamos el nombre de comando de la plataforma al EventType que el equipo
    // reconoce. Un comando no mapeado devuelve null y la plataforma usa su envelope genérico.
    private static readonly IReadOnlyDictionary<string, string> CommandToEventType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["REBOOT"] = "REBOOT",
            ["RESTART"] = "REBOOT",
            ["GET_STATUS"] = "GET_STATUS",
            ["STATUS"] = "GET_STATUS",
            ["GET_TELEMETRY"] = "GET_TELEMETRY",
            ["TELEMETRY"] = "GET_TELEMETRY",
            ["UpdateFirmwareDevice"] = "UpdateFirmwareDevice",
            ["OTA"] = "UpdateFirmwareDevice",
            ["DeviceConfigValidationResponse"] = "DeviceConfigValidationResponse",
            ["TicketValidationResult"] = "TicketValidationResult",
        };

    public string? EncodeCommandPayload(string commandName, string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(commandName) ||
            !CommandToEventType.TryGetValue(commandName.Trim(), out var eventType))
        {
            return null;
        }

        // El cuerpo opcional del comando (p.ej. {"IsValid":true,"Message":"ok"} para
        // TicketValidationResult) se incrusta como Details; si no viene o es inválido, Details={}.
        JsonNode? details = null;
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try { details = JsonNode.Parse(payloadJson); }
            catch (JsonException) { details = null; }
        }

        var root = new JsonObject
        {
            ["EventType"] = eventType,
            ["Details"] = details ?? new JsonObject()
        };
        return root.ToJsonString();
    }

    public IDeviceProtocolAdapter Adapter { get; }

    public TicketValidatorProfile(IDeviceProtocolAdapter? adapter = null)
    {
        Adapter = adapter ?? new TicketValidatorAdapter();
    }

    public bool SupportsTransport(string transportName) =>
        string.Equals(transportName, "MQTT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(transportName, "HTTP", StringComparison.OrdinalIgnoreCase);
}
