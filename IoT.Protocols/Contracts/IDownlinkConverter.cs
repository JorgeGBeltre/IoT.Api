using IoT.Domain.Common;

namespace IoT.Protocols.Contracts;

/// <summary>
/// Solicitud de comando de bajada hacia un dispositivo.
/// </summary>
public sealed record DownlinkCommandRequest(
    string DeviceIdentifier,
    string CommandName,
    string? PayloadJson = null,
    ushort Sequence = 1,
    Dictionary<string, string>? Metadata = null);

/// <summary>
/// Carga útil codificada lista para el transporte (bytes binarios o texto formateado).
/// </summary>
public sealed record DownlinkWireFrame(
    string TargetTopicOrAddress,
    byte[] RawBytes,
    string? TextPayload = null,
    bool IsBinary = false);

/// <summary>
/// Contrato para convertidores de bajada (Downlink Data Converters).
/// Transforma intenciones o comandos de la plataforma (ej. reset, cut-fuel, set-interval)
/// a la trama específica que entiende el hardware o firmware según su protocolo.
/// </summary>
public interface IDownlinkConverter
{
    /// <summary>Identificador único del convertidor o protocolo.</summary>
    string ConverterId { get; }

    /// <summary>Evalúa si este convertidor maneja el comando solicitado.</summary>
    bool CanHandle(DownlinkCommandRequest command);

    /// <summary>Codifica el comando a su representación de red final.</summary>
    Result<DownlinkWireFrame> ConvertDownlink(DownlinkCommandRequest command);
}
