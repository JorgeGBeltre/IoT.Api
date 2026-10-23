using System.Text.Json;
using IoT.Domain.Common;
using IoT.Domain.ValueObjects;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

public sealed class GpsAccidentAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "MQTT-GPS-Accident";

    public bool CanHandle(string topic, string payload)
    {
        if (topic.Equals("telemetry", StringComparison.OrdinalIgnoreCase) ||
            topic.Equals("events", StringComparison.OrdinalIgnoreCase) ||
            TopicPatternMatcher.MatchesMqttTopic("devices/+/telemetry", topic) ||
            TopicPatternMatcher.MatchesMqttTopic("devices/+/gps", topic) ||
            TopicPatternMatcher.MatchesMqttTopic("devices/+/accident", topic) ||
            TopicPatternMatcher.MatchesMqttTopic("devices/+/events", topic) ||
            TopicPatternMatcher.MatchesMqttTopic("telemetry/+", topic) ||
            TopicPatternMatcher.MatchesMqttTopic("telemetry/gps/+", topic) ||
            TopicPatternMatcher.MatchesMqttTopic("events/+", topic))
        {
            return payload.Contains("accident", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("gps_data", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("latitude", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public Result<NormalizedTelemetryEvent> Decode(string topic, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            string chipId = string.Empty;
            if (root.TryGetProperty("Device", out var deviceElem) && deviceElem.TryGetProperty("ChipId", out var chipElem))
            {
                chipId = chipElem.GetString() ?? string.Empty;
            }

            // El firmware de accidentes publica el ChipId en la RAÍZ, bajo la clave "id"
            // (References/Dispositivos/GPS/src/application/services/AccidentService.cpp:34).
            // Ningún adaptador leía esa clave, así que la identidad se perdía.
            if (string.IsNullOrEmpty(chipId))
            {
                foreach (var key in new[] { "id", "chipId", "ChipId", "device", "deviceId" })
                {
                    if (root.TryGetProperty(key, out var idElem) &&
                        idElem.ValueKind == JsonValueKind.String)
                    {
                        string candidate = idElem.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(candidate))
                        {
                            chipId = candidate;
                            break;
                        }
                    }
                }
            }

            // Del tópico sólo se acepta un identificador REAL. El firmware publica en el
            // tópico fijo "telemetry/accidents", y la plantilla "telemetry/{deviceId}"
            // devolvía "accidents" como ChipId: cada accidente se rechazaba por dispositivo
            // no aprovisionado y generaba una fila de auditoría.
            if (string.IsNullOrEmpty(chipId))
            {
                chipId = TopicIdentity.ExtractDeviceId(topic) ?? string.Empty;
            }

            // Sin identidad no se inventa un marcador ("Unknown-Device" creaba un
            // dispositivo fantasma): manda la identidad autenticada del transporte.

            DateTime recordedAt = DateTime.UtcNow;
            if ((root.TryGetProperty("Timestamp", out var tsElem) || root.TryGetProperty("timestamp", out tsElem))
                && tsElem.TryGetDateTime(out var parsedTs))
            {
                recordedAt = parsedTs.ToUniversalTime();
            }

            GeoPosition? position = null;
            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("Details", out var detailsElem))
            {
                JsonElement accidentElem = default;
                bool hasAccident = detailsElem.TryGetProperty("accident", out accidentElem);

                JsonElement gpsElem = default;
                bool hasGps = hasAccident && accidentElem.TryGetProperty("gps_data", out gpsElem) ||
                              detailsElem.TryGetProperty("gps", out gpsElem);

                if (hasGps && gpsElem.ValueKind == JsonValueKind.Object)
                {
                    double lat = 0.0;
                    double lon = 0.0;
                    bool latOk = gpsElem.TryGetProperty("latitude", out var latElem) && latElem.TryGetDouble(out lat);
                    bool lonOk = gpsElem.TryGetProperty("longitude", out var lonElem) && lonElem.TryGetDouble(out lon);

                    if (latOk && lonOk)
                    {
                        float? speed = gpsElem.TryGetProperty("speed", out var spElem) && spElem.TryGetSingle(out var s) ? s : null;
                        float? heading = gpsElem.TryGetProperty("heading", out var hdElem) && hdElem.TryGetSingle(out var h) ? h : null;
                        bool isValid = !gpsElem.TryGetProperty("is_valid", out var vElem) || vElem.GetBoolean();

                        var posResult = GeoPosition.Create(lat, lon, speed, heading, null, null, isValid);
                        if (posResult.IsSuccess)
                            position = posResult.Value;
                    }
                }

                if (hasAccident)
                {
                    if (accidentElem.TryGetProperty("impact_detected", out var impElem))
                        attributes["impact_detected"] = impElem.GetBoolean().ToString();

                    if (accidentElem.TryGetProperty("rollover_detected", out var rollElem))
                        attributes["rollover_detected"] = rollElem.GetBoolean().ToString();

                    if (accidentElem.TryGetProperty("impact_magnitude", out var magElem) && magElem.TryGetDouble(out var mag))
                        metrics["impact_magnitude"] = mag;

                    if (accidentElem.TryGetProperty("roll_angle", out var rElem) && rElem.TryGetDouble(out var r))
                        metrics["roll_angle_deg"] = r;

                    if (accidentElem.TryGetProperty("pitch_angle", out var pElem) && pElem.TryGetDouble(out var p))
                        metrics["pitch_angle_deg"] = p;
                }
            }

            // Forma PLANA del firmware de accidentes. Transcrita de AccidentService.cpp:30-39:
            //   {"type":"accident","impact":bool,"rollover":bool,"id":"<chipId>","timestamp":"<ISO>"}
            // El adaptador sólo leía Details.accident.{impact_detected,rollover_detected},
            // que este firmware nunca emite: la alerta llegaba vacía de semántica.
            // (El firmware lee la posición pero NO la incluye en esta trama MQTT; sólo viaja
            // en el SMS de alerta, así que aquí no hay posición que recuperar.)
            if (root.TryGetProperty("type", out var typeElem) && typeElem.ValueKind == JsonValueKind.String)
            {
                attributes["event_type"] = typeElem.GetString()!;
            }

            bool impact = TryReadFlag(root, "impact", out var impactValue) && impactValue;
            bool rollover = TryReadFlag(root, "rollover", out var rolloverValue) && rolloverValue;

            if (TryReadFlag(root, "impact", out _))
            {
                attributes["impact_detected"] = impact.ToString();
                metrics["impact_detected"] = impact ? 1 : 0;
            }

            if (TryReadFlag(root, "rollover", out _))
            {
                attributes["rollover_detected"] = rollover.ToString();
                metrics["rollover_detected"] = rollover ? 1 : 0;
            }

            if (impact || rollover)
            {
                // Código de alarma legible para que las vistas y reglas puedan filtrarlo.
                attributes["alarm_code"] = (impact, rollover) switch
                {
                    (true, true) => "impact_and_rollover",
                    (true, false) => "impact",
                    _ => "rollover"
                };
            }

            // Fallback: payload PLANO con lat/lng en la raíz (p.ej. el que publica el
            // gateway TCP R12L-F: {latitude, longitude, speed, course, satellites, isValid}).
            // Sin esto, la posición de los trackers binarios por TCP se perdía en silencio.
            if (position == null)
            {
                double lat = 0.0, lon = 0.0;
                bool latOk = root.TryGetProperty("latitude", out var rLat) && rLat.TryGetDouble(out lat);
                bool lonOk = root.TryGetProperty("longitude", out var rLon) && rLon.TryGetDouble(out lon);
                if (latOk && lonOk)
                {
                    float? speed = root.TryGetProperty("speed", out var rSp) && rSp.TryGetSingle(out var s) ? s : null;
                    float? heading = root.TryGetProperty("course", out var rCo) && rCo.TryGetSingle(out var c) ? c
                        : (root.TryGetProperty("heading", out var rHd) && rHd.TryGetSingle(out var h) ? h : (float?)null);
                    short? sats = root.TryGetProperty("satellites", out var rSat) && rSat.TryGetInt32(out var st) && st >= 0 && st <= short.MaxValue
                        ? (short)st
                        : (short?)null;
                    bool isValid = true;
                    if (root.TryGetProperty("isValid", out var rV) && (rV.ValueKind == JsonValueKind.True || rV.ValueKind == JsonValueKind.False))
                        isValid = rV.GetBoolean();

                    var posFlat = GeoPosition.Create(lat, lon, speed, heading, null, sats, isValid);
                    if (posFlat.IsSuccess)
                        position = posFlat.Value;
                    if (speed.HasValue) metrics["speed_kph"] = speed.Value;
                    if (heading.HasValue) metrics["heading_deg"] = heading.Value;
                }
            }

            return new NormalizedTelemetryEvent
            {
                DeviceIdentifier = chipId,
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
            return Result<NormalizedTelemetryEvent>.Failure(new Error("GpsAccidentAdapter.ParseError", ex.Message));
        }
    }

    /// <summary>
    /// Lee una bandera booleana de la raíz, tolerando que el firmware la serialice como
    /// booleano, número o cadena. Devuelve false si la clave no existe.
    /// </summary>
    private static bool TryReadFlag(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var element)) return false;

        switch (element.ValueKind)
        {
            case JsonValueKind.True: value = true; return true;
            case JsonValueKind.False: value = false; return true;
            case JsonValueKind.Number: value = element.GetDouble() != 0; return true;
            case JsonValueKind.String:
                return bool.TryParse(element.GetString(), out value);
            default:
                return false;
        }
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
