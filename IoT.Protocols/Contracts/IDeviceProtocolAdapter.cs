using IoT.Domain.Common;

namespace IoT.Protocols.Contracts;

public interface IDeviceProtocolAdapter : IUplinkConverter
{
    string ProtocolName { get; }
    string IUplinkConverter.ConverterId => ProtocolName;
    Result<NormalizedTelemetryEvent> Decode(TransportMessage message);

    Result<NormalizedTelemetryEvent> IUplinkConverter.ConvertUplink(TransportMessage message) => Decode(message);

    // Backward-compatible overloads for string-based topic and payload
    bool CanHandle(string topic, string payload) =>
        CanHandle(TransportMessage.Create(topic, payload));

    Result<NormalizedTelemetryEvent> Decode(string topic, string payload) =>
        Decode(TransportMessage.Create(topic, payload));
}
