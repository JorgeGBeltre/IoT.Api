namespace IoT.Protocols.Tcp;

/// <summary>
/// Resultado de leer UNA trama del flujo TCP de una familia de protocolo.
/// Separa el framing/handshake (específico de cada wire-protocol) de la ingesta
/// (que reusa los adaptadores existentes vía <see cref="TransportProtocol"/>).
/// </summary>
public sealed record TcpFrameOutcome
{
    /// <summary>IMEI/ChipId que identifica esta trama (login o cada-trama en ASCII); null si usa el de la sesión ya enlazada.</summary>
    public string? SessionImei { get; init; }

    /// <summary>true cuando <see cref="SessionImei"/> es la identidad de adopción (debe validarse contra el registro).</summary>
    public bool IdentifiesSession { get; init; }

    /// <summary>Bytes de respuesta al dispositivo (ACK/handshake), o null si no hay.</summary>
    public byte[]? Ack { get; init; }

    /// <summary>true si esta trama debe ingerirse al pipeline.</summary>
    public bool Ingest { get; init; }

    public string IngestTopic { get; init; } = string.Empty;
    public string IngestPayload { get; init; } = string.Empty;

    /// <summary>Hint de transporte para enrutar el decode (p.ej. "TCP-QUECLINK"); vacío = enrutar por tópico/payload.</summary>
    public string TransportProtocol { get; init; } = string.Empty;
}

/// <summary>
/// Maneja el wire-protocol de UNA familia de dispositivos sobre TCP: extrae tramas del
/// buffer acumulado, identifica el IMEI, produce el ACK/handshake y deja el payload listo
/// para la ingesta. NO hace I/O ni accede a la base; eso lo orquesta el gateway.
/// </summary>
public interface ITcpProtocolHandler
{
    /// <summary>Nombre de la familia (GT06, Queclink, Coban, iStartek, Jointech).</summary>
    string Family { get; }

    /// <summary>
    /// Intenta extraer y parsear la siguiente trama completa del buffer, consumiendo sus bytes.
    /// Devuelve false si todavía no hay una trama completa (esperar más datos).
    /// </summary>
    /// <param name="buffer">Acumulador de bytes recibidos (se le quitan los consumidos).</param>
    /// <param name="boundImei">IMEI ya enlazado a la sesión (para tramas de datos sin identidad propia).</param>
    bool TryReadFrame(List<byte> buffer, string? boundImei, out TcpFrameOutcome? outcome);

    /// <summary>Construye una trama de comando saliente para esta familia, o null si no se soporta.</summary>
    byte[]? BuildCommand(string commandText, ushort serial);
}
