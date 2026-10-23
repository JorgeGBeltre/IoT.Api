using System.Globalization;
using System.Text.Json;
using IoT.Domain.Common;
using IoT.Domain.ValueObjects;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

/// <summary>
/// Adaptador de protocolo para la familia Queclink @Track Air Interface:
/// GV50CG, GV55, GV55W, GV300, GV300W, GL300 y Garmin-FMI.
/// Procesa reportes ASCII (+RESP:GTFRI, +BUFF:GTFRI, +RESP:GTINF, +RESP:GTEPN, +RESP:GTHBM)
/// y tramas JSON puenteadas.
/// </summary>
public sealed class QueclinkTrackProtocolAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "Queclink-@Track";

    private readonly string _model;

    public QueclinkTrackProtocolAdapter(string model = "GV55")
    {
        _model = model;
    }

    public bool CanHandle(TransportMessage message)
    {
        if (string.Equals(message.TransportProtocol, "TCP-QUECLINK", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.TransportProtocol, "UDP-QUECLINK", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Source, "queclink", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Source, _model, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return CanHandle(message.TopicOrRoute, message.Payload);
    }

    public bool CanHandle(string topic, string payload)
    {
        var lowerTopic = topic.ToLowerInvariant();
        if (lowerTopic.Contains("queclink") ||
            lowerTopic.Contains("@track") ||
            lowerTopic.Contains("gv50") ||
            lowerTopic.Contains("gv55") ||
            lowerTopic.Contains("gv300") ||
            lowerTopic.Contains("gl300") ||
            lowerTopic.Contains("garmin-fmi"))
        {
            return true;
        }

        var trimmed = payload.Trim();
        if (trimmed.StartsWith("+RESP:GT", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("+BUFF:GT", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("+ACK:GT", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith('{') &&
            (payload.Contains("queclink", StringComparison.OrdinalIgnoreCase) ||
             payload.Contains("@track", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

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

    public Result<NormalizedTelemetryEvent> Decode(string topic, string payload)
    {
        try
        {
            string chipId = ExtractChipIdFromTopic(topic);
            DateTime recordedAt = DateTime.UtcNow;
            GeoPosition? position = null;
            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["manufacturer"] = "Queclink",
                ["model"] = _model,
                ["protocol"] = "@Track"
            };

            var trimmed = payload.Trim();

            // 1. Trama ASCII nativa Queclink: +RESP:GTFRI,... o +BUFF:GTFRI,...
            if (trimmed.StartsWith("+RESP:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("+BUFF:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("+ACK:", StringComparison.OrdinalIgnoreCase))
            {
                // Remover delimitador de fin de trama si existe ($)
                var cleanPayload = trimmed.TrimEnd('$', '\r', '\n');
                var parts = cleanPayload.Split(',');

                if (parts.Length >= 3)
                {
                    attributes["report_type"] = parts[0]; // e.g. +RESP:GTFRI
                    attributes["protocol_version"] = parts[1];
                    string imei = parts[2].Trim();
                    if (!string.IsNullOrEmpty(imei))
                    {
                        chipId = imei;
                    }
                }

                // Reporte de posición fija (GTFRI / GTPNA / GTEPN)
                // Estructura estándar: [0]type, [1]ver, [2]imei, [3]name, [4]report_id, [5]report_type, [6]seq, [7]gps_acc,
                // [8]speed, [9]azimuth, [10]altitude, [11]longitude, [12]latitude, [13]utc_time
                if (parts.Length >= 14 && (parts[0].Contains("GTFRI") || parts[0].Contains("GTPNA") || parts[0].Contains("GTEPN")))
                {
                    float.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out float speed);
                    float.TryParse(parts[9], NumberStyles.Float, CultureInfo.InvariantCulture, out float heading);
                    float.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out float altitude);

                    if (double.TryParse(parts[11], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) &&
                        double.TryParse(parts[12], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat))
                    {
                        var posRes = GeoPosition.Create(lat, lon, speed, heading, altitude, null, true);
                        if (posRes.IsSuccess)
                        {
                            position = posRes.Value;
                        }
                    }

                    // UTC Timestamp (formato: YYYYMMDDHHMMSS)
                    string timeStr = parts[13].Trim();
                    if (timeStr.Length == 14 &&
                        DateTime.TryParseExact(timeStr, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                    {
                        recordedAt = dt.ToUniversalTime();
                    }

                    metrics["speed_kph"] = speed;
                    metrics["heading_deg"] = heading;
                    metrics["altitude_m"] = altitude;

                    // Odómetro (Mileage) en índice 19 — correcto según spec GV55/GV300.
                    if (parts.Length > 19 && double.TryParse(parts[19], NumberStyles.Float, CultureInfo.InvariantCulture, out double odom))
                    {
                        metrics["odometer_km"] = odom;
                    }
                    // NOTA: en GTFRI el índice 20 es 'Hour Meter Count' (p.ej. "12345:12:34"),
                    // NO la batería. El % de batería va en un índice posterior que depende del
                    // modelo (GV55/GV300/GV50CG). No se mapea aquí para no reportar un valor
                    // incorrecto; usar GTHBM o parametrizar por modelo con una trama real.
                }
                // Heartbeat monitor (GTHBM)
                else if (parts.Length >= 7 && parts[0].Contains("GTHBM"))
                {
                    attributes["packet_type"] = "heartbeat";
                    if (parts.Length > 5 && double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double bat))
                    {
                        metrics["battery_pct"] = bat;
                    }
                }
            }
            // 2. Formato JSON (Bridge)
            else if (trimmed.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                if (string.IsNullOrEmpty(chipId))
                {
                    if (root.TryGetProperty("imei", out var c1)) chipId = c1.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("uniqueId", out var c2)) chipId = c2.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("chipId", out var c3)) chipId = c3.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var dt))
                    recordedAt = dt.ToUniversalTime();

                if (root.TryGetProperty("latitude", out var lt) && lt.TryGetDouble(out double lat) &&
                    root.TryGetProperty("longitude", out var ln) && ln.TryGetDouble(out double lon))
                {
                    float? speed = root.TryGetProperty("speed", out var sp) && sp.TryGetSingle(out float s) ? s : null;
                    float? heading = root.TryGetProperty("heading", out var hd) && hd.TryGetSingle(out float h) ? h : null;
                    var posRes = GeoPosition.Create(lat, lon, speed, heading, null, null, true);
                    if (posRes.IsSuccess) position = posRes.Value;
                    if (speed.HasValue) metrics["speed_kph"] = speed.Value;
                    if (heading.HasValue) metrics["heading_deg"] = heading.Value;
                }
            }

            if (string.IsNullOrEmpty(chipId))
                chipId = $"QUECLINK-{_model.ToUpperInvariant()}-UNKNOWN";

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
            return Result<NormalizedTelemetryEvent>.Failure(new Error("QueclinkProtocolAdapter.Error", ex.Message));
        }
    }

    private static string ExtractChipIdFromTopic(string topic)
    {
        if (TopicPatternMatcher.TryExtractParameters("queclink/{deviceId}/telemetry", topic, out var p1))
            return p1["deviceId"];
        if (TopicPatternMatcher.TryExtractParameters("tcp/queclink/{deviceId}", topic, out var p2))
            return p2["deviceId"];
        if (TopicPatternMatcher.TryExtractParameters("devices/{deviceId}/gps", topic, out var p3))
            return p3["deviceId"];
        return string.Empty;
    }
}
