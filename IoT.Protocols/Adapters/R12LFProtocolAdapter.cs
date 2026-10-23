using System.Text.Json;
using IoT.Domain.Common;
using IoT.Domain.ValueObjects;
using IoT.Protocols.Binary;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

public sealed class R12LFProtocolAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "SEEWORLD-R12L-F";

    public bool CanHandle(TransportMessage message)
    {
        if (string.Equals(message.TransportProtocol, "TCP-R12LF", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Source, "r12l-f", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return CanHandle(message.TopicOrRoute, message.Payload);
    }

    public bool CanHandle(string topic, string payload)
    {
        if (topic.StartsWith("devices/", StringComparison.OrdinalIgnoreCase) &&
            (topic.EndsWith("/gps", StringComparison.OrdinalIgnoreCase) ||
             topic.EndsWith("/heartbeat", StringComparison.OrdinalIgnoreCase) ||
             topic.EndsWith("/login", StringComparison.OrdinalIgnoreCase)))
        {
            return payload.Contains("gps_data", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("r12l", StringComparison.OrdinalIgnoreCase);
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
            string chipId = string.Empty;
            if (TopicPatternMatcher.TryExtractParameters("devices/{deviceId}/gps", topic, out var p))
                chipId = p["deviceId"];
            else if (TopicPatternMatcher.TryExtractParameters("devices/{deviceId}/heartbeat", topic, out var p2))
                chipId = p2["deviceId"];

            DateTime recordedAt = DateTime.UtcNow;
            GeoPosition? position = null;
            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["transport"] = "TCP-R12LF"
            };

            if (payload.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                if (string.IsNullOrEmpty(chipId) && root.TryGetProperty("Device", out var devElem) &&
                    devElem.TryGetProperty("ChipId", out var cElem))
                {
                    chipId = cElem.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("Timestamp", out var tsElem) && tsElem.TryGetDateTime(out var parsedTs))
                {
                    recordedAt = parsedTs.ToUniversalTime();
                }

                if (root.TryGetProperty("Details", out var detailsElem))
                {
                    JsonElement gpsElem = default;
                    bool hasGps = detailsElem.TryGetProperty("accident", out var accElem) &&
                                  accElem.TryGetProperty("gps_data", out gpsElem) ||
                                  detailsElem.TryGetProperty("gps_data", out gpsElem);

                    if (hasGps && gpsElem.ValueKind == JsonValueKind.Object)
                    {
                        double lat = 0.0;
                        double lon = 0.0;
                        bool latOk = gpsElem.TryGetProperty("latitude", out var lt) && lt.TryGetDouble(out lat);
                        bool lonOk = gpsElem.TryGetProperty("longitude", out var ln) && ln.TryGetDouble(out lon);

                        if (latOk && lonOk)
                        {
                            float? speed = gpsElem.TryGetProperty("speed", out var sp) && sp.TryGetSingle(out var s) ? s : null;
                            float? heading = gpsElem.TryGetProperty("heading", out var hd) && hd.TryGetSingle(out var h) ? h : null;
                            bool isValid = !gpsElem.TryGetProperty("is_valid", out var v) || v.GetBoolean();

                            var posRes = GeoPosition.Create(lat, lon, speed, heading, null, null, isValid);
                            if (posRes.IsSuccess)
                                position = posRes.Value;

                            if (speed.HasValue) metrics["speed_kph"] = speed.Value;
                            if (heading.HasValue) metrics["heading_deg"] = heading.Value;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(chipId))
                chipId = "R12L-UNKNOWN";

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
            return Result<NormalizedTelemetryEvent>.Failure(new Error("R12LFProtocolAdapter.ParseError", ex.Message));
        }
    }
}
