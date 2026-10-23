using FluentAssertions;
using IoT.Protocols.Adapters;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

public sealed class HardwareTrackerAdaptersTests
{
    [Fact]
    public void Gt06ProtocolAdapter_DecodesHexLocationPacketSuccessfully()
    {
        var adapter = new Gt06ProtocolAdapter("SEEWORLD", "R12L-F");

        // Trama GT06 válida: cabecera 7878, longitud 0x17, protocolo 0x12 (Location).
        // Lat 0x01FBBC64/1800000 = 18.4861, Lon 0x0780B7C0/1800000 = 69.9312.
        // Course,Status = 0x1C00 → Bit4 (0x1000) posicionado, Bit3 (0x0800) Oeste, Bit2 (0x0400) Norte
        // ⇒ equipo en República Dominicana (18.4861 N, -69.9312 O). CRC-ITU recalculado = 0x2176.
        string hexPayload = "787817121A0A160E1E008801FBBC640780B7C03C1C00000121760D0A";

        var result = adapter.Decode("tcp/gt06/864200051234567", hexPayload);

        result.IsSuccess.Should().BeTrue();
        result.Value.DeviceIdentifier.Should().Be("864200051234567");
        result.Value.Position.Should().NotBeNull();
        result.Value.Position!.Latitude.Should().BeApproximately(18.4861, 0.01);
        result.Value.Position!.Longitude.Should().BeApproximately(-69.9312, 0.01);
        result.Value.Attributes["manufacturer"].Should().Be("SEEWORLD");
        result.Value.Attributes["model"].Should().Be("R12L-F");
    }

    [Fact]
    public void QueclinkTrackProtocolAdapter_DecodesRespGtfriSuccessfully()
    {
        var adapter = new QueclinkTrackProtocolAdapter("GV55");

        // Formato estándar Queclink: +RESP:GTFRI,ver,imei,name,report_id,report_type,seq,gps_acc,speed,azimuth,alt,lon,lat,utc_time...$
        string payload = "+RESP:GTFRI,010200,860123456789012,GV55,0,0,1,1,65.4,180,15.2,-69.9145,18.4722,20261022201530,370,01,1F40,2B01,00,12500,85$";

        var result = adapter.Decode("queclink/860123456789012/telemetry", payload);

        result.IsSuccess.Should().BeTrue();
        result.Value.DeviceIdentifier.Should().Be("860123456789012");
        result.Value.Position.Should().NotBeNull();
        result.Value.Position!.Latitude.Should().BeApproximately(18.4722, 0.0001);
        result.Value.Position!.Longitude.Should().BeApproximately(-69.9145, 0.0001);
        result.Value.Position!.SpeedKph.Should().BeApproximately(65.4f, 0.1f);
        result.Value.Position!.Heading.Should().BeApproximately(180f, 0.1f);
        result.Value.NumericMetrics["speed_kph"].Should().BeApproximately(65.4, 0.1);
        // En GTFRI el índice 20 es 'Hour Meter Count', no la batería: ya NO se mapea a battery_pct.
        result.Value.NumericMetrics.Should().NotContainKey("battery_pct");
        result.Value.NumericMetrics["odometer_km"].Should().Be(12500);
    }

    [Fact]
    public void CobanProtocolAdapter_DecodesImeiReportSuccessfully()
    {
        var adapter = new CobanProtocolAdapter("TK103");

        // Formato estándar Coban ASCII: imei:<IMEI>,keyword,date,phone,fix,time,status,lat,N,lon,W,speed,heading...;
        // 1828.3320,N -> 18 + 28.3320/60 = 18.4722
        // 06954.8700,W -> -(69 + 54.8700/60) = -69.9145
        string payload = "imei:353451044508750,001,2610222015,13554900601,F,201530.000,A,1828.3320,N,06954.8700,W,40.0,90.0,25.0,1,0,80%,0.0%,28;";

        var result = adapter.Decode("coban/353451044508750/telemetry", payload);

        result.IsSuccess.Should().BeTrue();
        result.Value.DeviceIdentifier.Should().Be("353451044508750");
        result.Value.Position.Should().NotBeNull();
        result.Value.Position!.Latitude.Should().BeApproximately(18.4722, 0.01);
        result.Value.Position!.Longitude.Should().BeApproximately(-69.9145, 0.01);
        result.Value.Position!.IsValid.Should().BeTrue();
        result.Value.NumericMetrics["ignition_acc"].Should().Be(1.0);
        result.Value.NumericMetrics["fuel_pct"].Should().Be(80.0);
        result.Value.NumericMetrics["temperature_c"].Should().Be(28.0);
    }

    [Fact]
    public void IStartekProtocolAdapter_DecodesAsciiPacketSuccessfully()
    {
        var adapter = new IStartekProtocolAdapter("VT100");

        // Formato estándar iStartek: &&<pack-info>,<ID>,000,<alm>,<data>,<MMddyy HH:mm:ss>,<A/V>,<lat>,<lon>,<sats>,<hdop>,<speed>,<course>,<alt>,<odo>...*AB\r\n
        string payload = "&&001147,021104023195429,000,00,00,102226 20:15:30,A,18.4722,-69.9145,12,1.1,55.0,270.0,32.0,14520,370|01|27B3|0EA7,28,0000000F,01,00,4100|12200,00,85%,26.0*AB\r\n";

        var result = adapter.Decode("istartek/021104023195429/telemetry", payload);

        result.IsSuccess.Should().BeTrue();
        result.Value.DeviceIdentifier.Should().Be("021104023195429");
        result.Value.Position.Should().NotBeNull();
        result.Value.Position!.Latitude.Should().BeApproximately(18.4722, 0.0001);
        result.Value.Position!.Longitude.Should().BeApproximately(-69.9145, 0.0001);
        result.Value.Position!.SpeedKph.Should().Be(55.0f);
        result.Value.Position!.Heading.Should().Be(270.0f);
        result.Value.NumericMetrics["ignition_acc"].Should().Be(1.0);
        result.Value.NumericMetrics["csq"].Should().Be(28.0);
        result.Value.NumericMetrics["odometer_m"].Should().Be(14520.0);
    }

    [Fact]
    public void JointechProtocolAdapter_DecodesAsciiPacketSuccessfully()
    {
        var adapter = new JointechProtocolAdapter("JT701D");

        // Formato estándar Jointech: (ID,P45,ddMMyy,HHmmss,lat,N,lon,W,A,spd,head,sats,valid,serial,lock,...)
        string payload = "(7500313602,P45,221026,201530,18.4722,N,69.9145,W,A,35.0,180.0,4,1,0000000000,1,0,3,5)";

        var result = adapter.Decode("jointech/7500313602/telemetry", payload);

        result.IsSuccess.Should().BeTrue();
        result.Value.DeviceIdentifier.Should().Be("7500313602");
        result.Value.Position.Should().NotBeNull();
        result.Value.Position!.Latitude.Should().BeApproximately(18.4722, 0.0001);
        result.Value.Position!.Longitude.Should().BeApproximately(-69.9145, 0.0001);
        result.Value.Position!.SpeedKph.Should().Be(35.0f);
        result.Value.Position!.Heading.Should().Be(180.0f);
        result.Value.NumericMetrics["lock_state"].Should().Be(1.0);
        result.Value.Attributes["lock_status"].Should().Be("Locked");
    }
}
