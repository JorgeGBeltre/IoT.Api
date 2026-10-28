namespace IoT.Protocols.Contracts;

/// <summary>
/// Contrato que formaliza la separación entre Conector (gestión de transporte, sockets,
/// tópicos y sesiones) y Convertidor (Uplink/Downlink data converters).
/// Siguiendo la arquitectura de ThingsBoard PE Integrations.
/// </summary>
public interface IProtocolConnector
{
    /// <summary>Nombre del conector (ej: "MQTT", "TCP-GT06", "HTTP-WEBHOOK").</summary>
    string ConnectorName { get; }

    /// <summary>Tipo de transporte subyacente.</summary>
    string TransportType { get; }

    /// <summary>Convertidor de datos de subida asignado al conector.</summary>
    IUplinkConverter UplinkConverter { get; }

    /// <summary>Convertidor de datos de bajada asignado al conector.</summary>
    IDownlinkConverter? DownlinkConverter { get; }
}
