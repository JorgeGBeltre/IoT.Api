namespace IoT.Protocols.Tcp;

/// <summary>
/// Familia Queclink @Track (ASCII): tramas +RESP:/+BUFF:/+ACK: terminadas en '$'.
/// El IMEI va en el campo [2]. El decode lo hace QueclinkTrackProtocolAdapter (hint TCP-QUECLINK).
/// SACK opcional: por defecto NO se responde (muchos despliegues @Track no lo exigen).
/// </summary>
public sealed class QueclinkTcpHandler : ITcpProtocolHandler
{
    public string Family => "Queclink";

    public bool TryReadFrame(List<byte> buffer, string? boundImei, out TcpFrameOutcome? outcome)
    {
        outcome = null;
        if (!TcpFraming.TryReadUntil(buffer, (byte)'$', out var frame)) return false;

        string body = frame.TrimEnd('$');
        var parts = body.Split(',');
        string? imei = parts.Length > 2 ? parts[2].Trim() : null;

        outcome = new TcpFrameOutcome
        {
            SessionImei = string.IsNullOrWhiteSpace(imei) ? null : imei,
            IdentifiesSession = !string.IsNullOrWhiteSpace(imei),
            Ack = null,
            Ingest = true,
            IngestTopic = $"queclink/{imei}/telemetry",
            IngestPayload = frame,
            TransportProtocol = "TCP-QUECLINK",
        };
        return true;
    }

    public byte[]? BuildCommand(string commandText, ushort serial)
    {
        if (string.IsNullOrWhiteSpace(commandText)) return null;
        string cmd = commandText.Trim();
        if (!cmd.EndsWith('$')) cmd += "$";
        return System.Text.Encoding.ASCII.GetBytes(cmd);
    }
}
