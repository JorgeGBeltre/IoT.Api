using System.Text.Json;
using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

public sealed class RelaySwitchAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "MQTT-Relay-Switch";

    public bool CanHandle(string topic, string payload)
    {
        // El firmware publica el estado del relé en "<base>_state" (guion bajo, p.ej.
        // "device_switch1_state") y se suscribe a "<base>/state" y "<base>/command".
        // Sólo se reconocía la forma con barra, así que el estado real del actuador
        // (texto plano "on"/"off") lo acababa resolviendo el adaptador genérico.
        if (topic.StartsWith("relays/", StringComparison.OrdinalIgnoreCase) ||
            topic.EndsWith("/state", StringComparison.OrdinalIgnoreCase) ||
            topic.EndsWith("_state", StringComparison.OrdinalIgnoreCase) ||
            topic.EndsWith("/report", StringComparison.OrdinalIgnoreCase) ||
            topic.Contains("switch", StringComparison.OrdinalIgnoreCase) ||
            topic.Contains("relay", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // La telemetría periódica llega al tópico GLOBAL "telemetry" (sin chipId ni nada
        // que delate al relé en el nombre), así que hay que reconocerla por el payload:
        // su marca es el objeto "relays".
        return payload.Contains("devices_count", StringComparison.OrdinalIgnoreCase) ||
               payload.Contains("desired_state", StringComparison.OrdinalIgnoreCase) ||
               payload.Contains("device_switch", StringComparison.OrdinalIgnoreCase) ||
               payload.Contains("\"relays\"", StringComparison.OrdinalIgnoreCase);
    }

    public Result<NormalizedTelemetryEvent> Decode(string topic, string payload)
    {
        // El estado del relé llega como TEXTO PLANO, no como JSON
        // (Relay_Wifi_Esp32/src/Application/Services/TelemetryService.cpp:42-45 publica
        // la cadena desnuda "on"/"off"). JsonDocument.Parse lanzaba excepción y cada
        // conmutación del actuador se perdía.
        if (!LooksLikeJson(payload))
        {
            return DecodePlainTextState(topic, payload);
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            string chipId = string.Empty;
            if (root.TryGetProperty("Device", out var devElem) && devElem.TryGetProperty("ChipId", out var chipElem))
                chipId = chipElem.GetString() ?? string.Empty;

            // Sin identidad NO se inventa un marcador ("Relay-Switch" creaba un dispositivo
            // fantasma): los tópicos de este firmware son globales y no llevan ChipId, así
            // que manda la identidad autenticada de la conexión.

            DateTime recordedAt = DateTime.UtcNow;
            if (root.TryGetProperty("Timestamp", out var tsElem) && tsElem.TryGetDateTime(out var parsedTs))
                recordedAt = parsedTs.ToUniversalTime();

            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("Details", out var detailsElem))
            {
                if (detailsElem.TryGetProperty("rssi", out var rssiElem) && rssiElem.TryGetDouble(out var rssi))
                    metrics["rssi_dbm"] = rssi;

                if (detailsElem.TryGetProperty("free_heap", out var heapElem) && heapElem.TryGetDouble(out var heap))
                    metrics["free_heap_bytes"] = heap;

                if (detailsElem.TryGetProperty("uptime", out var upElem) && upElem.TryGetDouble(out var uptime))
                    metrics["uptime_seconds"] = uptime;

                if (detailsElem.TryGetProperty("sync_completed", out var syncElem) &&
                    (syncElem.ValueKind == JsonValueKind.True || syncElem.ValueKind == JsonValueKind.False))
                {
                    attributes["sync_completed"] = syncElem.GetBoolean().ToString();
                }

                if (detailsElem.TryGetProperty("devices_count", out var countElem) && countElem.TryGetDouble(out var count))
                    metrics["devices_count"] = count;

                if (detailsElem.TryGetProperty("wifi_status", out var wifiElem) && wifiElem.ValueKind == JsonValueKind.String)
                    attributes["wifi_status"] = wifiElem.GetString()!;

                // Forma del mensaje de STATUS: Details.devices = { "<id>": {state, desired_state, pin} }
                // (Relay_Wifi_Esp32/.../ArduinoJsonPayloadSerializer.cpp:33-39).
                if (detailsElem.TryGetProperty("devices", out var switchesElem) && switchesElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in switchesElem.EnumerateObject())
                    {
                        var switchData = prop.Value;
                        if (switchData.ValueKind != JsonValueKind.Object) continue;

                        if (switchData.TryGetProperty("state", out var stateElem))
                        {
                            SetSwitchState(metrics, attributes, prop.Name, stateElem.GetString() ?? "off");
                        }

                        if (switchData.TryGetProperty("desired_state", out var desElem))
                            attributes[$"{prop.Name}_desired_state"] = desElem.GetString() ?? "off";

                        if (switchData.TryGetProperty("pin", out var pinElem) && pinElem.TryGetDouble(out var pin))
                            metrics[$"{prop.Name}_gpio_pin"] = pin;
                    }
                }

                // Forma del mensaje de TELEMETRÍA periódica: Details.relays = { "<id>": "on" }
                // (mismo serializador, líneas 102-105). Es PLANA, no anidada: el adaptador
                // sólo entendía la forma de status, así que la telemetría periódica caía al
                // fallback genérico y producía métricas con otros nombres para el MISMO
                // equipo, partiendo sus series históricas en dos.
                if (detailsElem.TryGetProperty("relays", out var relaysElem) && relaysElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in relaysElem.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            SetSwitchState(metrics, attributes, prop.Name, prop.Value.GetString() ?? "off");
                        }
                        else if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            SetSwitchState(metrics, attributes, prop.Name, prop.Value.GetBoolean() ? "on" : "off");
                        }
                    }
                }
            }

            return new NormalizedTelemetryEvent
            {
                DeviceIdentifier = chipId,
                RecordedAt = recordedAt,
                ReceivedAt = DateTime.UtcNow,
                Position = null,
                NumericMetrics = metrics,
                Attributes = attributes,
                RawPayload = payload
            };
        }
        catch (Exception ex)
        {
            return Result<NormalizedTelemetryEvent>.Failure(new Error("RelaySwitchAdapter.ParseError", ex.Message));
        }
    }

    /// <summary>
    /// Publica el estado de un relé con los MISMOS nombres por los dos caminos (status y
    /// telemetría periódica), para que las series históricas del equipo no se partan.
    /// </summary>
    private static void SetSwitchState(
        Dictionary<string, double> metrics,
        Dictionary<string, string> attributes,
        string switchName,
        string state)
    {
        bool on = state.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                  state == "1" ||
                  state.Equals("true", StringComparison.OrdinalIgnoreCase);

        attributes[$"{switchName}_state"] = on ? "on" : "off";
        metrics[$"{switchName}_state_binary"] = on ? 1.0 : 0.0;
    }

    private static bool LooksLikeJson(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        char first = payload.TrimStart()[0];
        return first == '{' || first == '[';
    }

    /// <summary>
    /// Decodifica el estado del relé publicado como texto plano. Acepta las mismas formas
    /// que el propio firmware reconoce en sus comandos
    /// (RelayFirmwareController.cpp:154-155: "on"/"1"/"true" y "off"/"0"/"false").
    /// </summary>
    private static Result<NormalizedTelemetryEvent> DecodePlainTextState(string topic, string payload)
    {
        string value = payload.Trim().Trim('"');

        bool? state = value.ToLowerInvariant() switch
        {
            "on" or "1" or "true" => true,
            "off" or "0" or "false" => false,
            _ => null
        };

        var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Nombre del conmutador a partir del tópico: "device_switch1_state" → "device_switch1",
        // "device_switch1/state" → "device_switch1". Permite varios relés por equipo.
        string switchName = topic;
        // "/report" es el tópico por el que el equipo REPORTA su estado; "_state" es el de
        // bajada con el que el servidor se lo fija. Se aceptan los dos para no perder nada
        // de los equipos aún sin re-flashear.
        foreach (var suffix in new[] { "/report", "_state", "/state", "/command", "/get", "/set" })
        {
            if (switchName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                switchName = switchName[..^suffix.Length];
                break;
            }
        }
        switchName = switchName.Trim('/');
        if (string.IsNullOrWhiteSpace(switchName)) switchName = "switch";

        if (state.HasValue)
        {
            attributes[$"{switchName}_state"] = state.Value ? "on" : "off";
            metrics[$"{switchName}_state_binary"] = state.Value ? 1.0 : 0.0;
            // Métrica canónica para que las vistas no dependan del nombre del conmutador.
            metrics["relay_state"] = state.Value ? 1.0 : 0.0;
        }
        else
        {
            // Otros textos planos del firmware (p.ej. "status" en <base>/get): se conserva
            // el valor como atributo en vez de perder la trama.
            attributes[$"{switchName}_message"] = value;
        }

        return new NormalizedTelemetryEvent
        {
            // Identidad vacía a propósito: el tópico es global y el payload no es JSON.
            // La aporta la conexión autenticada (TransportDeviceIdentifier).
            DeviceIdentifier = string.Empty,
            RecordedAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
            Position = null,
            NumericMetrics = metrics,
            Attributes = attributes,
            RawPayload = payload
        };
    }

    public bool CanHandle(TransportMessage message) => CanHandle(message.TopicOrRoute, message.Payload);

    public Result<NormalizedTelemetryEvent> Decode(TransportMessage message)
    {
        var result = Decode(message.TopicOrRoute, message.Payload);
        if (result.IsSuccess)
        {
            result.Value.MessageId = message.MessageId;
            result.Value.ReceivedAt = message.ReceivedAt;
        }
        return result;
    }
}
