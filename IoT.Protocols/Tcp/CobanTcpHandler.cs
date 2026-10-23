using System.Text;

namespace IoT.Protocols.Tcp;

/// <summary>
/// Familia Coban/TK103 (ASCII): tramas terminadas en ';'. Login de handshake
/// '##,imei:xxx,A;' → el servidor responde 'LOAD'. Datos 'imei:xxx,...;'.
/// El decode lo hace CobanProtocolAdapter (hint TCP-COBAN).
/// </summary>
public sealed class CobanTcpHandler : ITcpProtocolHandler
{
    public string Family => "Coban";

    public bool TryReadFrame(List<byte> buffer, string? boundImei, out TcpFrameOutcome? outcome)
    {
        outcome = null;
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
