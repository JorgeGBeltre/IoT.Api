using IoT.Domain.Common;

namespace IoT.Protocols.Contracts;

public interface IDeviceProtocolAdapter
{
    string ProtocolName { get; }

    bool CanHandle(TransportMessage message);
    Result<NormalizedTelemetryEvent> Decode(TransportMessage message);

    // Backward-compatible overloads for string-based topic and payload
    bool CanHandle(string topic, string payload) =>
        CanHandle(TransportMessage.Create(topic, payload));

    Result<NormalizedTelemetryEvent> Decode(string topic, string payload) =>
        Decode(TransportMessage.Create(topic, payload));
}
