using IoT.Domain.Common;
using IoT.Protocols.Adapters;
using IoT.Protocols.Contracts;
using IoT.Protocols.Profiles;

namespace IoT.Protocols;

public interface ICompositeProtocolAdapter
{
    Result<NormalizedTelemetryEvent> Decode(string topic, string payload);
    Result<NormalizedTelemetryEvent> Decode(TransportMessage message);
}

public sealed class CompositeProtocolAdapter : ICompositeProtocolAdapter
{
    private readonly IEnumerable<IDeviceProtocolAdapter> _adapters;
    private readonly IProtocolRegistry? _registry;
    private readonly StrategyBasedProtocolAdapter _universalStrategyAdapter = new();

    public CompositeProtocolAdapter(IEnumerable<IDeviceProtocolAdapter> adapters, IProtocolRegistry? registry = null)
    {
        _adapters = adapters;
        _registry = registry;
    }

    public CompositeProtocolAdapter(IProtocolRegistry registry)
    {
        _registry = registry;
        _adapters = [];
    }

    public static CompositeProtocolAdapter CreateDefault()
    {
        var registry = ProtocolRegistry.CreateDefault();
        return new CompositeProtocolAdapter(
        [
            new GpsAccidentAdapter(),
            new GlpPressureAdapter(),
            new RelaySwitchAdapter(),
            new TicketValidatorAdapter(),
            new MqttV5JsonAdapter(),
            new AccessControlAdapter(),
            new R12LFProtocolAdapter(),
            new Gt06ProtocolAdapter(),
            new QueclinkTrackProtocolAdapter(),
            new CobanProtocolAdapter(),
            new IStartekProtocolAdapter(),
            new JointechProtocolAdapter(),
            new OtaDeviceAdapter()
        ], registry);
    }

    public Result<NormalizedTelemetryEvent> Decode(string topic, string payload)
    {
        var msg = TransportMessage.FromMqtt(topic, payload);
        return Decode(msg);
    }

    public Result<NormalizedTelemetryEvent> Decode(TransportMessage message)
    {
        // 1. Intentar resolver vía perfil declarativo en el registro. Sólo se usa el
        //    adaptador del perfil si DE VERDAD puede manejar el mensaje (CanHandle); de lo
        //    contrario se cae al loop de adaptadores. Evita que un match de tópico enrute a
        //    un adaptador que luego devuelve un evento vacío (p.ej. JSON plano del gateway).
        if (_registry != null && !string.IsNullOrWhiteSpace(message.TopicOrRoute))
        {
            var profile = _registry.FindProfileForTopic(message.TopicOrRoute);
            if (profile != null && profile.Adapter.CanHandle(message))
            {
                return profile.Adapter.Decode(message);
            }
        }

        // 2. Probar adaptadores registrados
        foreach (var adapter in _adapters)
        {
            if (adapter.CanHandle(message))
            {
                return adapter.Decode(message);
            }
        }

        // 3. Fallback dinámico universal mediante Strategy (MQTT / HTTP / TCP) y AutoMapper
        if (_universalStrategyAdapter.CanHandle(message))
        {
            return _universalStrategyAdapter.Decode(message);
        }

        return Result<NormalizedTelemetryEvent>.Failure(
            new Error("ProtocolAdapter.NoHandler", $"No se encontró adaptador o estrategia para el mensaje en '{message.TopicOrRoute}'."));
    }
}
