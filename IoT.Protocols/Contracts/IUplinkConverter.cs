using IoT.Domain.Common;

namespace IoT.Protocols.Contracts;

/// <summary>
/// Contrato para convertidores de subida (Uplink Data Converters).
/// Desacopla la lógica de transformación de formato y mapeo de telemetría/atributos/posición
/// del transporte de red físico subyacente (TCP, MQTT, HTTP, LoRaWAN).
/// </summary>
public interface IUplinkConverter
{
    /// <summary>Identificador único del convertidor o protocolo.</summary>
    string ConverterId { get; }

    /// <summary>Evalúa si el convertidor es capaz de procesar el mensaje entrante.</summary>
    bool CanHandle(TransportMessage message);

    /// <summary>Decodifica y normaliza la carga útil en un evento de telemetría estándar.</summary>
    Result<NormalizedTelemetryEvent> ConvertUplink(TransportMessage message);
}
