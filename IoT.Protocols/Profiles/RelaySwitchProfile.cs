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

    // Dispositivo MQTT nativo: no entra por el gateway TCP, así que no tiene familia ni puerto.
    public string? TcpFamily => null;

    public IReadOnlyList<CapabilityType> SupportedCapabilities { get; } =
    [
        CapabilityType.Switch
    ];

    // Patrones de telemetría. Se conservan los históricos y se añade el canal REAL del
    // firmware tras el cambio a tópicos por dispositivo (devices/<chipId>/<base>...).
    //
    // A propósito NO se declaran "telemetry/{deviceId}", "status/{deviceId}" ni
    // "events/{deviceId}": el relé publica ahí, pero también lo hacen Fligo y Toour, así que
    // declararlos hacía que este perfil se quedara con el enrutado de aquellos. La elección
    // de adaptador para esos tópicos la resuelve el contenido del payload (CanHandle).
    public IReadOnlyList<string> TelemetryTopicPatterns { get; } =
    [
        "relays/devices/{deviceId}/action",
        "relays/{deviceId}/state"
        // NO se declara "devices/{deviceId}/#": es demasiado amplio y se quedaría con los
        // reportes que el validador de tickets publica en devices/{id}/ticket-validation.
        // Los tópicos reales del relé (devices/<id>/<base>_state, /get) los reconoce
        // RelaySwitchAdapter.CanHandle por el nombre del tópico y el contenido del payload.
    ];

    // Canal general de comandos del equipo, el que el firmware escucha de verdad
    // (Relay_Wifi_Esp32/src/Application/Messaging/MqttTopics.h). Acepta "status", "reset",
    // "toggle_all", "sync_states"... Para accionar un relé concreto se publica en
    // devices/{deviceId}/<base>/command con "on", "off" o "toggle".
    public string CommandTopicPattern => "commands/{deviceId}";

    public IDeviceProtocolAdapter Adapter { get; }

    public RelaySwitchProfile(IDeviceProtocolAdapter? adapter = null)
    {
        Adapter = adapter ?? new RelaySwitchAdapter();
    }

    public bool SupportsTransport(string transportName) =>
        string.Equals(transportName, "MQTT", StringComparison.OrdinalIgnoreCase);
}
