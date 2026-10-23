using System.Text.Json;
using IoT.Domain.Common;
using IoT.Domain.ValueObjects;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

public sealed class TicketValidatorAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "MQTT-LTE-TicketValidator-v1";

    public bool CanHandle(TransportMessage message) => CanHandle(message.TopicOrRoute, message.Payload);

    public bool CanHandle(string topic, string payload)
    {
        if (topic.StartsWith("telemetry/", StringComparison.OrdinalIgnoreCase) ||
            topic.StartsWith("events/", StringComparison.OrdinalIgnoreCase) ||
            topic.StartsWith("status/", StringComparison.OrdinalIgnoreCase) ||
            topic.Equals("TicketValidationRequest", StringComparison.OrdinalIgnoreCase) ||
            TopicPatternMatcher.MatchesMqttTopic("telemetry/+", topic) ||
            TopicPatternMatcher.MatchesMqttTopic("events/+", topic) ||
            TopicPatternMatcher.MatchesMqttTopic("status/+", topic))
        {
            return payload.Contains("GroupName", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("TransportCode", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("CodeTicket", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("free_heap", StringComparison.OrdinalIgnoreCase);
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
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            string chipId = string.Empty;
            string imei = string.Empty;
            string macAddress = string.Empty;
            string groupName = string.Empty;

            if (root.TryGetProperty("Device", out var devElem))
            {
                if (devElem.TryGetProperty("ChipId", out var cElem)) chipId = cElem.GetString() ?? string.Empty;
                if (devElem.TryGetProperty("IMEI", out var imeiElem)) imei = imeiElem.GetString() ?? string.Empty;
                if (devElem.TryGetProperty("MacAddress", out var macElem)) macAddress = macElem.GetString() ?? string.Empty;
                if (devElem.TryGetProperty("GroupName", out var grpElem)) groupName = grpElem.GetString() ?? string.Empty;
            }

            if (string.IsNullOrEmpty(chipId))
            {
                if (TopicPatternMatcher.TryExtractParameters("telemetry/{deviceId}", topic, out var p1))
                    chipId = p1["deviceId"];
                else if (TopicPatternMatcher.TryExtractParameters("events/{deviceId}", topic, out var p2))
                    chipId = p2["deviceId"];
                else if (TopicPatternMatcher.TryExtractParameters("status/{deviceId}", topic, out var p3))
                    chipId = p3["deviceId"];
            }

            if (string.IsNullOrEmpty(chipId))
            {
                chipId = !string.IsNullOrEmpty(imei) ? imei : "TICKET-VALIDATOR-UNKNOWN";
            }

            DateTime recordedAt = DateTime.UtcNow;
            if (root.TryGetProperty("Timestamp", out var tsElem) && tsElem.TryGetDateTime(out var parsedTs))
            {
                recordedAt = parsedTs.ToUniversalTime();
            }

            GeoPosition? position = null;
            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(macAddress)) attributes["mac"] = macAddress;
            if (!string.IsNullOrEmpty(groupName)) attributes["group"] = groupName;
            if (!string.IsNullOrEmpty(imei)) attributes["imei"] = imei;

            if (root.TryGetProperty("Details", out var detailsElem))
            {
                // Standard system telemetry
                if (detailsElem.TryGetProperty("rssi", out var rssiElem) && rssiElem.TryGetDouble(out var rssi))
                    metrics["rssi"] = rssi;
                if (detailsElem.TryGetProperty("free_heap", out var heapElem) && heapElem.TryGetDouble(out var heap))
                    metrics["free_heap_bytes"] = heap;
                if (detailsElem.TryGetProperty("uptime", out var upElem) && upElem.TryGetDouble(out var up))
                    metrics["uptime_seconds"] = up;

                // Cellular status
                if (detailsElem.TryGetProperty("cellular", out var cellElem))
                {
                    if (cellElem.TryGetProperty("operator", out var opElem))
                        attributes["cellular_operator"] = opElem.GetString() ?? string.Empty;
                    if (cellElem.TryGetProperty("signal_quality", out var sqElem) && sqElem.TryGetDouble(out var sq))
                        metrics["signal_quality"] = sq;
                }

                // GPS Location
                double lat = 0.0;
                double lon = 0.0;
                bool hasLat = (detailsElem.TryGetProperty("gps_latitude", out var lt1) && lt1.TryGetDouble(out lat)) ||
                              (detailsElem.TryGetProperty("GPS_Latitude", out var lt2) && lt2.TryGetDouble(out lat));
                bool hasLon = (detailsElem.TryGetProperty("gps_longitude", out var ln1) && ln1.TryGetDouble(out lon)) ||
                              (detailsElem.TryGetProperty("GPS_Longitude", out var ln2) && ln2.TryGetDouble(out lon));

                if (hasLat && hasLon && (Math.Abs(lat) > 0.0001 || Math.Abs(lon) > 0.0001))
                {
                    float? speed = detailsElem.TryGetProperty("gps_speed", out var spElem) && spElem.TryGetSingle(out var s) ? s : null;
                    short? sats = detailsElem.TryGetProperty("gps_satellites", out var satElem) && satElem.TryGetInt16(out var st) ? st : null;
                    var posRes = GeoPosition.Create(lat, lon, speed, null, null, sats, true);
                    if (posRes.IsSuccess)
                        position = posRes.Value;
                }

                // Ticket verification event
                if (detailsElem.TryGetProperty("CodeTicket", out var codeElem))
                {
                    attributes["code_ticket"] = codeElem.GetString() ?? string.Empty;
                    attributes["event_type"] = "TICKET_VALIDATION";
                }

                if (detailsElem.TryGetProperty("event", out var evNameElem))
                {
                    attributes["event_name"] = evNameElem.GetString() ?? string.Empty;
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
            return Result<NormalizedTelemetryEvent>.Failure(new Error("TicketValidatorAdapter.ParseError", ex.Message));
        }
    }
}
