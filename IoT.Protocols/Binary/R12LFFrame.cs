using System.Buffers.Binary;
using System.Text;
using IoT.Domain.Common;
using IoT.Domain.ValueObjects;

namespace IoT.Protocols.Binary;

public sealed record R12LFFrame
{
    public byte StartBit1 { get; init; } = 0x78;
    public byte StartBit2 { get; init; } = 0x78;
    public byte PacketLength { get; init; }
    public byte ProtocolNumber { get; init; }
    public byte[] InformationContent { get; init; } = [];
    public ushort SerialNumber { get; init; }
    public ushort Crc { get; init; }

    public const byte ProtocolLogin = 0x01;
    public const byte ProtocolLocation = 0x12;
    public const byte ProtocolStatus = 0x13;
    public const byte ProtocolStringInfo = 0x15;
    public const byte ProtocolAlarm = 0x16;
    public const byte ProtocolServerCommand = 0x80;

    public static bool TryParse(ReadOnlySpan<byte> buffer, out R12LFFrame? frame, out int consumedBytes)
    {
        frame = null;
        consumedBytes = 0;

        if (buffer.Length < 10)
            return false;

        // Find Start Bit (0x78 0x78 or 0x79 0x79)
        int startIndex = -1;
        for (int i = 0; i <= buffer.Length - 10; i++)
        {
            if ((buffer[i] == 0x78 && buffer[i + 1] == 0x78) ||
                (buffer[i] == 0x79 && buffer[i + 1] == 0x79))
            {
                startIndex = i;
                break;
            }
        }

        if (startIndex == -1)
        {
            consumedBytes = buffer.Length - 1; // discard noise up to last byte
            return false;
        }

        consumedBytes = startIndex; // discard noise before frame start

        var slice = buffer[startIndex..];
        byte start1 = slice[0];
        byte start2 = slice[1];

        // For standard 0x78 0x78: Packet length is 1 byte at index 2
        // Length = ProtocolNumber (1) + InfoContent (N) + Serial (2) + ErrorCheck (2) = 5 + N
        // Total frame size = Start(2) + Length(1) + PacketLength + Stop(2) = 5 + PacketLength
        if (start1 == 0x78)
        {
            byte lengthByte = slice[2];
            int totalFrameSize = 5 + lengthByte;

            if (slice.Length < totalFrameSize)
                return false; // Incomplete packet, wait for more data

            // Check stop bits (0x0D 0x0A)
            if (slice[totalFrameSize - 2] != 0x0D || slice[totalFrameSize - 1] != 0x0A)
            {
                consumedBytes += 2; // invalid framing, advance past start bits
                return false;
            }

            // CRC check over: PacketLength (1) to SerialNumber (index 2 to totalFrameSize - 5)
            var crcSpan = slice.Slice(2, lengthByte - 1);
            ushort expectedCrc = BinaryPrimitives.ReadUInt16BigEndian(slice.Slice(totalFrameSize - 4, 2));
            ushort computedCrc = R12LFCrc16.Compute(crcSpan);

            if (computedCrc != expectedCrc)
            {
                consumedBytes += totalFrameSize; // corrupt frame, discard
                return false;
            }

            byte protocol = slice[3];
            int infoLen = lengthByte - 5;
            byte[] info = infoLen > 0 ? slice.Slice(4, infoLen).ToArray() : [];
            ushort serial = BinaryPrimitives.ReadUInt16BigEndian(slice.Slice(4 + infoLen, 2));

            frame = new R12LFFrame
            {
                StartBit1 = start1,
                StartBit2 = start2,
                PacketLength = lengthByte,
                ProtocolNumber = protocol,
                InformationContent = info,
                SerialNumber = serial,
                Crc = expectedCrc
            };

            consumedBytes += totalFrameSize;
            return true;
        }

        // Tramas extendidas 0x79 0x79: la longitud del paquete ocupa 2 bytes (big-endian).
        // Se usan para contenidos grandes (IMSI 0x90, ICCID 0x94, archivos 0x8D).
        // Length = Protocol(1) + Information(N) + Serial(2) + ErrorCheck(2).
        // Tamaño total = Start(2) + Length(2) + Length + Stop(2) = 6 + Length.
        if (start1 == 0x79)
        {
            if (slice.Length < 6)
                return false; // falta la cabecera mínima

            int lengthField = BinaryPrimitives.ReadUInt16BigEndian(slice.Slice(2, 2));
            int totalFrameSize = 6 + lengthField;

            if (slice.Length < totalFrameSize)
                return false; // paquete incompleto, esperar más datos

            if (slice[totalFrameSize - 2] != 0x0D || slice[totalFrameSize - 1] != 0x0A)
            {
                consumedBytes += 2; // framing inválido, avanzar más allá de los start bits
                return false;
            }

            // CRC-ITU sobre: los 2 bytes de Packet Length hasta el Serial (inclusive) = lengthField bytes.
            var crcSpan = slice.Slice(2, lengthField);
            ushort expectedCrc = BinaryPrimitives.ReadUInt16BigEndian(slice.Slice(2 + lengthField, 2));
            ushort computedCrc = R12LFCrc16.Compute(crcSpan);

            if (computedCrc != expectedCrc)
            {
                consumedBytes += totalFrameSize; // trama corrupta, descartar
                return false;
            }

            byte protocol = slice[4];
            int infoLen = lengthField - 5;
            byte[] info = infoLen > 0 ? slice.Slice(5, infoLen).ToArray() : [];
            ushort serial = BinaryPrimitives.ReadUInt16BigEndian(slice.Slice(5 + infoLen, 2));

            frame = new R12LFFrame
            {
                StartBit1 = start1,
                StartBit2 = start2,
                // PacketLength es de 1 byte; para 0x79 (longitud de 2 bytes) no se usa para
                // dimensionar la trama (el llamador usa consumedBytes). Se guarda el byte bajo.
                PacketLength = (byte)(lengthField & 0xFF),
                ProtocolNumber = protocol,
                InformationContent = info,
                SerialNumber = serial,
                Crc = expectedCrc
            };

            consumedBytes += totalFrameSize;
            return true;
        }

        return false;
    }

