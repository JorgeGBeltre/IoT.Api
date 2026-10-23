using System.Text;

namespace IoT.Protocols.Tcp;

/// <summary>
/// Familia Jointech JT701D (ASCII): tramas '(ID,Pxx,...)'. El ID (10 díg) va en el campo [0].
/// Para reportes P45 (unlock/lock) el servidor responde '(P69,0,SN)' con el SN del campo [16]
/// (confirmado en la Guía de Integración JT701D). El decode lo hace JointechProtocolAdapter
/// (hint TCP-JOINTECH).
/// </summary>
public sealed class JointechTcpHandler : ITcpProtocolHandler
{
    public string Family => "Jointech";

    public bool TryReadFrame(List<byte> buffer, string? boundImei, out TcpFrameOutcome? outcome)
    {
        outcome = null;
        if (!TcpFraming.TryReadParenFrame(buffer, out var frame)) return false;

        var parts = frame.Trim('(', ')').Split(',');
        string? id = parts.Length > 0 ? parts[0].Trim() : null;

        byte[]? ack = null;
        bool isP45 = parts.Length > 1 && string.Equals(parts[1].Trim(), "P45", StringComparison.OrdinalIgnoreCase);
        if (isP45 && parts.Length > 16)
        {
            // Reconocimiento universal P69 con el nº de serie del reporte (campo 16).
            ack = Encoding.ASCII.GetBytes($"(P69,0,{parts[16].Trim()})");
        }

        outcome = new TcpFrameOutcome
        {
            SessionImei = string.IsNullOrWhiteSpace(id) ? null : id,
            IdentifiesSession = !string.IsNullOrWhiteSpace(id),
            Ack = ack,
            Ingest = true,
            IngestTopic = $"jointech/{id}/telemetry",
            IngestPayload = frame,
            TransportProtocol = "TCP-JOINTECH",
        };
        return true;
    }

    public byte[]? BuildCommand(string commandText, ushort serial) => null;
}
