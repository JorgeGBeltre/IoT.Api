using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using IoT.Protocols.Binary;
using Microsoft.Extensions.Logging;

namespace IoT.Protocols.Tcp;

/// <summary>
/// Familia GT06/Concox/SEEWORLD R12L-F (binario 0x78 0x78 / 0x79 0x79).
/// Reusa <see cref="R12LFFrame"/> para framing/decode y emite el payload canónico plano
/// que decodifica GpsAccidentAdapter. ACK de login/heartbeat/alarma según spec (5s).
/// </summary>
public class Gt06TcpHandler : ITcpProtocolHandler
{
    private readonly ILogger? _logger;

    // ILogger OPCIONAL (default null) para no romper los call sites que hacen
    // `new Gt06TcpHandler()` (tests, wiring de familias) ni la subclase MictrackTcpHandler.
    public Gt06TcpHandler(ILogger<Gt06TcpHandler>? logger = null) => _logger = logger;

    public virtual string Family => "GT06";

    /// <summary>
    /// Traduce el número de protocolo recibido al número CANÓNICO de la familia GT06.
    /// Por defecto no traduce nada. Los dialectos que renumeran las tramas sin cambiar su
    /// contenido (p.ej. Mictrack, que documenta posición 0x22 y alarma 0x26) lo sobrescriben.
    /// El ACK y el payload siempre usan el número ORIGINAL, que es el que el equipo espera.
    /// </summary>
    protected virtual byte NormalizeProtocol(byte protocolNumber) => protocolNumber;

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

        // El ACK echa de vuelta el número ORIGINAL recibido (es el que el equipo correlaciona);
        // el enrutado interno usa el número canónico de la familia.
        byte canonical = NormalizeProtocol(frame.ProtocolNumber);

