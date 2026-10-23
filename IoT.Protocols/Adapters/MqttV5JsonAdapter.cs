using System.Text.Json;
using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

public sealed class MqttV5JsonAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "MQTTv5-JSON";

    public bool CanHandle(TransportMessage message) => CanHandle(message.TopicOrRoute, message.Payload);

    public bool CanHandle(string topic, string payload)
    {
        if (topic.StartsWith("mqttv5/", StringComparison.OrdinalIgnoreCase) ||
            TopicPatternMatcher.MatchesMqttTopic("mqttv5/+", topic))
        {
            return true;
        }

        return payload.Contains("MQTTv5_JSON", StringComparison.OrdinalIgnoreCase);
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
            string chipId = "MQTTV5-DEVICE";
            DateTime recordedAt = DateTime.UtcNow;
            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (payload.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                if (root.TryGetProperty("Device", out var devElem) && devElem.TryGetProperty("ChipId", out var cElem))
                    chipId = cElem.GetString() ?? chipId;
                else if (root.TryGetProperty("chipId", out var idElem))
                    chipId = idElem.GetString() ?? chipId;

                if (root.TryGetProperty("Timestamp", out var tsElem) && tsElem.TryGetDateTime(out var parsedTs))
                    recordedAt = parsedTs.ToUniversalTime();

                if (root.TryGetProperty("Details", out var detailsElem))
                {
                    foreach (var prop in detailsElem.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var num))
                        {
                            metrics[prop.Name] = num;
                        }
                        else
                        {
                            attributes[prop.Name] = prop.Value.ToString();
                        }
                    }
                }
            }
            else
            {
                // Plain string status (e.g. "online", "offline")
                attributes["status_text"] = payload;
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
            return Result<NormalizedTelemetryEvent>.Failure(new Error("MqttV5JsonAdapter.ParseError", ex.Message));
        }
    }
}
