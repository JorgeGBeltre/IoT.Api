using System.Text.Json;
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

    public string? EncodeCommandPayload(string commandName, string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return null;
        }

        string cmd = commandName.Trim();

        // Comandos nativos ya expresados en el dialecto del firmware
        if (cmd.StartsWith("switch", StringComparison.OrdinalIgnoreCase) && cmd.Contains(':'))
        {
            return cmd.ToLowerInvariant();
        }

        if (cmd.StartsWith("startup:", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("pulse:", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("sled:", StringComparison.OrdinalIgnoreCase))
        {
            return cmd.ToLowerInvariant();
        }

        if (string.Equals(cmd, "reset", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cmd, "reboot", StringComparison.OrdinalIgnoreCase))
        {
            return "reset";
        }

        if (string.Equals(cmd, "status", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cmd, "get_status", StringComparison.OrdinalIgnoreCase))
        {
            return "status";
        }

        if (string.Equals(cmd, "sync_states", StringComparison.OrdinalIgnoreCase))
        {
            return "sync_states";
        }

        var (channel, state) = ParseChannelAndState(payloadJson);

        if (string.Equals(cmd, "toggle_all", StringComparison.OrdinalIgnoreCase))
        {
            return "toggle_all";
        }

        if (string.Equals(cmd, "SET_POWER_ALL", StringComparison.OrdinalIgnoreCase))
        {
            return state is null or "toggle" ? "toggle_all" : $"all:{state}";
        }

        if (string.Equals(cmd, "set_switch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cmd, "set_channel", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cmd, "SET_POWER", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cmd, "SET_POWER_STATE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cmd, "SET_RELAY_STATE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cmd, "switch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cmd, "power", StringComparison.OrdinalIgnoreCase))
        {
            int targetChannel = channel is >= 1 and <= 16 ? channel.Value : 1;
            string targetState = state ?? "toggle";
            return $"switch{targetChannel}:{targetState}";
        }

        return null;
    }

    private static (int? Channel, string? State) ParseChannelAndState(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return (null, null);
        }

        string trimmed = payloadJson.Trim();

        // Soporte para escalares planos ("ON", "OFF", "toggle", "1", "0", "true", "false")
        string? directScalar = NormalizeStateToken(trimmed.Trim('"'));
        if (!trimmed.StartsWith('{') && directScalar is not null)
        {
            return (null, directScalar);
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            if (root.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number)
            {
                return (null, NormalizeElementState(root));
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            int? channel = null;
            if (TryGetPropertyCaseInsensitive(root, "channel", out var chEl) ||
                TryGetPropertyCaseInsensitive(root, "outlet", out chEl) ||
                TryGetPropertyCaseInsensitive(root, "relay", out chEl))
            {
                if (chEl.ValueKind == JsonValueKind.Number && chEl.TryGetInt32(out int chNum))
                {
                    channel = chNum <= 0 ? chNum + 1 : chNum;
                }
                else if (chEl.ValueKind == JsonValueKind.String && int.TryParse(chEl.GetString(), out int chParsed))
                {
                    channel = chParsed <= 0 ? chParsed + 1 : chParsed;
                }
            }

            string? state = null;
            foreach (string key in new[] { "switch", "state", "power", "value" })
            {
                if (TryGetPropertyCaseInsensitive(root, key, out var stEl))
                {
                    state = NormalizeElementState(stEl);
                    if (state is not null)
                    {
                        break;
                    }
                }
            }

            return (channel, state);
        }
        catch (JsonException)
        {
            return (null, directScalar);
        }
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? NormalizeElementState(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => "on",
        JsonValueKind.False => "off",
        JsonValueKind.Number when element.TryGetInt32(out int n) => n != 0 ? "on" : "off",
        JsonValueKind.String => NormalizeStateToken(element.GetString()),
        _ => null
    };

    private static string? NormalizeStateToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "on" or "1" or "true" => "on",
            "off" or "0" or "false" => "off",
            "toggle" => "toggle",
            _ => null
        };
    }
}
