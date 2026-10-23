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
        if (topic.StartsWith("relays/", StringComparison.OrdinalIgnoreCase) ||
            topic.StartsWith("relays/", StringComparison.OrdinalIgnoreCase) ||
            topic.EndsWith("/state", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return payload.Contains("devices_count", StringComparison.OrdinalIgnoreCase) ||
               payload.Contains("desired_state", StringComparison.OrdinalIgnoreCase) ||
               payload.Contains("device_switch", StringComparison.OrdinalIgnoreCase);
    }

    public Result<NormalizedTelemetryEvent> Decode(string topic, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            string chipId = string.Empty;
            if (root.TryGetProperty("Device", out var devElem) && devElem.TryGetProperty("ChipId", out var chipElem))
                chipId = chipElem.GetString() ?? string.Empty;

            if (string.IsNullOrEmpty(chipId))
                chipId = "Relay-Switch";

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

                if (detailsElem.TryGetProperty("devices", out var switchesElem) && switchesElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in switchesElem.EnumerateObject())
                    {
                        var switchData = prop.Value;
                        if (switchData.TryGetProperty("state", out var stateElem))
                        {
                            string stateStr = stateElem.GetString() ?? "off";
                            attributes[$"{prop.Name}_state"] = stateStr;
                            metrics[$"{prop.Name}_state_binary"] = stateStr.Equals("on", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
                        }

                        if (switchData.TryGetProperty("desired_state", out var desElem))
                            attributes[$"{prop.Name}_desired_state"] = desElem.GetString() ?? "off";
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
