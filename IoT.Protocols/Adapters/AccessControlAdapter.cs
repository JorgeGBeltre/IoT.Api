using System.Text.Json;
using IoT.Domain.Common;
using IoT.Domain.ValueObjects;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

public sealed class AccessControlAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "MQTT-AccessControl-v1";

    public bool CanHandle(TransportMessage message) => CanHandle(message.TopicOrRoute, message.Payload);

    public bool CanHandle(string topic, string payload)
    {
        if (topic.StartsWith("access/", StringComparison.OrdinalIgnoreCase) ||
            TopicPatternMatcher.MatchesMqttTopic("access/+/+", topic))
        {
            return true;
        }

        return payload.Contains("nfc_uid", StringComparison.OrdinalIgnoreCase) ||
               payload.Contains("qr_payload", StringComparison.OrdinalIgnoreCase) ||
               (payload.Contains("accel_x", StringComparison.OrdinalIgnoreCase) && payload.Contains("access", StringComparison.OrdinalIgnoreCase));
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
            if (TopicPatternMatcher.TryExtractParameters("access/{channel}/{deviceId}", topic, out var p))
            {
                chipId = p["deviceId"];
            }

            if (string.IsNullOrEmpty(chipId) && root.TryGetProperty("Device", out var devElem))
            {
                if (devElem.TryGetProperty("ChipId", out var cElem)) chipId = cElem.GetString() ?? string.Empty;
                else if (devElem.TryGetProperty("DeviceId", out var dElem)) chipId = dElem.GetString() ?? string.Empty;
            }

            if (string.IsNullOrEmpty(chipId))
            {
                chipId = "ACCESS-UNKNOWN";
            }

            DateTime recordedAt = DateTime.UtcNow;
            if (root.TryGetProperty("Timestamp", out var tsElem) && tsElem.TryGetDateTime(out var parsedTs))
            {
                recordedAt = parsedTs.ToUniversalTime();
            }

            GeoPosition? position = null;
            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("Details", out var detailsElem))
            {
                // Access Control: NFC card / QR code
                if (detailsElem.TryGetProperty("nfc_uid", out var nfcElem))
                    attributes["nfc_uid"] = nfcElem.GetString() ?? string.Empty;
                if (detailsElem.TryGetProperty("qr_payload", out var qrElem))
                    attributes["qr_payload"] = qrElem.GetString() ?? string.Empty;
                if (detailsElem.TryGetProperty("access_granted", out var accElem))
                    attributes["access_granted"] = accElem.GetBoolean().ToString();

                // MPU6050 Accelerometer
                if (detailsElem.TryGetProperty("accel_x", out var axElem) && axElem.TryGetDouble(out var ax)) metrics["accel_x_g"] = ax;
                if (detailsElem.TryGetProperty("accel_y", out var ayElem) && ayElem.TryGetDouble(out var ay)) metrics["accel_y_g"] = ay;
                if (detailsElem.TryGetProperty("accel_z", out var azElem) && azElem.TryGetDouble(out var az)) metrics["accel_z_g"] = az;

                // Health & System
                if (detailsElem.TryGetProperty("rssi", out var rssiElem) && rssiElem.TryGetDouble(out var rssi)) metrics["rssi"] = rssi;
                if (detailsElem.TryGetProperty("free_heap", out var heapElem) && heapElem.TryGetDouble(out var heap)) metrics["free_heap_bytes"] = heap;
                if (detailsElem.TryGetProperty("battery_mv", out var batElem) && batElem.TryGetDouble(out var bat)) metrics["battery_mv"] = bat;

                // GPS
                if (detailsElem.TryGetProperty("latitude", out var latElem) && latElem.TryGetDouble(out var lat) &&
                    detailsElem.TryGetProperty("longitude", out var lonElem) && lonElem.TryGetDouble(out var lon) &&
                    (Math.Abs(lat) > 0.0001 || Math.Abs(lon) > 0.0001))
                {
                    float? speed = detailsElem.TryGetProperty("speed", out var spElem) && spElem.TryGetSingle(out var s) ? s : null;
                    var posRes = GeoPosition.Create(lat, lon, speed, null, null, null, true);
                    if (posRes.IsSuccess)
                        position = posRes.Value;
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
            return Result<NormalizedTelemetryEvent>.Failure(new Error("AccessControlAdapter.ParseError", ex.Message));
        }
    }
}
