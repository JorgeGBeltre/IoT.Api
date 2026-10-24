using System.Text.Json;
using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

/// <summary>
/// Adaptador para reportes y telemetría de eventos OTA emitidos por dispositivos MQTT
/// (MQTTOTAv5: ota/{device}/progress, error, success, state, ack, o el broker de referencia:
/// .../error-update-firmware-device).
/// </summary>
public sealed class OtaDeviceAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "MQTT-OTA-Feedback";

    public bool CanHandle(TransportMessage message) => CanHandle(message.TopicOrRoute, message.Payload);

    public bool CanHandle(string topic, string payload)
    {
        var lower = topic.ToLowerInvariant();
        if (lower.StartsWith("ota/") ||
            lower.Contains("/ota/") ||
            lower.EndsWith("error-update-firmware-device") ||
            lower.EndsWith("error_update_firmware_device") ||
            lower.EndsWith("errorupdatefirmwaredevice"))
        {
            return true;
        }

        if (payload.Contains("UpdateFirmwareDevice", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("OTAV5_", StringComparison.OrdinalIgnoreCase) ||
            (payload.Contains("\"progress\"", StringComparison.OrdinalIgnoreCase) && lower.Contains("ota")))
        {
            return true;
        }

        return false;
    }

    public Result<NormalizedTelemetryEvent> Decode(TransportMessage message)
    {
        var res = Decode(message.TopicOrRoute, message.Payload);
        if (res.IsSuccess)
        {
            res.Value.MessageId = message.MessageId;
            res.Value.ReceivedAt = message.ReceivedAt;
        }
        return res;
    }

    public Result<NormalizedTelemetryEvent> Decode(string topic, string payload)
    {
        try
        {
            string chipId = ExtractChipId(topic);
            DateTime recordedAt = DateTime.UtcNow;
            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["packet_type"] = "ota_event",
                ["protocol"] = ProtocolName
            };

            string action = DetermineAction(topic);

            if (payload.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                // Extract chipId if not in topic
                if (string.IsNullOrEmpty(chipId))
                {
                    if (root.TryGetProperty("device", out var d1) && d1.ValueKind == JsonValueKind.String)
                        chipId = d1.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("chipId", out var d2) && d2.ValueKind == JsonValueKind.String)
                        chipId = d2.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("device", out var dObj) && dObj.ValueKind == JsonValueKind.Object &&
                             dObj.TryGetProperty("chipId", out var c3))
                        chipId = c3.GetString() ?? string.Empty;
                }

                // Extract version
                if (root.TryGetProperty("version", out var vElem) && vElem.ValueKind == JsonValueKind.String)
                    attributes["firmware_version"] = vElem.GetString() ?? string.Empty;
                else if (root.TryGetProperty("firmware_version", out var fvElem) && fvElem.ValueKind == JsonValueKind.String)
                    attributes["firmware_version"] = fvElem.GetString() ?? string.Empty;

                // Extract error message
                if (root.TryGetProperty("error", out var errElem) && errElem.ValueKind == JsonValueKind.String)
                {
                    action = "error";
                    attributes["ota_error"] = errElem.GetString() ?? "Unknown OTA error";
                }
                else if (root.TryGetProperty("details", out var detObj) && detObj.ValueKind == JsonValueKind.Object &&
                         detObj.TryGetProperty("errorMessage", out var emElem))
                {
                    action = "error";
                    attributes["ota_error"] = emElem.GetString() ?? "Unknown OTA error";
                }

                // Extract progress
                if (root.TryGetProperty("progress", out var pElem) && pElem.TryGetDouble(out var pVal))
                {
                    if (action == "unknown") action = "progress";
                    metrics["ota_progress"] = pVal;
                }

                // Extract bytes
                if (root.TryGetProperty("bytes", out var bElem) && bElem.TryGetDouble(out var bVal))
                {
                    metrics["ota_bytes"] = bVal;
                }

                // Extract state
                if (root.TryGetProperty("name", out var sName) && sName.ValueKind == JsonValueKind.String)
                {
                    attributes["ota_state_name"] = sName.GetString() ?? string.Empty;
                }

                // "ts" NO es un epoch: la librería del dispositivo publica millis(), es decir
                // los milisegundos desde que ARRANCÓ (MQTTOTAv5.cpp:1037,1064,1091,1111).
                // Interpretarlo como epoch daba fechas de 1970, y como el pipeline usa
                // RecordedAt para `device.RecordTelemetry(...)`, CADA reporte OTA mandaba el
                // last-seen del equipo a 1970 y la UI lo mostraba como no visto mientras se
                // estaba actualizando; además ensuciaba el histórico con registros de 1970.
                // Se conserva como métrica, que es información útil de verdad, y la fecha del
                // evento es la de recepción.
                if (root.TryGetProperty("ts", out var tsElem) && tsElem.TryGetInt64(out var uptimeMs) && uptimeMs >= 0)
                {
                    metrics["device_uptime_ms"] = uptimeMs;
                }
            }

            attributes["ota_action"] = action;

            // Sin identidad NO se inventa un marcador: "UNKNOWN-OTA-DEVICE" creaba un
            // dispositivo fantasma y una fila de auditoría por reporte. Se deja vacío para que
            // mande la identidad autenticada de la conexión, y si no hay ninguna el pipeline
            // rechaza la trama.


            return new NormalizedTelemetryEvent
            {
                DeviceIdentifier = chipId,
                RecordedAt = recordedAt,
                ReceivedAt = DateTime.UtcNow,
                NumericMetrics = metrics,
                Attributes = attributes,
                RawPayload = payload
            };
        }
        catch (Exception ex)
        {
            return Result<NormalizedTelemetryEvent>.Failure(new Error("OtaDeviceAdapter.Error", ex.Message));
        }
    }

    /// <summary>
    /// Identidad del equipo tomada del tópico. Se delega en <see cref="TopicIdentity"/>, que
    /// salta las palabras de protocolo: este adaptador devolvía parts[1] a secas, así que de
    /// un "ota/progress" sacaba "progress" como ChipId y creaba un dispositivo fantasma.
    /// Devuelve cadena vacía si el tópico no lleva identidad, para que mande la autenticada
    /// de la conexión.
    /// </summary>
    private static string ExtractChipId(string topic) =>
        Routing.TopicIdentity.ExtractDeviceId(topic) ?? string.Empty;

    private static string DetermineAction(string topic)
    {
        var lower = topic.ToLowerInvariant();
        if (lower.EndsWith("/progress")) return "progress";
        if (lower.EndsWith("/error") || lower.Contains("error-update-firmware-device")) return "error";
        if (lower.EndsWith("/success")) return "success";
        if (lower.EndsWith("/state")) return "state";
        if (lower.EndsWith("/ack")) return "ack";
        return "unknown";
    }
}
