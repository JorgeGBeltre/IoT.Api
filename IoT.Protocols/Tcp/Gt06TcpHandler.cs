using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using IoT.Protocols.Binary;

namespace IoT.Protocols.Tcp;

/// <summary>
/// Familia GT06/Concox/SEEWORLD R12L-F (binario 0x78 0x78 / 0x79 0x79).
/// Reusa <see cref="R12LFFrame"/> para framing/decode y emite el payload canónico plano
/// que decodifica GpsAccidentAdapter. ACK de login/heartbeat/alarma según spec (5s).
/// </summary>
public sealed class Gt06TcpHandler : ITcpProtocolHandler
{
    public string Family => "GT06";

    public bool TryReadFrame(List<byte> buffer, string? boundImei, out TcpFrameOutcome? outcome)
    {
        outcome = null;
        if (buffer.Count < 10) return false;

        byte[] raw = buffer.ToArray();
        if (!R12LFFrame.TryParse(raw, out var frame, out int consumedBytes) || frame == null)
        {
            if (consumedBytes > 0 && consumedBytes <= buffer.Count)
                buffer.RemoveRange(0, consumedBytes);
            return false;
        }

        // En éxito, consumedBytes = ruido inicial + la trama completa (contrato probado).
        buffer.RemoveRange(0, Math.Min(consumedBytes, buffer.Count));

        switch (frame.ProtocolNumber)
        {
            case R12LFFrame.ProtocolLogin:
                string imei = frame.ExtractImeiFromLoginPacket();
                outcome = new TcpFrameOutcome
                {
                    SessionImei = string.IsNullOrWhiteSpace(imei) ? null : imei,
                    IdentifiesSession = !string.IsNullOrWhiteSpace(imei),
                    Ack = R12LFFrame.BuildAck(R12LFFrame.ProtocolLogin, frame.SerialNumber),
                    Ingest = false,
                };
                return true;

            case R12LFFrame.ProtocolStatus:
                outcome = new TcpFrameOutcome
                {
                    Ack = R12LFFrame.BuildAck(R12LFFrame.ProtocolStatus, frame.SerialNumber),
                    Ingest = false,
                };
                return true;

            case R12LFFrame.ProtocolLocation:
            case R12LFFrame.ProtocolAlarm:
                bool isAlarm = frame.ProtocolNumber == R12LFFrame.ProtocolAlarm;
                var (position, recordedAt) = frame.DecodeLocationPacket();
                byte[]? ack = isAlarm ? R12LFFrame.BuildAck(R12LFFrame.ProtocolAlarm, frame.SerialNumber) : null;

                if (position == null)
                {
                    // Sin posición (p.ej. sólo LBS): ACK si aplica, sin ingesta.
                    outcome = new TcpFrameOutcome { Ack = ack, Ingest = false };
                    return true;
                }

                var payloadObj = new
                {
                    imei = boundImei ?? string.Empty,
                    latitude = position.Latitude,
                    longitude = position.Longitude,
                    speed = position.SpeedKph,
                    course = position.Heading,
                    satellites = position.Satellites,
                    isValid = position.IsValid,
                    timestamp = recordedAt.ToString("o"),
                    is_alarm = isAlarm,
                    protocol_number = $"0x{frame.ProtocolNumber:X2}",
                    serial_number = frame.SerialNumber
                };

                outcome = new TcpFrameOutcome
                {
                    Ack = ack,
                    Ingest = !string.IsNullOrEmpty(boundImei),
                    IngestTopic = $"devices/{boundImei}/telemetry",
                    IngestPayload = JsonSerializer.Serialize(payloadObj),
                    TransportProtocol = string.Empty, // JSON plano → GpsAccidentAdapter
                };
                return true;

            default:
                outcome = new TcpFrameOutcome { Ingest = false };
                return true;
        }
    }

    public byte[]? BuildCommand(string commandText, ushort serial)
    {
        byte[] contentBytes = Encoding.ASCII.GetBytes(commandText);

        // Comando servidor 0x80. Length = Protocol(1) + [flagLen(1)+flag(4)+CommandLen+lang(2)]
        //   + Serial(2) + CRC(2) = 12 + CommandLen. (El protocolo cuenta en Length; con 11 el
        //   array 'framed' quedaba 1 byte corto y BuildCommand lanzaba IndexOutOfRange.)
        byte packetLength = (byte)(12 + contentBytes.Length);

        Span<byte> toHash = stackalloc byte[1 + 1 + 1 + 4 + contentBytes.Length + 2 + 2];
        toHash[0] = packetLength;
        toHash[1] = R12LFFrame.ProtocolServerCommand;
        toHash[2] = 0x04;
        toHash[3] = 0x00;
        toHash[4] = 0x00;
        toHash[5] = 0x00;
        toHash[6] = 0x01;
        contentBytes.CopyTo(toHash.Slice(7, contentBytes.Length));
        int offset = 7 + contentBytes.Length;
        toHash[offset] = 0x00;     // Language 0x0002 = English
        toHash[offset + 1] = 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(toHash.Slice(offset + 2, 2), serial);

        ushort crc = R12LFCrc16.Compute(toHash);

        byte[] framed = new byte[5 + packetLength];
        framed[0] = 0x78;
        framed[1] = 0x78;
        framed[2] = packetLength;
        framed[3] = R12LFFrame.ProtocolServerCommand;
        framed[4] = 0x04;
        framed[5] = 0x00;
        framed[6] = 0x00;
        framed[7] = 0x00;
        framed[8] = 0x01;
        Array.Copy(contentBytes, 0, framed, 9, contentBytes.Length);
        int frameOffset = 9 + contentBytes.Length;
        framed[frameOffset] = 0x00;
        framed[frameOffset + 1] = 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(frameOffset + 2, 2), serial);
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(frameOffset + 4, 2), crc);
        framed[frameOffset + 6] = 0x0D;
        framed[frameOffset + 7] = 0x0A;

        return framed;
    }
}
