using System.Text.Json;
using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

public sealed class GlpPressureAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "MQTT-PressureLevel-EKF";

    public bool CanHandle(string topic, string payload)
    {
        if (topic.StartsWith("glp/telemetria", StringComparison.OrdinalIgnoreCase) ||
            TopicPatternMatcher.MatchesMqttTopic("glp/telemetria/+", topic) ||
            // Aviso de desconexión (will message, retenido) que el equipo deja configurado
            // en el broker: glp/status/<device_id> con {"status":"offline","device_id":...}
            // (GLP_Presure/src/Infrastructure/Messaging/mqtt_client.cpp:41-43).
            topic.StartsWith("glp/status", StringComparison.OrdinalIgnoreCase) ||
            TopicPatternMatcher.MatchesMqttTopic("glp/status/+", topic))
        {
            return true;
        }

        return payload.Contains("sensors_filtered", StringComparison.OrdinalIgnoreCase) &&
               payload.Contains("pressure_bar", StringComparison.OrdinalIgnoreCase);
    }

    public Result<NormalizedTelemetryEvent> Decode(string topic, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            string deviceId = string.Empty;
            if (TopicPatternMatcher.TryExtractParameters("glp/telemetria/{deviceId}", topic, out var p))
                deviceId = p["deviceId"];

            if (string.IsNullOrEmpty(deviceId) && root.TryGetProperty("Device", out var devElem))
            {
                if (devElem.TryGetProperty("DeviceId", out var dIdElem))
                    deviceId = dIdElem.GetString() ?? string.Empty;
                else if (devElem.TryGetProperty("ChipId", out var chipElem))
                    deviceId = chipElem.GetString() ?? string.Empty;
            }

            // El will message no lleva objeto "Device": el identificador va en la raíz como
            // "device_id". Y en cualquier otro tópico de la familia (glp/status/<id>,
            // glp/config/<id>) el identificador NO está en el segundo segmento.
            if (string.IsNullOrEmpty(deviceId))
            {
                foreach (var key in new[] { "device_id", "deviceId", "DeviceId", "id" })
                {
                    if (root.TryGetProperty(key, out var idElem) && idElem.ValueKind == JsonValueKind.String)
                    {
                        string candidate = idElem.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(candidate))
                        {
                            deviceId = candidate;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(deviceId))
                deviceId = TopicIdentity.ExtractDeviceId(topic) ?? string.Empty;

            // Sin identidad NO se inventa un ChipId: un marcador fijo ("GLP-Device") creaba
            // un dispositivo fantasma y una fila de auditoría por trama. Se deja vacío para
            // que mande la identidad autenticada del transporte, y si tampoco la hay el
            // pipeline rechaza la trama (IngestTelemetryCommand.TransportDeviceIdentifier).

            DateTime recordedAt = DateTime.UtcNow;
            if (root.TryGetProperty("Timestamp", out var tsElem) && tsElem.TryGetDateTime(out var parsedTs))
                recordedAt = parsedTs.ToUniversalTime();

            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IoT.Domain.ValueObjects.GeoPosition? position = null;

            // Estado de conexión: lo trae el will message ("offline") y también el objeto
            // Device de la telemetría ("connected"). Sin esto, la caída del tanque no se
            // registraba en ninguna parte.
            if (root.TryGetProperty("status", out var statusElem) && statusElem.ValueKind == JsonValueKind.String)
            {
                string status = statusElem.GetString()!;
                attributes["connection_status"] = status;
                metrics["online"] = status.Equals("offline", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            }

            if (root.TryGetProperty("Details", out var detailsElem))
            {
                // Raw readings
                if (detailsElem.TryGetProperty("sensors_raw", out var rawElem))
                {
                    if (rawElem.TryGetProperty("level_mm", out var l)) metrics["level_mm_raw"] = l.GetDouble();
                    if (rawElem.TryGetProperty("pressure_bar", out var pr)) metrics["pressure_bar_raw"] = pr.GetDouble();
                    if (rawElem.TryGetProperty("temp_celsius", out var t)) metrics["temp_celsius_raw"] = t.GetDouble();
                }

                // EKF Filtered readings
                if (detailsElem.TryGetProperty("sensors_filtered", out var fltElem))
                {
                    if (fltElem.TryGetProperty("level_mm", out var l)) metrics["level_mm"] = l.GetDouble();
                    if (fltElem.TryGetProperty("pressure_bar", out var pr)) metrics["pressure_bar"] = pr.GetDouble();
                    if (fltElem.TryGetProperty("temp_celsius", out var t)) metrics["temp_celsius"] = t.GetDouble();
                    if (fltElem.TryGetProperty("kalman_uncertainty_mm", out var u)) metrics["kalman_uncertainty_mm"] = u.GetDouble();
                }

                // Volume & mass
                if (detailsElem.TryGetProperty("volume", out var volElem))
                {
                    if (volElem.TryGetProperty("liters", out var lt)) metrics["volume_liters"] = lt.GetDouble();
                    if (volElem.TryGetProperty("gallons", out var gl)) metrics["volume_gallons"] = gl.GetDouble();
                    if (volElem.TryGetProperty("percentage", out var pct)) metrics["fill_percentage"] = pct.GetDouble();
                    if (volElem.TryGetProperty("mass_kg", out var kg)) metrics["mass_kg"] = kg.GetDouble();
                    if (volElem.TryGetProperty("liters_15c", out var v15)) metrics["volume_liters_15c"] = v15.GetDouble();
                    // El firmware también emite la densidad usada y la marca de anomalía.
                    if (volElem.TryGetProperty("density_kg_l", out var dn)) metrics["density_kg_l"] = dn.GetDouble();
                    if (volElem.TryGetProperty("anomaly", out var an)) SetFlag(metrics, attributes, "volume_anomaly", an);
                }

                // Autonomía/consumo. El firmware lo publica como
                // Details.consumption.{l_per_day,remaining_days}; se aceptan además los
                // nombres antiguos (Details.autonomy.*) por compatibilidad.
                if (detailsElem.TryGetProperty("consumption", out var consElem))
                {
                    if (consElem.TryGetProperty("l_per_day", out var c)) metrics["consumption_l_day"] = c.GetDouble();
                    if (consElem.TryGetProperty("remaining_days", out var d)) metrics["estimated_autonomy_days"] = d.GetDouble();
                }
                if (detailsElem.TryGetProperty("autonomy", out var autElem))
                {
                    if (autElem.TryGetProperty("consumption_l_day", out var c)) metrics["consumption_l_day"] = c.GetDouble();
                    if (autElem.TryGetProperty("estimated_days", out var d)) metrics["estimated_autonomy_days"] = d.GetDouble();
                }

                // Salud de sensores (scores 0-100). El firmware la publica en
                // Details.health.{ultrasonic,pressure,temperature}; se aceptan también los
                // nombres antiguos (Details.diagnostics.*_health).
                if (detailsElem.TryGetProperty("health", out var healthElem))
                {
                    if (healthElem.TryGetProperty("ultrasonic", out var uh)) metrics["sensor_health_ultrasonic"] = uh.GetDouble();
                    if (healthElem.TryGetProperty("pressure", out var ph)) metrics["sensor_health_pressure"] = ph.GetDouble();
                    if (healthElem.TryGetProperty("temperature", out var th)) metrics["sensor_health_temp"] = th.GetDouble();
                }
                if (detailsElem.TryGetProperty("diagnostics", out var diagElem))
                {
                    if (diagElem.TryGetProperty("ultrasonic_health", out var uh)) metrics["sensor_health_ultrasonic"] = uh.GetDouble();
                    if (diagElem.TryGetProperty("pressure_health", out var ph)) metrics["sensor_health_pressure"] = ph.GetDouble();
                    if (diagElem.TryGetProperty("temp_health", out var th)) metrics["sensor_health_temp"] = th.GetDouble();
                }

                // Geometría del tanque (contexto de la medición).
                if (detailsElem.TryGetProperty("tank", out var tankElem))
                {
                    if (tankElem.TryGetProperty("radius_cm", out var r)) metrics["tank_radius_cm"] = r.GetDouble();
                    if (tankElem.TryGetProperty("length_cm", out var l)) metrics["tank_length_cm"] = l.GetDouble();
                }

                // Contraste con la presión de vapor (Antoine): detecta fuga o sensor sucio.
                if (detailsElem.TryGetProperty("vapor_check", out var vaporElem))
                {
                    if (vaporElem.TryGetProperty("p_measured", out var pm)) metrics["vapor_p_measured_bar"] = pm.GetDouble();
                    if (vaporElem.TryGetProperty("p_antoine", out var pa)) metrics["vapor_p_antoine_bar"] = pa.GetDouble();
                    if (vaporElem.TryGetProperty("residual", out var rs)) metrics["vapor_residual_bar"] = rs.GetDouble();
                }

                // Modelo acústico usado para convertir tiempo de vuelo en nivel.
                if (detailsElem.TryGetProperty("acoustic", out var acElem))
                {
                    if (acElem.TryGetProperty("c_used_ms", out var cu)) metrics["acoustic_sound_speed_ms"] = cu.GetDouble();
                    if (acElem.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String)
                        attributes["acoustic_source"] = src.GetString()!;
                    if (acElem.TryGetProperty("healthy", out var hl)) SetFlag(metrics, attributes, "acoustic_healthy", hl);
                }

                // Observabilidad del equipo.
                if (detailsElem.TryGetProperty("rssi", out var rssiElem) && rssiElem.ValueKind == JsonValueKind.Number)
                    metrics["rssi"] = rssiElem.GetDouble();
                if (detailsElem.TryGetProperty("free_heap", out var heapElem) && heapElem.ValueKind == JsonValueKind.Number)
                    metrics["free_heap"] = heapElem.GetDouble();
                if (detailsElem.TryGetProperty("uptime", out var upElem) && upElem.ValueKind == JsonValueKind.Number)
                    metrics["uptime_s"] = upElem.GetDouble();

                // POSICIÓN: el firmware envía Details.gps.{lat,lng,valid}. Antes se fijaba
                // Position=null incondicionalmente y el tanque nunca aparecía en el mapa.
                if (detailsElem.TryGetProperty("gps", out var gpsElem) &&
                    gpsElem.TryGetProperty("lat", out var latElem) &&
                    gpsElem.TryGetProperty("lng", out var lngElem) &&
                    latElem.ValueKind == JsonValueKind.Number && lngElem.ValueKind == JsonValueKind.Number)
                {
                    double lat = latElem.GetDouble();
                    double lng = lngElem.GetDouble();
                    bool gpsValid = !gpsElem.TryGetProperty("valid", out var vElem) || ReadFlag(vElem);

                    // (0,0) es el valor por defecto de un GPS sin fix: no es una posición.
                    if (gpsValid && (Math.Abs(lat) > 0.000001 || Math.Abs(lng) > 0.000001))
                    {
                        var posResult = IoT.Domain.ValueObjects.GeoPosition.Create(
                            latitude: lat,
                            longitude: lng,
                            isValid: true);
                        if (posResult.IsSuccess)
                            position = posResult.Value;
                    }
                }
            }

            return new NormalizedTelemetryEvent
            {
                DeviceIdentifier = deviceId,
                RecordedAt = recordedAt,
                ReceivedAt = DateTime.UtcNow,
                Position = position,
                NumericMetrics = metrics,
                Attributes = attributes,
                RawPayload = payload
            };
        }
        catch (Exception ex)
        {
            return Result<NormalizedTelemetryEvent>.Failure(new Error("GlpPressureAdapter.ParseError", ex.Message));
        }
    }

    /// <summary>
    /// Lee una bandera que el firmware puede serializar como booleano o como número.
    /// </summary>
    private static bool ReadFlag(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.GetDouble() != 0,
        JsonValueKind.String => bool.TryParse(element.GetString(), out var b) && b,
        _ => false
    };

    /// <summary>
    /// Publica una bandera como métrica (0/1, graficable) y como atributo (legible).
    /// </summary>
    private static void SetFlag(
        Dictionary<string, double> metrics,
        Dictionary<string, string> attributes,
        string name,
        JsonElement element)
    {
        bool value = ReadFlag(element);
        metrics[name] = value ? 1 : 0;
        attributes[name] = value ? "true" : "false";
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
