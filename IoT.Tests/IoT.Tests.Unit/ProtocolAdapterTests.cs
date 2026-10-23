using FluentAssertions;
using IoT.Protocols;
using IoT.Protocols.Adapters;
using Xunit;

namespace IoT.Tests.Unit;

public class ProtocolAdapterTests
{
    private readonly ICompositeProtocolAdapter _composite = CompositeProtocolAdapter.CreateDefault();

    [Fact]
    public void GpsAccidentAdapter_ShouldDecodeCoordinatesAndAccident()
    {
        // Sample payload from GPS Readme
        string topic = "telemetry";
        string payload = """
        {
          "Device": {
            "status": "connected",
            "ChipId": "GPS-ESP8266-99",
            "MacAddress": "AA:BB:CC:DD:EE:FF",
            "ChipType": "ESP8266",
            "FirmwareVersion": "v0.0.4"
          },
          "Timestamp": "2026-10-21T10:30:45.123Z",
          "Details": {
            "accident": {
              "impact_detected": true,
              "rollover_detected": false,
              "impact_magnitude": 2450.5,
              "roll_angle": 15.2,
              "pitch_angle": 5.8,
              "gps_data": {
                "latitude": 18.4861,
                "longitude": -69.9312,
                "speed": 55.4,
                "heading": 120.0,
                "is_valid": true
              }
            }
          }
        }
        """;

        // Act
        var result = _composite.Decode(topic, payload);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var ev = result.Value;
        ev.DeviceIdentifier.Should().Be("GPS-ESP8266-99");
        ev.Position.Should().NotBeNull();
        ev.Position!.Latitude.Should().Be(18.4861);
        ev.Position!.Longitude.Should().Be(-69.9312);
        ev.Position!.SpeedKph.Should().Be(55.4f);
        ev.Position!.Heading.Should().Be(120.0f);
        ev.NumericMetrics.Should().ContainKey("impact_magnitude");
        ev.NumericMetrics["impact_magnitude"].Should().Be(2450.5);
        ev.Attributes["impact_detected"].Should().Be("True");
    }

    [Fact]
    public void GlpPressureAdapter_ShouldDecodeEkfAndVolume()
    {
        // Sample payload from GLP Readme
        string topic = "glp/telemetria/GLP-001";
        string payload = """
        {
          "Device": {
            "DeviceId": "GLP-001",
            "ChipType": "ESP32-S3",
            "FirmwareVersion": "1.0.0"
          },
          "Timestamp": "2026-10-21T10:30:00.000Z",
          "Details": {
            "sensors_filtered": {
              "level_mm": 325.8,
              "pressure_bar": 6.21,
              "temp_celsius": 27.9,
              "kalman_uncertainty_mm": 0.07
            },
            "volume": {
              "liters": 145.3,
              "gallons": 38.4,
              "percentage": 72.1,
              "mass_kg": 81.8,
              "liters_15c": 143.1
            },
            "autonomy": {
              "consumption_l_day": 3.8,
              "estimated_days": 38.2
            }
          }
        }
        """;

        // Act
        var result = _composite.Decode(topic, payload);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var ev = result.Value;
        ev.DeviceIdentifier.Should().Be("GLP-001");
        ev.NumericMetrics["level_mm"].Should().Be(325.8);
        ev.NumericMetrics["pressure_bar"].Should().Be(6.21);
        ev.NumericMetrics["temp_celsius"].Should().Be(27.9);
        ev.NumericMetrics["volume_liters"].Should().Be(145.3);
        ev.NumericMetrics["fill_percentage"].Should().Be(72.1);
        ev.NumericMetrics["estimated_autonomy_days"].Should().Be(38.2);
    }

    [Fact]
    public void RelaySwitchAdapter_ShouldDecodeRelayState()
    {
        // Sample payload from Relay Readme
        string topic = "status";
        string payload = """
        {
          "Device": {
            "status": "connected",
            "ChipId": "0000A1B2C3D4",
            "ChipType": "ESP32"
          },
          "Timestamp": "2026-10-21T20:30:00.000Z",
          "Details": {
            "rssi": -58.0,
            "free_heap": 182400.0,
            "uptime": 145020.0,
            "devices": {
              "relay_1": {
                "state": "on",
                "desired_state": "on"
              }
            }
          }
        }
        """;

        // Act
        var result = _composite.Decode(topic, payload);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var ev = result.Value;
        ev.DeviceIdentifier.Should().Be("0000A1B2C3D4");
        ev.NumericMetrics["rssi_dbm"].Should().Be(-58.0);
        ev.NumericMetrics["relay_1_state_binary"].Should().Be(1.0);
        ev.Attributes["relay_1_state"].Should().Be("on");
    }
}
