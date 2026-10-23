using System.Text;
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