    public static byte[] BuildAck(byte protocolNumber, ushort serialNumber)
    {
        // Response format: 0x78 0x78 0x05 <protocol> <serialHi> <serialLo> <crcHi> <crcLo> 0x0D 0x0A (10 bytes)
        Span<byte> toHash = stackalloc byte[4];
        toHash[0] = 0x05;
        toHash[1] = protocolNumber;
        BinaryPrimitives.WriteUInt16BigEndian(toHash[2..4], serialNumber);

        ushort crc = R12LFCrc16.Compute(toHash);

        byte[] ack = new byte[10];
        ack[0] = 0x78;
        ack[1] = 0x78;
        ack[2] = 0x05;
        ack[3] = protocolNumber;
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(4, 2), serialNumber);
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(6, 2), crc);
        ack[8] = 0x0D;
        ack[9] = 0x0A;

        return ack;
    }

    public string ExtractImeiFromLoginPacket()
    {
        if (ProtocolNumber != ProtocolLogin || InformationContent.Length < 8)
            return string.Empty;

        // Terminal ID is 8 bytes in packed BCD / hex.
        var sb = new StringBuilder(16);
        foreach (byte b in InformationContent.AsSpan(0, 8))
        {
            sb.Append(b.ToString("X2"));
        }
        string raw = sb.ToString();
        return raw.StartsWith('0') ? raw[1..] : raw;
    }

    public (GeoPosition? Position, DateTime RecordedAt) DecodeLocationPacket()
        => DecodeLocationPacket(ProtocolNumber);

    /// <summary>
    /// Decodifica una trama de posición/alarma interpretándola como
    /// <paramref name="canonicalProtocolNumber"/>, que puede diferir del número realmente
    /// recibido. Lo necesitan los dialectos que renumeran las tramas sin cambiar su
    /// contenido: el Mictrack M558G-V2 envía la posición como 0x22 y la alarma como 0x26,
    /// con el mismo cuerpo que el 0x12/0x16 de GT06. Sin esto el filtro de abajo devolvía
    /// null y se descartaban todas sus posiciones.
    /// </summary>
    public (GeoPosition? Position, DateTime RecordedAt) DecodeLocationPacket(byte canonicalProtocolNumber)
    {
        // Contrato histórico que conservan otros transportes (TcpBinaryTransport,
        // R12LFTcpGatewayService, TcpBinaryTelemetryStrategy): sin fix GPS se devuelve null.
        var (position, recordedAt, extras) = DecodeLocationPacketDetailed(canonicalProtocolNumber);
        return (extras.HasFix ? position : null, recordedAt);
    }

    /// <summary>
    /// Celda LBS (localización por antena) presente en las tramas 0x12/0x16 tras el bloque
    /// de curso/estado: MCC(2)/MNC(1)/LAC(2)/CellID(3). NO es una posición GPS; se expone
    /// como metadato para conservar la trama sin fix sin inventar lat/lon.
    /// </summary>
    public readonly record struct Gt06LbsCell(int Mcc, int Mnc, int Lac, long CellId);

    /// <summary>
    /// Metadatos adicionales de una trama de posición/alarma: si trae fix GPS, el tipo de
    /// alarma (sólo 0x16) y la celda LBS (si el contenido la incluye).
    /// </summary>
    public readonly record struct Gt06LocationExtras(bool HasFix, string? AlarmType, Gt06LbsCell? Lbs);

    /// <summary>
    /// Variante de <see cref="DecodeLocationPacket(byte)"/> que, además de la posición,
    /// devuelve metadatos: presencia de fix, tipo de alarma (0x16) y celda LBS. A diferencia
    /// del método base, SÍ construye la posición cuando no hay fix (marcada IsValid=false),
    /// para que el llamador pueda conservar la trama en vez de descartarla entera. No sustituye
    /// al método base porque otros transportes dependen de su contrato de devolver null sin fix.
    /// </summary>
    public (GeoPosition? Position, DateTime RecordedAt, Gt06LocationExtras Extras) DecodeLocationPacketDetailed(byte canonicalProtocolNumber)
    {
        if (canonicalProtocolNumber != ProtocolLocation && canonicalProtocolNumber != ProtocolAlarm)
            return (null, DateTime.UtcNow, default);

        if (InformationContent.Length < 18)
            return (null, DateTime.UtcNow, default);

        var span = InformationContent.AsSpan();

        // 1. Date Time: YY MM DD HH MM SS (6 bytes)
        int year = 2000 + span[0];
        int month = Math.Clamp((int)span[1], 1, 12);
        // Acotar al nº real de días del mes: un día inválido (p.ej. 31 en abril, 30 en
        // febrero) haría que new DateTime(...) lance y tumbe el decode, cerrando el socket.
        int day = Math.Clamp((int)span[2], 1, DateTime.DaysInMonth(year, month));
        int hour = Math.Clamp((int)span[3], 0, 23);
        int min = Math.Clamp((int)span[4], 0, 59);
        int sec = Math.Clamp((int)span[5], 0, 59);
        var recordedAt = new DateTime(year, month, day, hour, min, sec, DateTimeKind.Utc);

        // 2. GPS Info Length & Satellites (1 byte): High 4 bits = info length, Low 4 bits = satellites count
        byte satByte = span[6];
        short satellites = (short)(satByte & 0x0F);

        // 3. Latitude (4 bytes): Degrees * 1800000.0 (30000 * 60)
        uint rawLat = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(7, 4));
        double latitude = rawLat / 1800000.0;

        // 4. Longitude (4 bytes): Degrees * 1800000.0
        uint rawLon = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(11, 4));
        double longitude = rawLon / 1800000.0;

        // 5. Speed (1 byte): km/h
        float speed = span[15];

        // 6. Course & Status (2 bytes, big-endian). BYTE_1 es el byte alto.
        //    Según el spec R12L-F/GT06 (sección Course,Status):
        //      BYTE_1 Bit4 (0x1000): 1 = GPS posicionado
        //      BYTE_1 Bit3 (0x0800): 0 = Longitud Este, 1 = Longitud Oeste
        //      BYTE_1 Bit2 (0x0400): 1 = Latitud Norte, 0 = Latitud Sur
        //      Course = 10 bits bajos (0x03FF), 0-360° medidos desde el norte.
        ushort courseStatus = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(16, 2));
        float heading = courseStatus & 0x03FF;
        bool isGpsPositioned = (courseStatus & 0x1000) != 0;
        bool isNorth = (courseStatus & 0x0400) != 0; // 1 = Norte, 0 = Sur
        bool isWest = (courseStatus & 0x0800) != 0;  // 1 = Oeste, 0 = Este

        if (!isNorth) latitude = -latitude;   // hemisferio sur → latitud negativa
        if (isWest) longitude = -longitude;   // longitud oeste → longitud negativa

        // 7. Celda LBS (8 bytes) inmediatamente después del bloque de curso/estado.
        Gt06LbsCell? lbs = null;
        if (InformationContent.Length >= 18 + 8)
        {
            int mcc = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(18, 2));
            int mnc = span[20];
            int lac = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(21, 2));
            long cellId = ((long)span[23] << 16) | ((long)span[24] << 8) | span[25];
            lbs = new Gt06LbsCell(mcc, mnc, lac, cellId);
        }

        // 8. Tipo de alarma: sólo el 0x16 añade, tras la LBS, Terminal Info(1) + Voltage(1)
        //    + GSM(1) + Alarm/Language(2). El byte "former" de Alarm/Language (índice
        //    18+8+3 = 29) es el tipo de alarma.
        string? alarmType = null;
        if (canonicalProtocolNumber == ProtocolAlarm && InformationContent.Length >= 30)
            alarmType = MapAlarmType(span[29]);

        GeoPosition? position;
        if (isGpsPositioned)
        {
            // Caso con fix: idéntico al comportamiento previo (rumbo/velocidad sin sanear).
            var posResult = GeoPosition.Create(latitude, longitude, speed, heading, null, satellites, true);
            position = posResult.IsSuccess ? posResult.Value : null;
        }
        else
        {
            // #5: sin fix se CONSERVA la trama como posición marcada no válida (sin inventar
            // lat/lon: se pasan las coordenadas decodificadas, casi siempre 0,0). Se sanea
            // rumbo/velocidad porque sin fix suelen venir fuera de rango y GeoPosition.Create
            // las rechazaría, perdiendo también la celda LBS asociada al llamador.
            float? safeSpeed = speed >= 0f ? speed : null;
            float safeHeading = ((heading % 360f) + 360f) % 360f;
            var posResult = GeoPosition.Create(latitude, longitude, safeSpeed, safeHeading, null, satellites, false);
            position = posResult.IsSuccess ? posResult.Value : null;
        }

        return (position, recordedAt, new Gt06LocationExtras(isGpsPositioned, alarmType, lbs));
    }

    /// <summary>
    /// Traduce el byte "former" del campo Alarm/Language (tramas 0x13/0x16) al código de
    /// alarma canónico, según el spec GT06/R12L-F (apéndice de alarmas). 0x00 (normal) no es
    /// alarma; un valor desconocido se conserva como hex para no perder información.
    /// </summary>
    public static string? MapAlarmType(byte alarm) => alarm switch
    {
        0x00 => null,
        0x01 => "sos",
        0x02 => "power_cut",
        0x03 => "vibration",
        0x04 => "geofence_enter",
        0x05 => "geofence_exit",
        _ => $"0x{alarm:X2}"
    };
}
