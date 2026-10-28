using IoT.Domain.Common;
using IoT.Protocols.Contracts;

namespace IoT.Protocols.Dynamic;

/// <summary>
/// Adaptador de protocolo que ejecuta lógica JavaScript sandboxed (Jint) mediante <see cref="ScriptableUplinkConverter"/>
/// y opcionalmente <see cref="ScriptableDownlinkConverter"/>. Permite configurar perfiles de protocolo dinámicos
/// sin recompilar la solución.
/// </summary>
public sealed class ScriptableProtocolAdapter : IDeviceProtocolAdapter
{
    private readonly ScriptableUplinkConverter _uplinkConverter;
    private readonly ScriptableDownlinkConverter? _downlinkConverter;

    public string ProtocolName => _uplinkConverter.ConverterId;
    public IUplinkConverter UplinkConverter => _uplinkConverter;
    public IDownlinkConverter? DownlinkConverter => _downlinkConverter;

    public ScriptableProtocolAdapter(
        string protocolName,
        string uplinkScript,
        string? downlinkScript = null,
        Func<TransportMessage, bool>? uplinkPredicate = null,
        Func<DownlinkCommandRequest, bool>? downlinkPredicate = null)
    {
        _uplinkConverter = new ScriptableUplinkConverter(protocolName, uplinkScript, uplinkPredicate);
        if (!string.IsNullOrWhiteSpace(downlinkScript))
        {
            _downlinkConverter = new ScriptableDownlinkConverter(protocolName, downlinkScript, downlinkPredicate);
        }
    }

    public bool CanHandle(TransportMessage message) => _uplinkConverter.CanHandle(message);

    public Result<NormalizedTelemetryEvent> Decode(TransportMessage message) => _uplinkConverter.ConvertUplink(message);

    public Result<NormalizedTelemetryEvent> Decode(string topic, string payload)
    {
        return Decode(TransportMessage.FromMqtt(topic, payload));
    }
}
