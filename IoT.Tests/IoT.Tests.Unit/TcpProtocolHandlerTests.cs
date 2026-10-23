using System.Text;
using System.Text.Json;
using FluentAssertions;
using IoT.Protocols.Binary;
using IoT.Protocols.Tcp;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

/// <summary>
/// Framing, extracción de IMEI, ACK/handshake y hint de transporte de los handlers TCP
/// multi-protocolo (una familia por puerto). Lógica pura, sin sockets.
/// </summary>
public sealed class TcpProtocolHandlerTests
{
    private static List<byte> Ascii(string s) => new(Encoding.ASCII.GetBytes(s));

    private static List<byte> Hex(string hex) => new(Convert.FromHexString(hex.Replace(" ", string.Empty)));

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes);

    // =========================================================================
    // Dialecto Mictrack M558G-V2: posición 0x22 y alarma 0x26 (mismo cuerpo que
    // el 0x12/0x16 de GT06). Vectores tomados de los ejemplos hexadecimales del
    // propio spec (secciones 5.2.2 y 5.3.1.21) con el CRC-ITU RECALCULADO: los
    // CRC impresos en el documento se arrastraron del 0x12/0x16 y son inválidos,
    // así que R12LFFrame.TryParse los rechazaría. Si un CRC de aquí estuviera mal,
    // estos tests fallarían al no parsear la trama.
    // =========================================================================

    private const string MictrackLocation0x22 =
        "78 78 1F 22 0B 08 1D 11 2E 10 CF 02 7A C7 EB 0C 46 58 49 00 14 8F 01 CC 00 28 7D 00 1F B8 00 03 B4 A1 0D 0A";

    private const string MictrackAlarm0x26 =
        "78 78 25 26 0B 0B 0F 0E 24 1D CF 02 7A C8 87 0C 46 57 E6 00 14 02 09 01 CC 00 28 7D 00 1F 72 65 06 04 01 01 00 36 2A 05 0D 0A";

    // 0x12 auténtico de GT06 con el CRC válido del spec: el soporte del dialecto
    // no debe romperlo (NT20.txt:1939 / M558G.txt:1998).
    private const string Gt06Location0x12 =
        "78 78 1F 12 0B 08 1D 11 2E 10 CF 02 7A C7 EB 0C 46 58 49 00 14 8F 01 CC 00 28 7D 00 1F B8 00 03 80 81 0D 0A";

    [Fact]
    public void Mictrack_LocationFrame0x22_IsDecodedLikeGt06Location()
    {
        var handler = new MictrackTcpHandler();
        handler.Family.Should().Be("Mictrack");

        var buf = Hex(MictrackLocation0x22);
        bool ok = handler.TryReadFrame(buf, "861234567890123", out var outcome);

        ok.Should().BeTrue("la trama debe parsear: si el CRC del vector fuese incorrecto, TryParse la rechazaría");
        outcome!.Ingest.Should().BeTrue("antes se descartaba el 100% de las posiciones del M558G-V2");
        outcome.IngestTopic.Should().Be("devices/861234567890123/telemetry");
        outcome.Ack.Should().BeNull("la posición no se confirma");

        using var doc = JsonDocument.Parse(outcome.IngestPayload!);
        var root = doc.RootElement;
        root.GetProperty("latitude").GetDouble().Should().BeApproximately(23.1116683, 0.000001);
        root.GetProperty("longitude").GetDouble().Should().BeApproximately(114.4092850, 0.000001);
        root.GetProperty("speed").GetDouble().Should().Be(0);
        root.GetProperty("course").GetDouble().Should().Be(143);
        root.GetProperty("satellites").GetInt32().Should().Be(15);
        root.GetProperty("isValid").GetBoolean().Should().BeTrue();
        root.GetProperty("is_alarm").GetBoolean().Should().BeFalse();
        // El payload conserva el número ORIGINAL recibido, no el canónico traducido.
        root.GetProperty("protocol_number").GetString().Should().Be("0x22");
        DateTime.Parse(root.GetProperty("timestamp").GetString()!).ToUniversalTime()
            .Should().Be(new DateTime(2011, 8, 29, 17, 46, 16, DateTimeKind.Utc));
        buf.Should().BeEmpty();
    }

    [Fact]
    public void Mictrack_AlarmFrame0x26_IngestsAndAcksEchoingItsOwnProtocolNumber()
    {
        var handler = new MictrackTcpHandler();
        var buf = Hex(MictrackAlarm0x26);

        bool ok = handler.TryReadFrame(buf, "861234567890123", out var outcome);

        ok.Should().BeTrue();
        outcome!.Ingest.Should().BeTrue();
        // El ACK ECOA el número de protocolo recibido (regla del spec): con 0x16 el equipo
        // no correlacionaría la respuesta. CRC-ITU sobre [05 26 00 36].
        ToHex(outcome.Ack!).Should().Be("787805260036 13DE 0D0A".Replace(" ", string.Empty));

        using var doc = JsonDocument.Parse(outcome.IngestPayload!);
        var root = doc.RootElement;
        root.GetProperty("is_alarm").GetBoolean().Should().BeTrue();
        root.GetProperty("protocol_number").GetString().Should().Be("0x26");
        root.GetProperty("latitude").GetDouble().Should().BeApproximately(23.1117550, 0.000001);
        root.GetProperty("longitude").GetDouble().Should().BeApproximately(114.4092300, 0.000001);
        DateTime.Parse(root.GetProperty("timestamp").GetString()!).ToUniversalTime()
            .Should().Be(new DateTime(2011, 11, 15, 14, 36, 29, DateTimeKind.Utc));
    }

    [Fact]
    public void Mictrack_StillDecodesTheStandardGt06Location0x12()
    {
        // El apéndice B del propio spec muestra tráfico REAL del equipo con 0x12/0x16, no
        // con 0x22/0x26: el documento se contradice, así que el dialecto debe aceptar ambos.
        var handler = new MictrackTcpHandler();
        var buf = Hex(Gt06Location0x12);

        handler.TryReadFrame(buf, "861234567890123", out var outcome).Should().BeTrue();

        outcome!.Ingest.Should().BeTrue();
        using var doc = JsonDocument.Parse(outcome.IngestPayload!);
        doc.RootElement.GetProperty("protocol_number").GetString().Should().Be("0x12");
        doc.RootElement.GetProperty("latitude").GetDouble().Should().BeApproximately(23.1116683, 0.000001);
    }

    [Fact]
    public void Gt06Handler_DoesNotDecode0x22_SoAnNt20IsNeverMisread()
    {
        // El X3Tech NT20 define SU PROPIO 0x22 con otro layout (antepone Location Source
        // Type y Terminal ID). Si el handler GT06 tradujese 0x22 globalmente, un NT20 en
        // modo "new frame" se decodificaría con los offsets equivocados y publicaría
        // posiciones FALSAS, que es peor que descartar la trama.
        var handler = new Gt06TcpHandler();
        var buf = Hex(MictrackLocation0x22);

        handler.TryReadFrame(buf, "861234567890123", out var outcome).Should().BeTrue();

        outcome!.Ingest.Should().BeFalse("el 0x22 sólo se interpreta en el listener del dialecto que lo documenta");
        outcome.IngestPayload.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Gt06_LoginFrame_ExtractsImeiAndAcks()
    {
        // Login: 7878 | len | 01 | <8 bytes Terminal ID BCD> | serial(2) | crc(2) | 0D0A
        byte[] id = { 0x01, 0x23, 0x45, 0x67, 0x89, 0x01, 0x23, 0x45 }; // IMEI 123456789012345
        var body = new List<byte> { 0x0D, 0x01 };
        body.AddRange(id);
        body.Add(0x00); body.Add(0x01); // serial
        ushort crc = R12LFCrc16.Compute(body.ToArray());
        var frame = new List<byte> { 0x78, 0x78 };
        frame.AddRange(body);
        frame.Add((byte)(crc >> 8)); frame.Add((byte)(crc & 0xFF));
        frame.Add(0x0D); frame.Add(0x0A);

        var handler = new Gt06TcpHandler();
        bool ok = handler.TryReadFrame(frame, null, out var outcome);

        ok.Should().BeTrue();
        outcome!.SessionImei.Should().Be("123456789012345");
        outcome.IdentifiesSession.Should().BeTrue();
        outcome.Ack.Should().NotBeNull();
        outcome.Ingest.Should().BeFalse();
    }

    [Fact]
    public void Queclink_ResolvesImeiFromField2_AndSetsHint()
    {
        var handler = new QueclinkTcpHandler();
        var buf = Ascii("+RESP:GTFRI,010200,860123456789012,GV55,0,0,1,1,65.4,180,15.2,-69.9145,18.4722,20261022201530,370,01,1F40,2B01,00,12500$");

        bool ok = handler.TryReadFrame(buf, null, out var outcome);

        ok.Should().BeTrue();
        outcome!.SessionImei.Should().Be("860123456789012");
        outcome.IdentifiesSession.Should().BeTrue();
        outcome.TransportProtocol.Should().Be("TCP-QUECLINK");
        outcome.Ingest.Should().BeTrue();
        buf.Should().BeEmpty("la trama completa debe consumirse");
    }

    [Fact]
    public void Coban_Handshake_RepliesLoad_NoIngest()
    {
        var handler = new CobanTcpHandler();
        var buf = Ascii("##,imei:353451044508750,A;");

        bool ok = handler.TryReadFrame(buf, null, out var outcome);

        ok.Should().BeTrue();
        outcome!.SessionImei.Should().Be("353451044508750");
        Encoding.ASCII.GetString(outcome.Ack!).Should().Be("LOAD");
        outcome.Ingest.Should().BeFalse();
    }

    [Fact]
    public void Coban_DataFrame_IngestsWithHint()
    {
        var handler = new CobanTcpHandler();
        var buf = Ascii("imei:353451044508750,001,2610222015,13554900601,F,201530.000,A,1828.3320,N,06954.8700,W,40.0,90.0,25.0,1,0,80%,0.0%,28;");

        bool ok = handler.TryReadFrame(buf, null, out var outcome);

        ok.Should().BeTrue();
        outcome!.SessionImei.Should().Be("353451044508750");
        outcome.TransportProtocol.Should().Be("TCP-COBAN");
        outcome.Ingest.Should().BeTrue();
    }

    // =========================================================================
    // Heartbeat Coban: IMEI DESNUDO sin terminador, el servidor responde 'ON'.
    // Spec: References/Protocolos/Coban protocolo GPRS.pdf, "2. Heartbeat-packets"
    // ("Heartbeat-packet content is the IMEI number" / "Server reply ON").
    // =========================================================================

    [Fact]
    public void Coban_Heartbeat_IsFramedAndRepliesOn()
    {
        var handler = new CobanTcpHandler();
        var buf = Ascii("359586015829802");

        bool ok = handler.TryReadFrame(buf, "359586015829802", out var outcome);

        ok.Should().BeTrue("el latido no lleva ';' y antes no se enmarcaba nunca");
        outcome!.SessionImei.Should().Be("359586015829802");
        Encoding.ASCII.GetString(outcome.Ack!).Should().Be("ON");
        outcome.Ingest.Should().BeFalse();
        buf.Should().BeEmpty();
    }

    [Fact]
    public void Coban_HeartbeatFollowedByDataFrame_DoesNotSwallowTheTelemetry()
    {
        // ESTE es el bug: el latido se quedaba en el buffer y se pegaba delante de la
        // trama siguiente ("359586...imei:3534..."), que ya no se podía identificar ni
        // decodificar. Se perdía una trama de telemetría por cada latido.
        var handler = new CobanTcpHandler();
        var buf = Ascii("359586015829802imei:353451044508750,001,2610222015,13554900601,F,201530.000,A,1828.3320,N,06954.8700,W,40.0,90.0,25.0,1,0,80%,0.0%,28;");

        handler.TryReadFrame(buf, null, out var heartbeat).Should().BeTrue();
        heartbeat!.SessionImei.Should().Be("359586015829802");
        Encoding.ASCII.GetString(heartbeat.Ack!).Should().Be("ON");

        handler.TryReadFrame(buf, "359586015829802", out var data).Should().BeTrue();
        data!.SessionImei.Should().Be("353451044508750");
        data.Ingest.Should().BeTrue();
        data.TransportProtocol.Should().Be("TCP-COBAN");
        data.IngestPayload.Should().StartWith("imei:353451044508750", "la trama de datos debe quedar limpia del latido");
        buf.Should().BeEmpty();
    }

    [Fact]
    public void Coban_Heartbeat_WithTrailingNewline_ConsumesIt()
    {
        var handler = new CobanTcpHandler();
        var buf = Ascii("359586015829802\r\n");

        handler.TryReadFrame(buf, null, out var outcome).Should().BeTrue();
        outcome!.SessionImei.Should().Be("359586015829802");
        buf.Should().BeEmpty();
    }

    [Fact]
    public void Coban_PartialHeartbeat_WaitsForTheRestInsteadOfSplittingTheImei()
    {
        // Un IMEI puede llegar partido en dos segmentos TCP. Cortarlo generaría un IMEI
        // falso y dejaría el resto contaminando la trama siguiente.
        var handler = new CobanTcpHandler();
        var buf = Ascii("3595860");

        handler.TryReadFrame(buf, null, out var outcome).Should().BeFalse();
        outcome.Should().BeNull();
        buf.Should().HaveCount(7, "los bytes se conservan hasta que llegue el resto");

        foreach (var b in Encoding.ASCII.GetBytes("15829802")) buf.Add(b);

        handler.TryReadFrame(buf, null, out var complete).Should().BeTrue();
        complete!.SessionImei.Should().Be("359586015829802");
    }

    [Fact]
    public void Coban_DataFrameStartingWithImeiPrefix_IsNotMistakenForAHeartbeat()
    {
        var handler = new CobanTcpHandler();
        var buf = Ascii("imei:353451044508750,001,A;");

        handler.TryReadFrame(buf, null, out var outcome).Should().BeTrue();
        outcome!.Ingest.Should().BeTrue("empieza por 'imei:', es una trama de datos, no un latido");
        outcome.Ack.Should().BeNull();
    }

    [Fact]
    public void IStartek_ResolvesImeiFromField1()
    {
        var handler = new IStartekTcpHandler();
        var buf = Ascii("&&001147,021104023195429,000,00,00,102226 20:15:30,A,18.4722,-69.9145,12,1.1,55.0,270.0,32.0,14520\r\n");

        bool ok = handler.TryReadFrame(buf, null, out var outcome);

        ok.Should().BeTrue();
        outcome!.SessionImei.Should().Be("021104023195429");
        outcome.TransportProtocol.Should().Be("TCP-ISTARTEK");
        outcome.Ingest.Should().BeTrue();
    }

    [Fact]
    public void Jointech_P45_RepliesP69WithSerial()
    {
        var handler = new JointechTcpHandler();
        var buf = Ascii("(7500313602,P45,221026,201530,18.4722,N,69.9145,W,A,35.0,180.0,4,1,0000000000,1,0,24,5)");

        bool ok = handler.TryReadFrame(buf, null, out var outcome);

        ok.Should().BeTrue();
        outcome!.SessionImei.Should().Be("7500313602");
        outcome.TransportProtocol.Should().Be("TCP-JOINTECH");
        Encoding.ASCII.GetString(outcome.Ack!).Should().Be("(P69,0,24)");
    }

    [Fact]
    public void Ascii_PartialFrame_ReturnsFalseUntilComplete()
    {
        var handler = new QueclinkTcpHandler();
        var buf = Ascii("+RESP:GTFRI,010200,860123456789012,GV55"); // sin '$'

        handler.TryReadFrame(buf, null, out _).Should().BeFalse();

        buf.AddRange(Ascii(",0,0$"));
        handler.TryReadFrame(buf, null, out var outcome).Should().BeTrue();
        outcome!.SessionImei.Should().Be("860123456789012");
    }

    [Fact]
    public void Ascii_TwoFramesInBuffer_ExtractedOneAtATime()
    {
        var handler = new CobanTcpHandler();
        var buf = Ascii("imei:111,001,A;imei:222,001,A;");

        handler.TryReadFrame(buf, null, out var first).Should().BeTrue();
        first!.SessionImei.Should().Be("111");
        handler.TryReadFrame(buf, "111", out var second).Should().BeTrue();
        second!.SessionImei.Should().Be("222");
        buf.Should().BeEmpty();
    }
}
