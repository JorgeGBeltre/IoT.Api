using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using IoT.Protocols.Http;
using IoT.Protocols.Models;
using IoT.Protocols.Mqtt;
using IoT.Protocols.Strategies;
using IoT.Protocols.Tcp;

namespace IoT.Protocols.Adapters;

/// <summary>
/// Adaptador de protocolo universal desacoplado de hardware específico.
/// Utiliza el patrón Strategy para admitir diversas familias de transporte (MQTT, HTTP, TCP binario)
/// y AutoMapper para traducir automáticamente cualquier trama recibida hacia el modelo canónico.
/// </summary>
public class StrategyBasedProtocolAdapter : IDeviceProtocolAdapter
{
    private readonly List<ITelemetryStrategy> _strategies = [];

    public virtual string ProtocolName => "Universal-Strategy-Adapter";

    public IReadOnlyList<ITelemetryStrategy> Strategies => _strategies.AsReadOnly();

    public StrategyBasedProtocolAdapter()
    {
        // Registrar estrategias predeterminadas organizadas por familia de transporte
        _strategies.Add(new MqttTelemetryStrategy());
        _strategies.Add(new HttpJsonTelemetryStrategy());
        _strategies.Add(new HttpUrlEncodedTelemetryStrategy());
        _strategies.Add(new HttpCustomFrameTelemetryStrategy());
        _strategies.Add(new TcpBinaryTelemetryStrategy());
    }

    public StrategyBasedProtocolAdapter(IEnumerable<ITelemetryStrategy> customStrategies)
    {
        _strategies.AddRange(customStrategies);
    }

    public void RegisterStrategy(ITelemetryStrategy strategy)
    {
        _strategies.Insert(0, strategy); // Mayor prioridad a estrategias agregadas
    }

    public virtual bool CanHandle(TransportMessage message)
    {
        return _strategies.Any(s => s.CanHandle(message));
    }

    public virtual bool CanHandle(string topic, string payload)
    {
        var msg = TransportMessage.FromMqtt(topic, payload);
        return CanHandle(msg);
    }

    public virtual Result<NormalizedTelemetryEvent> Decode(TransportMessage message)
    {
        var matchingStrategy = _strategies.FirstOrDefault(s => s.CanHandle(message));
        if (matchingStrategy == null)
        {
            return Result<NormalizedTelemetryEvent>.Failure(
                new Error("StrategyAdapter.NoStrategyFound",
                    $"No se encontró ninguna estrategia para el transporte '{message.TransportHint}' y payload shape."));
        }

        var parseResult = matchingStrategy.Parse(message);
        if (!parseResult.IsSuccess)
        {
            return Result<NormalizedTelemetryEvent>.Failure(parseResult.Error);
        }

        var canonical = parseResult.Value;
        var rawPayloadStr = !string.IsNullOrEmpty(message.Payload)
            ? message.Payload
            : Convert.ToHexString(message.RawBytes);

        var normalized = canonical.ToNormalizedEvent(
            fallbackDeviceId: message.DeviceIdentifier,
            messageId: message.MessageId,
            rawPayload: rawPayloadStr);

        normalized.ReceivedAt = message.ReceivedAt;

        // Registrar estrategia utilizada en los atributos de observabilidad
        normalized.Attributes["ingestion_strategy"] = matchingStrategy.StrategyName;
        normalized.Attributes["transport_family"] = matchingStrategy.Transport.ToString();

        return Result<NormalizedTelemetryEvent>.Success(normalized);
    }

    public virtual Result<NormalizedTelemetryEvent> Decode(string topic, string payload)
    {
        var msg = TransportMessage.FromMqtt(topic, payload);
        return Decode(msg);
    }
}