        switch (canonical)
        {
            case R12LFFrame.ProtocolLogin:
                string imei = frame.ExtractImeiFromLoginPacket();
                outcome = new TcpFrameOutcome
                {
                    SessionImei = string.IsNullOrWhiteSpace(imei) ? null : imei,
                    IdentifiesSession = !string.IsNullOrWhiteSpace(imei),
                    Ack = R12LFFrame.BuildAck(frame.ProtocolNumber, frame.SerialNumber),
                    Ingest = false,
                };
                return true;

            case R12LFFrame.ProtocolStatus:
                byte[] statusAck = R12LFFrame.BuildAck(frame.ProtocolNumber, frame.SerialNumber);
                if (frame.InformationContent.Length >= 3 && !string.IsNullOrEmpty(boundImei))
                {
                    byte termInfo = frame.InformationContent[0];
                    byte voltLevel = frame.InformationContent[1];
                    byte gsmLevel = frame.InformationContent[2];

                    bool acc = (termInfo & 0x02) != 0;
                    bool charge = (termInfo & 0x04) != 0;
                    bool armed = (termInfo & 0x01) != 0;
                    // #4: Bit7 de Terminal Info = 1 DESCONECTADO, 0 conectado (corte de
                    // aceite/electricidad). Antes estaba invertido y reportaba lo contrario.
                    bool oilConnected = (termInfo & 0x80) == 0;

                    // #8: battery_level es el VoltageLevel CRUDO del GT06 (escala ordinal 0-6:
                    // 0=sin batería … 6=plena), NO un porcentaje. Antes este handler lo mapeaba
                    // a un % inventado mientras Gt06ProtocolAdapter guardaba el nivel crudo;
                    // ambas rutas emiten ahora el mismo nivel 0-6.
                    int batteryLevel = voltLevel;
                    int gsmPercent = Math.Min(100, gsmLevel * 25);

                    // #3: el campo Alarm/Language del 0x13 trae el tipo de alarma en el byte
                    // "former" (índice 3 del contenido); antes se ignoraba por completo.
                    string? alarmType = frame.InformationContent.Length >= 4
                        ? R12LFFrame.MapAlarmType(frame.InformationContent[3])
                        : null;

                    var statusObj = new
                    {
                        imei = boundImei,
                        acc = acc,
                        charge = charge,
                        defense_armed = armed,
                        oil_connected = oilConnected,
                        battery_level = batteryLevel,
                        gsm_signal = gsmPercent,
                        alarm_type = alarmType,
                        protocol_number = $"0x{frame.ProtocolNumber:X2}",
                        serial_number = frame.SerialNumber,
                        timestamp = DateTime.UtcNow.ToString("o")
                    };

                    outcome = new TcpFrameOutcome
                    {
                        Ack = statusAck,
                        Ingest = true,
                        IngestTopic = $"devices/{boundImei}/telemetry",
                        IngestPayload = JsonSerializer.Serialize(statusObj),
                        TransportProtocol = string.Empty,
                    };
                    return true;
                }

                outcome = new TcpFrameOutcome
                {
                    Ack = statusAck,
                    Ingest = false,
                };
                return true;

            case R12LFFrame.ProtocolStringInfo:
                string? commandResponse = null;
                if (frame.InformationContent.Length >= 5)
                {
                    int cmdLen = frame.InformationContent[0];
                    int textLen = Math.Max(0, cmdLen - 4);
                    if (frame.InformationContent.Length >= 5 + textLen)
                    {
                        commandResponse = Encoding.ASCII.GetString(frame.InformationContent, 5, textLen);
                    }
                    else if (frame.InformationContent.Length > 5)
                    {
                        commandResponse = Encoding.ASCII.GetString(frame.InformationContent, 5, frame.InformationContent.Length - 5);
                    }
                }

                var cmdObj = new
                {
                    imei = boundImei ?? string.Empty,
                    command_response = commandResponse,
                    protocol_number = $"0x{frame.ProtocolNumber:X2}",
                    serial_number = frame.SerialNumber,
                    timestamp = DateTime.UtcNow.ToString("o")
                };

                outcome = new TcpFrameOutcome
                {
                    Ack = null,
                    Ingest = !string.IsNullOrEmpty(boundImei),
                    IngestTopic = $"devices/{boundImei}/telemetry",
                    IngestPayload = JsonSerializer.Serialize(cmdObj),
                    TransportProtocol = string.Empty,
                };
                return true;

            case R12LFFrame.ProtocolLocation:
            case R12LFFrame.ProtocolAlarm:
                bool isAlarm = canonical == R12LFFrame.ProtocolAlarm;
                // Se decodifica según el número CANÓNICO: un dialecto puede haberlo enviado
                // con otro número (Mictrack: 0x22/0x26) y el cuerpo es el mismo.
                var (position, recordedAt, extras) = frame.DecodeLocationPacketDetailed(canonical);
                byte[]? ack = isAlarm ? R12LFFrame.BuildAck(frame.ProtocolNumber, frame.SerialNumber) : null;

                if (position == null)
                {
                    // Ni siquiera se pudo construir una posición (trama demasiado corta o
                    // coordenadas fuera de rango): ACK si aplica, sin ingesta.
                    outcome = new TcpFrameOutcome { Ack = ack, Ingest = false };
                    return true;
                }

                // #5: con o sin fix se CONSERVA la trama. Sin fix la posición va marcada
                // IsValid=false (sin inventar lat/lon) y se adjunta la celda LBS como metadato.
                // #3: alarm_type propaga el tipo de alarma del 0x16 (SOS, corte de energía, …).
                string? lbsCell = extras.Lbs is { } cell
                    ? $"{cell.Mcc}-{cell.Mnc}-{cell.Lac}-{cell.CellId}"
                    : null;

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
                    alarm_type = extras.AlarmType,
                    lbs = lbsCell,
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

            case R12LFFrame.ProtocolIccid:
            case R12LFFrame.ProtocolImsi:
                // Tramas 0x94 (ICCID) / 0x90 (IMSI): llevan IDENTIDAD de la SIM, NO posición
                // ni telemetría, así que se procesan como atributo (iccid/imsi, en el
                // allow-list CLIENT) sin exigir una posición. El decode es BCD fail-safe:
                // si el contenido no cuadra con el formato (plausibilidad), el campo queda
                // null y NO se puebla la identidad con basura (ver R12LFFrame.DecodeIccidPacket).
                var (iccidImei, iccidImsi, iccidVal) = frame.DecodeIccidPacket();

                // SEGURIDAD: la identidad de la SESIÓN la fija el LOGIN (0x01) y es AUTORITATIVA
                // durante toda la sesión. Una trama de identidad SIM (0x94/0x90) NUNCA
                // re-identifica ni re-enlaza la sesión. Si el IMEI embebido pudiera sobrescribir
                // el boundImei, un equipo ya logueado (o un atacante en su propia sesión TCP)
                // enviaría un 0x94 con el IMEI de OTRO dispositivo/tenant para re-atribuirse su
                // telemetría saltándose la autenticación del login.
                string? sessionImei;
                bool identifiesSession;
                if (!string.IsNullOrWhiteSpace(boundImei))
                {
                    // Sesión YA autenticada: la identidad es SIEMPRE el boundImei del login. La
                    // trama sólo puebla ICCID/IMSI del dispositivo ya enlazado; no identifica ni
                    // sustituye la identidad.
                    sessionImei = boundImei;
                    identifiesSession = false;

                    // Discrepancia: el IMEI embebido difiere del de la sesión -> se CONSERVA el
                    // boundImei del login y se registra (posible suplantación); no se re-atribuye.
                    if (!string.IsNullOrWhiteSpace(iccidImei) &&
                        !string.Equals(iccidImei, boundImei, StringComparison.Ordinal))
                        _logger?.LogWarning(
                            "Trama GT06 {Proto} con IMEI embebido {FrameImei} distinto del IMEI autenticado de la sesión {BoundImei}; posible suplantación. Se conserva la identidad del login y NO se re-atribuye la telemetría.",
                            canonical == R12LFFrame.ProtocolIccid ? "0x94 (ICCID)" : "0x90 (IMSI)",
                            iccidImei, boundImei);
                }
                else
                {
                    // Caso borde: trama de identidad SIM sin login previo. NO se confía ciegamente
                    // en el IMEI de la trama: se marca IdentifiesSession para que la capa de sesión
                    // aplique la MISMA compuerta de adopción que el login 0x01 (registrado + Active,
                    // tenant derivado del dispositivo). Sin IMEI plausible no identifica nada.
                    sessionImei = string.IsNullOrWhiteSpace(iccidImei) ? null : iccidImei;
                    identifiesSession = sessionImei != null;
                }

                if (canonical == R12LFFrame.ProtocolIccid && iccidVal is null && frame.InformationContent.Length > 0)
                    _logger?.LogWarning(
                        "Trama GT06 0x94 (ICCID) con {Len} bytes de contenido que no cuadran con un ICCID BCD plausible (19-20 dígitos, prefijo 89); identidad no poblada. Validar el layout contra una trama real del R12L-F físico.",
                        frame.InformationContent.Length);
                if (canonical == R12LFFrame.ProtocolImsi && iccidImsi is null && frame.InformationContent.Length > 0)
                    _logger?.LogWarning(
                        "Trama GT06 0x90 (IMSI) con {Len} bytes de contenido que no cuadran con un IMSI BCD de 15 dígitos; identidad no poblada. Validar el layout contra una trama real del R12L-F físico.",
                        frame.InformationContent.Length);

                var iccidPayload = new
                {
                    imei = sessionImei ?? string.Empty,
                    imsi = iccidImsi,
                    iccid = iccidVal,
                    packet_type = canonical == R12LFFrame.ProtocolIccid ? "iccid_info" : "imsi_info",
                    protocol_number = $"0x{frame.ProtocolNumber:X2}",
                    serial_number = frame.SerialNumber,
                    timestamp = DateTime.UtcNow.ToString("o")
                };

                outcome = new TcpFrameOutcome
                {
                    SessionImei = !string.IsNullOrWhiteSpace(sessionImei) ? sessionImei : null,
                    IdentifiesSession = identifiesSession,
                    Ack = null, // Spec R12L-F §5.5 no define respuesta ACK para 0x94/0x90
                    Ingest = !string.IsNullOrEmpty(sessionImei),
                    IngestTopic = $"devices/{sessionImei}/telemetry",
                    IngestPayload = JsonSerializer.Serialize(iccidPayload),
                    TransportProtocol = string.Empty,
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

        // Comando servidor 0x80. El tráfico REAL del equipo (apéndice B del spec R12L-F,
        // CRC-ITU verificado) NO lleva campo Language: el contenido va
        //   Protocol(1) | Length-of-Command(1) | ServerFlag(4) | ASCII(N) | Serial(2) | CRC(2).
        // (#2) Antes se añadían 2 bytes de Language (0x00 0x02) que el R12L-F físico omite;
        // debe validarse contra el equipo real.
        //
        // (#1) "Length of Command" = ServerFlag(4) + longitud del ASCII (p.ej. "DYD,000000#"
        //      de 11 bytes -> 0x0F). Antes estaba FIJO en 0x04, descuadrando el conteo.
        byte commandLength = (byte)(4 + contentBytes.Length);

        // Packet Length (de Protocol a CRC, ambos inclusive) =
        //   Protocol(1) + LenCmd(1) + ServerFlag(4) + ASCII(N) + Serial(2) + CRC(2) = 10 + N.
        byte packetLength = (byte)(10 + contentBytes.Length);

        // Región cubierta por el CRC: de Packet Length a Serial (ambos inclusive).
        Span<byte> toHash = stackalloc byte[1 + 1 + 1 + 4 + contentBytes.Length + 2];
        toHash[0] = packetLength;
        toHash[1] = R12LFFrame.ProtocolServerCommand;
        toHash[2] = commandLength;
        toHash[3] = 0x00;
        toHash[4] = 0x00;
        toHash[5] = 0x00;
        toHash[6] = 0x01;
        contentBytes.CopyTo(toHash.Slice(7, contentBytes.Length));
        int serialOffset = 7 + contentBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(toHash.Slice(serialOffset, 2), serial);

        ushort crc = R12LFCrc16.Compute(toHash);

        byte[] framed = new byte[5 + packetLength];
        framed[0] = 0x78;
        framed[1] = 0x78;
        framed[2] = packetLength;
        framed[3] = R12LFFrame.ProtocolServerCommand;
        framed[4] = commandLength;
        framed[5] = 0x00;
        framed[6] = 0x00;
        framed[7] = 0x00;
        framed[8] = 0x01;
        Array.Copy(contentBytes, 0, framed, 9, contentBytes.Length);
        int frameSerialOffset = 9 + contentBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(frameSerialOffset, 2), serial);
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(frameSerialOffset + 2, 2), crc);
        framed[frameSerialOffset + 4] = 0x0D;
        framed[frameSerialOffset + 5] = 0x0A;

        return framed;
    }
}
