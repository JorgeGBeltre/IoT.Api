using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using IoT.Protocols.Models;

namespace IoT.Protocols.Strategies;

/// <summary>
/// Familia de transporte / canal de ingestión.
/// Separa claramente dispositivos MQTT, HTTP y TCP Binario.
/// </summary>
public enum TransportFamily
{
    Mqtt,
    Http,
    TcpBinary
}

/// <summary>
/// Interfaz para estrategias de ingestión y decodificación de telemetría por familia de transporte.
/// Permite añadir nuevos formatos de tramas sin acoplar adaptadores a marcas o modelos físicos específicos.
/// </summary>
public interface ITelemetryStrategy
{
    string StrategyName { get; }
    TransportFamily Transport { get; }
    bool CanHandle(TransportMessage message);
    Result<CanonicalDeviceTelemetry> Parse(TransportMessage message);
}
