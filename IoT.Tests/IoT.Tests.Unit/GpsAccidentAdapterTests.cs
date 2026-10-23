using System.Text.Json;
using FluentAssertions;
using IoT.Protocols.Adapters;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

/// <summary>
/// Regresión del bug crítico E5: el gateway TCP R12L-F publica telemetría con un JSON
/// PLANO (latitude/longitude en la raíz, 'timestamp' en minúscula) en el tópico
/// devices/{imei}/telemetry. GpsAccidentAdapter sólo leía Details.gps y 'Timestamp'
/// (mayúscula), por lo que la posición se perdía en silencio. Ahora debe decodificarla.
/// </summary>
public sealed class GpsAccidentAdapterTests
{
    [Fact]
    public void Decode_FlatGatewayPayload_ParsesRootLevelPositionAndTimestamp()
    {
        var adapter = new GpsAccidentAdapter();

        // Forma EXACTA del payload que arma R12LFTcpGatewayService para la ingesta.
        string json = JsonSerializer.Serialize(new
        {
            imei = "864200051234567",
            latitude = 18.4861,
            longitude = -69.9312,
            speed = 60.0,
            course = 180.0,
            satellites = 8,
            isValid = true,
            timestamp = "2026-10-22T14:30:00Z",
            is_alarm = false,
            protocol_number = "0x12",
            serial_number = 42
        });

        var result = adapter.Decode("devices/864200051234567/telemetry", json);

        result.IsSuccess.Should().BeTrue();
        result.Value.DeviceIdentifier.Should().Be("864200051234567");
        result.Value.Position.Should().NotBeNull("la posición plana del gateway TCP no debe perderse");
        result.Value.Position!.Latitude.Should().BeApproximately(18.4861, 0.0001);
        result.Value.Position!.Longitude.Should().BeApproximately(-69.9312, 0.0001);
        result.Value.Position!.SpeedKph.Should().BeApproximately(60.0f, 0.1f);
        result.Value.Position!.Heading.Should().BeApproximately(180.0f, 0.1f);
        result.Value.Position!.Satellites.Should().Be(8);
        result.Value.RecordedAt.Should().Be(new DateTime(2026, 10, 22, 14, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Decode_NestedAccidentPayload_StillWorks()
    {
        var adapter = new GpsAccidentAdapter();

        string json = JsonSerializer.Serialize(new
        {
            Device = new { ChipId = "GPS-ACC-01" },
            Timestamp = "2026-10-22T10:00:00Z",
            Details = new
            {
                accident = new
                {
                    impact_detected = true,
                    impact_magnitude = 7.5,
                    gps_data = new { latitude = 18.50, longitude = -69.90, speed = 0.0, heading = 90.0 }
                }
            }
        });

        var result = adapter.Decode("devices/GPS-ACC-01/accident", json);

        result.IsSuccess.Should().BeTrue();
        result.Value.Position.Should().NotBeNull();
        result.Value.Position!.Latitude.Should().BeApproximately(18.50, 0.0001);
        result.Value.Attributes.Should().ContainKey("impact_detected").WhoseValue.Should().Be("True");
    }
}
