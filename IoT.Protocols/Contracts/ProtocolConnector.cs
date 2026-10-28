namespace IoT.Protocols.Contracts;

/// <summary>
/// Implementación canónica de <see cref="IProtocolConnector"/> siguiendo la arquitectura de integraciones.
/// Formaliza la separación entre Conector (gestión de transporte, sockets, tópicos y sesiones) y
/// Convertidores de datos (Uplink/Downlink data converters).
/// </summary>
public sealed class ProtocolConnector : IProtocolConnector
{
    public string ConnectorName { get; }
    public string TransportType { get; }
    public IUplinkConverter UplinkConverter { get; }
    public IDownlinkConverter? DownlinkConverter { get; }

    public ProtocolConnector(
        string connectorName,
        string transportType,
        IUplinkConverter uplinkConverter,
        IDownlinkConverter? downlinkConverter = null)
    {
        ConnectorName = connectorName ?? throw new ArgumentNullException(nameof(connectorName));
        TransportType = transportType ?? throw new ArgumentNullException(nameof(transportType));
        UplinkConverter = uplinkConverter ?? throw new ArgumentNullException(nameof(uplinkConverter));
        DownlinkConverter = downlinkConverter;
    }
}
