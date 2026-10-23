namespace IoT.Protocols.Tcp;

/// <summary>
/// Familia iStartek (ASCII V1.6): tramas '&amp;&amp;...' terminadas en LF. El IMEI va en el
/// campo [1]. El decode lo hace IStartekProtocolAdapter (hint TCP-ISTARTEK).
/// NOTA: el formato GPRS binario '$$' NO se maneja aquí (requiere adaptador binario aparte).
/// </summary>
public sealed class IStartekTcpHandler : ITcpProtocolHandler
{
    public string Family => "iStartek";

    public bool TryReadFrame(List<byte> buffer, string? boundImei, out TcpFrameOutcome? outcome)
    {
        outcome = null;
        if (!TcpFraming.TryReadUntil(buffer, (byte)'\n', out var frame)) return false;

        if (!frame.StartsWith("&&", StringComparison.Ordinal))
        {
            // Trama no reconocida (p.ej. GPRS binario '$$'): ignorar sin ingerir ni ACKear.
            outcome = new TcpFrameOutcome { Ingest = false };
            return true;
        }

        var parts = frame.Split(',');
        string? imei = parts.Length > 1 ? parts[1].Trim() : null;

        outcome = new TcpFrameOutcome
        {
            SessionImei = string.IsNullOrWhiteSpace(imei) ? null : imei,
            IdentifiesSession = !string.IsNullOrWhiteSpace(imei),
            Ack = null,
            Ingest = true,
            IngestTopic = $"istartek/{imei}/telemetry",
            IngestPayload = frame,
            TransportProtocol = "TCP-ISTARTEK",
        };
        return true;
    }

    public byte[]? BuildCommand(string commandText, ushort serial) => null;
}
