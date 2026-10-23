using System.Globalization;
using System.Text.Json;
using IoT.Domain.Common;
using IoT.Domain.ValueObjects;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

/// <summary>
/// Adaptador de protocolo para rastreadores y sellos electrónicos Jointech (JT701, JT701D).
/// Procesa reportes ASCII delimitados por paréntesis:
/// (7500313602,P45,170720,020607,22.56035,N,114.01640,E,A,0,270,4,1,0000000000,1,0,3,5)
/// y tramas binarias de posición iniciadas por 0x24 ($).
/// </summary>
public sealed class JointechProtocolAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "Jointech";

    private readonly string _model;

    public JointechProtocolAdapter(string model = "JT701D")
    {
        _model = model;
    }

    public bool CanHandle(TransportMessage message)
    {
        if (string.Equals(message.TransportProtocol, "TCP-JOINTECH", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.TransportProtocol, "TCP-JT701", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Source, "jointech", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Source, _model, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return CanHandle(message.TopicOrRoute, message.Payload);
    }

    public bool CanHandle(string topic, string payload)
    {
        var lowerTopic = topic.ToLowerInvariant();
        if (lowerTopic.Contains("jointech") ||
            lowerTopic.Contains("jt701"))
        {
            return true;
        }

        var trimmed = payload.Trim();
        // Sólo se declara capaz del formato ASCII P45 '(...)' que Decode SÍ sabe parsear.
        // El formato HEX binario (0x24/'$') aún NO está implementado en Decode; aceptarlo
        // aquí hacía que esas tramas se "adoptaran" produciendo eventos SIN posición.
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
        {
            return true;
        }

        if (trimmed.StartsWith('{') && payload.Contains("jointech", StringComparison.OrdinalIgnoreCase))
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
                ["manufacturer"] = "Jointech",
                ["model"] = _model,
                ["protocol"] = "JT701"
            };

            var trimmed = payload.Trim();

            // 1. Trama ASCII Jointech: (ID,P45,date,time,lat,N,lon,E,A,spd,head,...)
            if (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
            {
                var clean = trimmed.Trim('(', ')', '\r', '\n');
                var parts = clean.Split(',');

                if (parts.Length >= 2)
                {
                    chipId = parts[0].Trim();
                    attributes["command_type"] = parts[1].Trim();
                }

                if (parts.Length >= 11)
                {
                    // Fecha (ddMMyy) y Hora (HHmmss)
                    string dStr = parts[2].Trim();
                    string tStr = parts[3].Trim();
                    if (dStr.Length == 6 && tStr.Length >= 6 &&
                        DateTime.TryParseExact(dStr + tStr[..6], "ddMMyyHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                    {
                        recordedAt = dt.ToUniversalTime();
                    }

                    if (double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                        double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                    {
                        if (string.Equals(parts[5].Trim(), "S", StringComparison.OrdinalIgnoreCase)) lat = -lat;
                        if (string.Equals(parts[7].Trim(), "W", StringComparison.OrdinalIgnoreCase)) lon = -lon;

                        bool isValid = string.Equals(parts[8].Trim(), "A", StringComparison.OrdinalIgnoreCase);

                        float speed = 0f;
                        if (float.TryParse(parts[9], NumberStyles.Float, CultureInfo.InvariantCulture, out float spd))
                            speed = spd;

                        float heading = 0f;
                        if (float.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out float hdg))
                            heading = hdg;

                        var posRes = GeoPosition.Create(lat, lon, speed, heading, null, null, isValid);
                        if (posRes.IsSuccess) position = posRes.Value;

                        metrics["speed_kph"] = speed;
                        metrics["heading_deg"] = heading;
                    }

                    // Estado de bloqueo / candado (lock state) en index 14
                    if (parts.Length > 14 && int.TryParse(parts[14].Trim(), out int lockState))
                    {
                        metrics["lock_state"] = lockState;
                        attributes["lock_status"] = lockState == 1 ? "Locked" : "Unlocked";
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
                    if (root.TryGetProperty("id", out var c1)) chipId = c1.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("chipId", out var c2)) chipId = c2.GetString() ?? string.Empty;
                }

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

                if (root.TryGetProperty("lockState", out var ls))
                    metrics["lock_state"] = ls.GetInt32();
            }

            if (string.IsNullOrEmpty(chipId))
                chipId = $"JOINTECH-{_model.ToUpperInvariant()}-UNKNOWN";

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
            return Result<NormalizedTelemetryEvent>.Failure(new Error("JointechProtocolAdapter.Error", ex.Message));
        }
    }

    private static string ExtractChipIdFromTopic(string topic)
    {
        if (TopicPatternMatcher.TryExtractParameters("jointech/{deviceId}/telemetry", topic, out var p1))
            return p1["deviceId"];
        if (TopicPatternMatcher.TryExtractParameters("tcp/jointech/{deviceId}", topic, out var p2))
            return p2["deviceId"];
        if (TopicPatternMatcher.TryExtractParameters("devices/{deviceId}/gps", topic, out var p3))
            return p3["deviceId"];
        return string.Empty;
    }
}
