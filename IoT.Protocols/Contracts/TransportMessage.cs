using System.Text;

namespace IoT.Protocols.Contracts;

public sealed record TransportMessage
{
    public string TopicOrRoute { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public byte[]? BinaryPayload { get; init; }
    public string? MessageId { get; init; }
    public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string TransportProtocol { get; init; } = "MQTT";
    public string Source { get; init; } = "real";
    public string DeviceIdentifier { get; init; } = string.Empty;

    public string TransportHint => TransportProtocol;
    public Dictionary<string, string> Metadata => Headers;

    public byte[] RawBytes => BinaryPayload ?? Encoding.UTF8.GetBytes(Payload);

    public static TransportMessage Create(
        string topicOrRoute,
        string payload,
        string? messageId = null,
        Dictionary<string, string>? headers = null,
        string transportProtocol = "MQTT",
        string source = "real",
        DateTime? receivedAt = null,
        string deviceIdentifier = "")
    {
        var h = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!h.ContainsKey("topic") && !string.IsNullOrWhiteSpace(topicOrRoute))
        {
            h["topic"] = topicOrRoute;
        }

        return new TransportMessage
        {
            TopicOrRoute = topicOrRoute,
            Payload = payload,
            // NO autogenerar: si el protocolo no trae MessageId, se deja null para que el
            // pipeline calcule un hash determinista (device+tiempo+payload) y la dedup funcione.
            MessageId = messageId,
            Headers = h,
            TransportProtocol = transportProtocol,
            Source = source,
            ReceivedAt = receivedAt ?? DateTime.UtcNow,
            DeviceIdentifier = deviceIdentifier
        };
    }

    public static TransportMessage FromMqtt(string topic, string payload, string? messageId = null, string deviceId = "")
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["topic"] = topic
        };

        return Create(
            topicOrRoute: topic,
            payload: payload,
            messageId: messageId,
            headers: headers,
            transportProtocol: "MQTT",
            deviceIdentifier: deviceId);
    }

    public static TransportMessage FromHttp(string route, string payload, Dictionary<string, string>? headers = null, string deviceId = "")
    {
        return Create(
            topicOrRoute: route,
            payload: payload,
            headers: headers,
            transportProtocol: "HTTP",
            deviceIdentifier: deviceId);
    }

    public static TransportMessage FromTcpBinary(string route, byte[] binaryPayload, string deviceId = "", string? messageId = null)
    {
        return new TransportMessage
        {
            TopicOrRoute = route,
            Payload = Encoding.UTF8.GetString(binaryPayload),
            BinaryPayload = binaryPayload,
            MessageId = messageId, // null ⇒ el pipeline usa hash determinista (ver Create)
            TransportProtocol = "TCP-Binary",
            DeviceIdentifier = deviceId,
            ReceivedAt = DateTime.UtcNow
        };
    }
}
