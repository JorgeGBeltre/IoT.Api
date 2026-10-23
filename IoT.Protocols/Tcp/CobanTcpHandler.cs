using System.Text;

namespace IoT.Protocols.Tcp;

/// <summary>
/// Familia Coban/TK103 (ASCII): tramas terminadas en ';'. Login de handshake
/// '##,imei:xxx,A;' → el servidor responde 'LOAD'. Datos 'imei:xxx,...;'.
/// El decode lo hace CobanProtocolAdapter (hint TCP-COBAN).
///
/// El HEARTBEAT es un caso aparte: según el spec (References/Protocolos/Coban protocolo
/// GPRS.pdf, "2. Heartbeat-packets") su contenido es el número IMEI DESNUDO, sin ';' ni
/// ningún terminador, y el servidor debe responder 'ON' — si el equipo no lo recibe tres
/// veces se considera offline y reconecta. Al no llevar terminador, el framing por ';'
/// nunca lo extraía: quedaba en el buffer y se pegaba delante de la trama siguiente, que
/// ya no se podía identificar ni decodificar. Se perdía una trama de telemetría por latido.
/// </summary>
public sealed class CobanTcpHandler : ITcpProtocolHandler
{
    public string Family => "Coban";

    /// <summary>Longitud estándar de un IMEI (GSM TS 23.003).</summary>
    private const int ImeiLength = 15;

    /// <summary>Margen aceptado alrededor del IMEI para identificadores no estándar.</summary>
    private const int MinHeartbeatDigits = 10;
    private const int MaxHeartbeatDigits = 20;

    public bool TryReadFrame(List<byte> buffer, string? boundImei, out TcpFrameOutcome? outcome)
    {
        outcome = null;

        if (TryReadHeartbeat(buffer, boundImei, out outcome)) return true;

        if (!TcpFraming.TryReadUntil(buffer, (byte)';', out var frame)) return false;

        string body = frame.TrimEnd(';');
        var parts = body.Split(',');
        string? imei = ExtractImei(parts);

        bool isHandshake = body.StartsWith("##", StringComparison.Ordinal);
        if (isHandshake)
        {
            // Responder 'LOAD' para completar el login Coban; no hay telemetría que ingerir.
            outcome = new TcpFrameOutcome
            {
                SessionImei = string.IsNullOrWhiteSpace(imei) ? null : imei,
                IdentifiesSession = !string.IsNullOrWhiteSpace(imei),
                Ack = Encoding.ASCII.GetBytes("LOAD"),
                Ingest = false,
            };
            return true;
        }

        outcome = new TcpFrameOutcome
        {
            SessionImei = string.IsNullOrWhiteSpace(imei) ? null : imei,
            IdentifiesSession = !string.IsNullOrWhiteSpace(imei),
            Ack = null,
            Ingest = true,
            IngestTopic = $"coban/{imei}/telemetry",
            IngestPayload = frame,
            TransportProtocol = "TCP-COBAN",
        };
        return true;
    }

    /// <summary>
    /// Detecta y consume el latido: una ristra de dígitos al principio del buffer que NO
    /// forma parte de una trama de datos (las de datos empiezan por "imei:" o "##").
    /// Responde 'ON', que es lo que el spec exige.
    ///
    /// El corte se hace sólo cuando se puede determinar el final del número con certeza,
    /// para no partir un IMEI que llegue fragmentado en dos segmentos TCP.
    /// </summary>
    private static bool TryReadHeartbeat(List<byte> buffer, string? boundImei, out TcpFrameOutcome? outcome)
    {
        outcome = null;

        // Saltar CR/LF/espacios iniciales que hubiera dejado la trama anterior.
        int start = 0;
        while (start < buffer.Count &&
               (buffer[start] == (byte)'\r' || buffer[start] == (byte)'\n' || buffer[start] == (byte)' '))
        {
            start++;
        }
        if (start >= buffer.Count) return false;
        if (!IsAsciiDigit(buffer[start])) return false;

        int end = start;
        while (end < buffer.Count && IsAsciiDigit(buffer[end])) end++;
        int digitCount = end - start;

        bool atBufferEnd = end >= buffer.Count;
        int heartbeatLength;

        if (!string.IsNullOrEmpty(boundImei) &&
            digitCount >= boundImei.Length &&
            MatchesAt(buffer, start, boundImei))
        {
            // Caso más fiable: la sesión ya tiene IMEI y el latido es exactamente ese.
            heartbeatLength = boundImei.Length;
        }
        else if (!atBufferEnd && digitCount is >= MinHeartbeatDigits and <= MaxHeartbeatDigits)
        {
            // Hay un byte no numérico detrás: el número está completo (le sigue CR/LF o
            // el principio de la trama siguiente).
            heartbeatLength = digitCount;
        }
        else if (atBufferEnd && digitCount == ImeiLength)
        {
            // Final de buffer: sólo se asume completo con la longitud exacta de un IMEI,
            // porque un número más corto puede ser un fragmento aún por llegar.
            heartbeatLength = digitCount;
        }
        else
        {
            return false;
        }

        string imei = Encoding.ASCII.GetString(buffer.ToArray(), start, heartbeatLength);

        int consumed = start + heartbeatLength;
        while (consumed < buffer.Count && (buffer[consumed] == (byte)'\r' || buffer[consumed] == (byte)'\n'))
        {
            consumed++;
        }
        buffer.RemoveRange(0, consumed);

        outcome = new TcpFrameOutcome
        {
            SessionImei = imei,
            IdentifiesSession = true,
            // El spec exige responder 'ON'; sin respuesta el equipo se declara offline a
            // los tres latidos y reconecta. El gateway sólo envía el ACK si el IMEI está
            // registrado y activo (compuerta de adopción), que es lo que queremos.
            Ack = Encoding.ASCII.GetBytes("ON"),
            Ingest = false,
        };
        return true;
    }

    private static bool IsAsciiDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';

    private static bool MatchesAt(List<byte> buffer, int offset, string text)
    {
        if (offset + text.Length > buffer.Count) return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (buffer[offset + i] != (byte)text[i]) return false;
        }
        return true;
    }

    private static string? ExtractImei(string[] parts)
    {
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.StartsWith("imei:", StringComparison.OrdinalIgnoreCase))
                return t["imei:".Length..].Trim();
        }
        return null;
    }

    public byte[]? BuildCommand(string commandText, ushort serial) => null;
}
