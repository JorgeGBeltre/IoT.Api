using FluentAssertions;
using IoT.Protocols.Binary;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

/// <summary>
/// Cobertura del framing binario GT06/R12L-F sobre TCP: extracción de tramas
/// consecutivas (sin solaparse), tramas extendidas 0x79 0x79 (longitud de 2 bytes)
/// y que una trama 0x79 no bloquee el flujo de tramas 0x78 posteriores.
/// </summary>
public sealed class R12LFFrameStreamingTests
{
    private static byte[] BuildFrame78(byte protocol, byte[] info, ushort serial)
    {
        byte length = (byte)(info.Length + 5); // protocol + info + serial(2) + crc(2)
        var body = new List<byte> { length, protocol };
        body.AddRange(info);
        body.Add((byte)(serial >> 8));
        body.Add((byte)(serial & 0xFF));
        ushort crc = R12LFCrc16.Compute(body.ToArray()); // Packet Length .. Serial
        var frame = new List<byte> { 0x78, 0x78 };
        frame.AddRange(body);
        frame.Add((byte)(crc >> 8));
        frame.Add((byte)(crc & 0xFF));
        frame.Add(0x0D);
        frame.Add(0x0A);
        return frame.ToArray();
    }

    private static byte[] BuildFrame79(byte protocol, byte[] info, ushort serial)
    {
        int length = info.Length + 5; // protocol + info + serial(2) + crc(2)
        var body = new List<byte> { (byte)(length >> 8), (byte)(length & 0xFF), protocol };
        body.AddRange(info);
        body.Add((byte)(serial >> 8));
        body.Add((byte)(serial & 0xFF));
        ushort crc = R12LFCrc16.Compute(body.ToArray()); // Packet Length (2) .. Serial
        var frame = new List<byte> { 0x79, 0x79 };
        frame.AddRange(body);
        frame.Add((byte)(crc >> 8));
        frame.Add((byte)(crc & 0xFF));
        frame.Add(0x0D);
        frame.Add(0x0A);
        return frame.ToArray();
    }

    [Fact]
    public void TryParse_TwoBackToBack78Frames_ConsumesOnlyTheFirst()
    {
        byte[] f1 = BuildFrame78(R12LFFrame.ProtocolStatus, new byte[] { 0x04, 0x05, 0x06, 0x00, 0x01 }, 1);
        byte[] f2 = BuildFrame78(R12LFFrame.ProtocolStatus, new byte[] { 0x04, 0x05, 0x06, 0x00, 0x01 }, 2);
        byte[] stream = f1.Concat(f2).ToArray();

        bool parsed = R12LFFrame.TryParse(stream, out var frame, out int consumed);

        parsed.Should().BeTrue();
        frame!.SerialNumber.Should().Be(1);
        consumed.Should().Be(f1.Length, "solo la primera trama debe consumirse; la segunda queda en el buffer");

        // La segunda trama se parsea desde el remanente.
        bool parsed2 = R12LFFrame.TryParse(stream.AsSpan(consumed), out var frame2, out int consumed2);
        parsed2.Should().BeTrue();
        frame2!.SerialNumber.Should().Be(2);
        consumed2.Should().Be(f2.Length);
    }

    [Fact]
    public void TryParse_Extended79Frame_ParsesTwoByteLength()
    {
        // 0x94 = ICCID information: carga de 20 bytes (longitud total > 1 byte se cubre igual).
        byte[] info = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        byte[] frame79 = BuildFrame79(0x94, info, 7);

        bool parsed = R12LFFrame.TryParse(frame79, out var frame, out int consumed);

        parsed.Should().BeTrue();
        frame!.ProtocolNumber.Should().Be(0x94);
        frame.SerialNumber.Should().Be(7);
        frame.InformationContent.Length.Should().Be(20);
        consumed.Should().Be(frame79.Length);
    }

    [Fact]
    public void TryParse_79FrameFollowedBy78Frame_DoesNotStall()
    {
        byte[] f79 = BuildFrame79(0x90, new byte[] { 0x10, 0x20, 0x30 }, 5); // IMSI info
        byte[] f78 = BuildFrame78(R12LFFrame.ProtocolLogin,
            new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0x01, 0x23, 0x45 }, 6);
        byte[] stream = f79.Concat(f78).ToArray();

        bool parsed = R12LFFrame.TryParse(stream, out var frame, out int consumed);
        parsed.Should().BeTrue();
        frame!.ProtocolNumber.Should().Be(0x90);
        consumed.Should().Be(f79.Length, "la trama 0x79 debe consumirse por completo para no bloquear el flujo");

        bool parsed2 = R12LFFrame.TryParse(stream.AsSpan(consumed), out var frame2, out _);
        parsed2.Should().BeTrue();
        frame2!.ProtocolNumber.Should().Be(R12LFFrame.ProtocolLogin);
    }
}
