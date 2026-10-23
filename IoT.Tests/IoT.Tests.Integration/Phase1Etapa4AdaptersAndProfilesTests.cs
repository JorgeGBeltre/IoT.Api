using System.Text.Json;
using FluentAssertions;
using IoT.Domain.Enums;
using IoT.Domain.ValueObjects;
using IoT.Protocols.Adapters;
using IoT.Protocols.Contracts;
using IoT.Protocols.Profiles;
using Xunit;

namespace IoT.Tests.Integration;

public class Phase1Etapa4AdaptersAndProfilesTests
{
    // =========================================================================
    // E4-01: Migrated Adapters with TransportMessage Contracts
    // =========================================================================

    [Fact]
    public void GlpPressureAdapter_DecodesTransportMessage_PreservesMessageIdAndMetadata()
    {
        var adapter = new GlpPressureAdapter();
        string json = JsonSerializer.Serialize(new
        {
            Device = new { ChipId = "GLP-TANK-99" },
            Timestamp = DateTime.UtcNow.ToString("o"),
            Details = new
            {
                sensors_filtered = new
                {
                    pressure_bar = 8.45,
                    level_mm = 850.0,
                    temp_celsius = 24.2
                },
                volume = new
                {
                    percentage = 78.5,
                    liters = 1200.0
                }
            }
        });

        string messageId = Guid.NewGuid().ToString("N");
        DateTime receivedAt = DateTime.UtcNow.AddSeconds(-1);

        var transportMsg = new TransportMessage
        {
            MessageId = messageId,
            Source = "mqtt-broker-1",
            TransportProtocol = "MQTT",
            TopicOrRoute = "glp/telemetria/GLP-TANK-99",
            Payload = json,
            ReceivedAt = receivedAt
        };

        adapter.CanHandle(transportMsg).Should().BeTrue();

        var result = adapter.Decode(transportMsg);
        result.IsSuccess.Should().BeTrue();

        var evt = result.Value;
        evt.DeviceIdentifier.Should().Be("GLP-TANK-99");
        evt.MessageId.Should().Be(messageId);
        evt.ReceivedAt.Should().Be(receivedAt);
        evt.NumericMetrics.Should().ContainKey("pressure_bar").WhoseValue.Should().BeApproximately(8.45, 0.01);
        evt.NumericMetrics.Should().ContainKey("fill_percentage").WhoseValue.Should().BeApproximately(78.5, 0.01);
        evt.NumericMetrics.Should().ContainKey("temp_celsius").WhoseValue.Should().BeApproximately(24.2, 0.01);
    }

    [Fact]
    public void GpsAccidentAdapter_DecodesTransportMessage_ExtractsGeoPositionAndAccidentMetrics()
    {
        var adapter = new GpsAccidentAdapter();
        string json = JsonSerializer.Serialize(new
        {
            Device = new { ChipId = "GPS-UNIT-101" },
            Timestamp = DateTime.UtcNow.ToString("o"),
            Details = new
            {
                accident = new
                {
                    impact_magnitude = 4.8,
                    roll_angle = 45.0,
                    gps_data = new
                    {
                        latitude = 18.4861,
                        longitude = -69.9312,
                        speed = 65.5f,
                        heading = 180.0f,
                        is_valid = true
                    }
                }
            }
        });

        string messageId = Guid.NewGuid().ToString("N");
        var transportMsg = new TransportMessage
        {
            MessageId = messageId,
            Source = "mqtt-broker-1",
            TransportProtocol = "MQTT",
            TopicOrRoute = "devices/GPS-UNIT-101/accident",
            Payload = json
        };

        adapter.CanHandle(transportMsg).Should().BeTrue();

        var result = adapter.Decode(transportMsg);
        result.IsSuccess.Should().BeTrue();

        var evt = result.Value;
        evt.DeviceIdentifier.Should().Be("GPS-UNIT-101");
        evt.MessageId.Should().Be(messageId);
        evt.Position.Should().NotBeNull();
        evt.Position!.Latitude.Should().BeApproximately(18.4861, 0.0001);
        evt.Position.Longitude.Should().BeApproximately(-69.9312, 0.0001);
        evt.Position.SpeedKph.Should().Be(65.5f);
        evt.NumericMetrics.Should().ContainKey("impact_magnitude").WhoseValue.Should().BeApproximately(4.8, 0.01);
        evt.NumericMetrics.Should().ContainKey("roll_angle_deg").WhoseValue.Should().BeApproximately(45.0, 0.01);
    }

    [Fact]
    public void RelaySwitchAdapter_DecodesTransportMessage_ExtractsRelayStates()
    {
        var adapter = new RelaySwitchAdapter();
        string json = JsonSerializer.Serialize(new
        {
            Device = new { ChipId = "RELAY-NODE-01" },
            Timestamp = DateTime.UtcNow.ToString("o"),
            Details = new
            {
                devices = new
                {
                    relay_1 = new { state = "ON", desired_state = "ON" },
                    relay_2 = new { state = "OFF", desired_state = "OFF" }
                },
                uptime = 1250.0
            }
        });

        var transportMsg = new TransportMessage
        {
            MessageId = "msg-relay-01",
            Source = "mqtt-broker-1",
            TransportProtocol = "MQTT",
            TopicOrRoute = "relays/RELAY-NODE-01/state",
            Payload = json
        };

        adapter.CanHandle(transportMsg).Should().BeTrue();

        var result = adapter.Decode(transportMsg);
        result.IsSuccess.Should().BeTrue();

        var evt = result.Value;
        evt.DeviceIdentifier.Should().Be("RELAY-NODE-01");
        evt.Attributes.Should().ContainKey("relay_1_state").WhoseValue.Should().Be("ON");
        evt.Attributes.Should().ContainKey("relay_2_state").WhoseValue.Should().Be("OFF");
        evt.NumericMetrics.Should().ContainKey("uptime_seconds").WhoseValue.Should().Be(1250.0);
    }

    // =========================================================================
    // E4-02: Declarative Profiles for Target Devices & Protocol Registry
    // =========================================================================

    [Fact]
    public void ProtocolRegistry_CreateDefault_RegistersAllSevenProfiles()
    {
        var registry = ProtocolRegistry.CreateDefault();
        var profiles = registry.GetAllProfiles();

        profiles.Should().HaveCount(33);

        registry.GetProfile("pressure-level-sensor-v1").Should().NotBeNull();
        registry.GetProfile("mqtt-gps-tracker").Should().NotBeNull();
        registry.GetProfile("relay-switch").Should().NotBeNull();
        registry.GetProfile("ticket-validator-v1").Should().NotBeNull();
        registry.GetProfile("mqttv5-json").Should().NotBeNull();
        registry.GetProfile("access-control-v1").Should().NotBeNull();
        registry.GetProfile("r12l-f-gps-tracker").Should().NotBeNull();

        // Hardware trackers from References/Protocolos
        registry.GetProfile("seeworld-r11").Should().NotBeNull();
        registry.GetProfile("queclink-gv55").Should().NotBeNull();
        registry.GetProfile("concox-gt06n").Should().NotBeNull();
        registry.GetProfile("coban-tk103").Should().NotBeNull();
        registry.GetProfile("istartek-vt100").Should().NotBeNull();
        registry.GetProfile("jointech-jt701d").Should().NotBeNull();
        registry.GetProfile("benway-bw08").Should().NotBeNull();
        registry.GetProfile("x3tech-nt20").Should().NotBeNull();
        registry.GetProfile("mictrack-m558g-v2").Should().NotBeNull();
    }

    [Theory]
    [InlineData("telemetry/VALIDATOR-VEHICLE-01", "ticket-validator-v1")]
    [InlineData("TicketValidationRequest", "ticket-validator-v1")]
    [InlineData("access/telemetry/ACCESS-TURNSTILE-01", "access-control-v1")]
    [InlineData("access/access/ACCESS-GATE-02", "access-control-v1")]
    [InlineData("mqttv5/telemetry", "mqttv5-json")]
    [InlineData("mqttv5/status", "mqttv5-json")]
    [InlineData("glp/telemetria/TANK-01", "pressure-level-sensor-v1")]
    [InlineData("relays/RELAY-01/state", "relay-switch")]
    [InlineData("tcp/r12lf/864200051234567", "r12l-f-gps-tracker")]
    public void ProtocolRegistry_FindProfileForTopic_ResolvesExpectedProfile(string topic, string expectedProfileId)
    {
        var registry = ProtocolRegistry.CreateDefault();
        var profile = registry.FindProfileForTopic(topic);

        profile.Should().NotBeNull($"Topic '{topic}' should match profile '{expectedProfileId}'");
        profile!.ProfileId.Should().Be(expectedProfileId);
    }

    [Fact]
    public void TicketValidatorAdapter_DecodesRealTelemetryAndTicketValidation()
    {
        var adapter = new TicketValidatorAdapter();
        string json = JsonSerializer.Serialize(new
        {
            Device = new
            {
                ChipId = "VALIDATOR-9921",
                FirmwareVersion = "v0.0.1-prod"
            },
            Timestamp = DateTime.UtcNow.ToString("o"),
            Details = new
            {
                CodeTicket = "TKT-2026-X891",
                rssi = -68.0,
                free_heap = 142380.0,
                gps_latitude = 18.4722,
                gps_longitude = -69.9145,
                gps_speed = 34.2
            }
        });

        var msg = new TransportMessage
        {
            MessageId = "validator-msg-01",
            Source = "mqtt",
            TransportProtocol = "MQTT",
            TopicOrRoute = "telemetry/VALIDATOR-9921",
            Payload = json
        };

        adapter.CanHandle(msg).Should().BeTrue();

        var result = adapter.Decode(msg);
        result.IsSuccess.Should().BeTrue();

        var evt = result.Value;
        evt.DeviceIdentifier.Should().Be("VALIDATOR-9921");
        evt.Position.Should().NotBeNull();
        evt.Position!.Latitude.Should().BeApproximately(18.4722, 0.0001);
        evt.Position.Longitude.Should().BeApproximately(-69.9145, 0.0001);
        evt.Attributes.Should().ContainKey("code_ticket").WhoseValue.Should().Be("TKT-2026-X891");
        evt.NumericMetrics.Should().ContainKey("rssi").WhoseValue.Should().Be(-68.0);
        evt.NumericMetrics.Should().ContainKey("free_heap_bytes").WhoseValue.Should().Be(142380.0);
    }

    [Fact]
    public void AccessControlAdapter_DecodesNfcCardAndQrEventsWithAccelerometer()
    {
        var adapter = new AccessControlAdapter();
        string json = JsonSerializer.Serialize(new
        {
            Device = new { ChipId = "ACCESS-GATE-NORTH" },
            Timestamp = DateTime.UtcNow.ToString("o"),
            Details = new
            {
                nfc_uid = "04:A2:3B:8C:91",
                qr_payload = "https://access.example.io/pass/8921",
                access_granted = true,
                accel_x = 0.02,
                accel_y = 0.98,
                accel_z = 0.12,
                battery_mv = 4150.0
            }
        });

        var msg = new TransportMessage
        {
            MessageId = "access-msg-01",
            Source = "mqtt",
            TransportProtocol = "MQTT",
            TopicOrRoute = "access/access/ACCESS-GATE-NORTH",
            Payload = json
        };

        adapter.CanHandle(msg).Should().BeTrue();

        var result = adapter.Decode(msg);
        result.IsSuccess.Should().BeTrue();

        var evt = result.Value;
        evt.DeviceIdentifier.Should().Be("ACCESS-GATE-NORTH");
        evt.Attributes.Should().ContainKey("nfc_uid").WhoseValue.Should().Be("04:A2:3B:8C:91");
        evt.Attributes.Should().ContainKey("qr_payload");
        evt.Attributes.Should().ContainKey("access_granted").WhoseValue.Should().Be("True");
        evt.NumericMetrics.Should().ContainKey("accel_y_g").WhoseValue.Should().BeApproximately(0.98, 0.01);
        evt.NumericMetrics.Should().ContainKey("battery_mv").WhoseValue.Should().Be(4150.0);
    }

    [Fact]
    public void MqttV5JsonAdapter_DecodesTelemetryAndStatusStrings()
    {
        var adapter = new MqttV5JsonAdapter();
        string json = JsonSerializer.Serialize(new
        {
            Device = new { ChipId = "ESP32-S3-010" },
            Timestamp = DateTime.UtcNow.ToString("o"),
            Details = new
            {
                cpu_temp = 42.5,
                wifi_rssi = -55.0,
                uptime_sec = 86400.0
            }
        });

        var msg = new TransportMessage
        {
            MessageId = "esp32-v5-01",
            Source = "mqtt",
            TransportProtocol = "MQTT",
            TopicOrRoute = "mqttv5/telemetry",
            Payload = json
        };

        adapter.CanHandle(msg).Should().BeTrue();

        var result = adapter.Decode(msg);
        result.IsSuccess.Should().BeTrue();

        var evt = result.Value;
        evt.DeviceIdentifier.Should().Be("ESP32-S3-010");
        evt.NumericMetrics.Should().ContainKey("cpu_temp").WhoseValue.Should().Be(42.5);
        evt.NumericMetrics.Should().ContainKey("wifi_rssi").WhoseValue.Should().Be(-55.0);

        // Test plain string status decoding (e.g. "online")
        var statusMsg = new TransportMessage
        {
            TopicOrRoute = "mqttv5/status",
            Payload = "online"
        };
        var statusResult = adapter.Decode(statusMsg);
        statusResult.IsSuccess.Should().BeTrue();
        statusResult.Value.Attributes.Should().ContainKey("status_text").WhoseValue.Should().Be("online");
    }

    // =========================================================================
    // E4-03: SEEWORLD R12L-F Protocol Adapter & Device Profile
    // =========================================================================

    [Fact]
    public void R12LFDeviceProfile_ExposesTcpBinaryTransportAndGpsCapabilities()
    {
        var profile = new R12LFDeviceProfile();

        profile.ProfileId.Should().Be("r12l-f-gps-tracker");
        profile.Manufacturer.Should().Be("SEEWORLD");
        profile.Model.Should().Be("R12L-F");
        profile.PrimaryTransport.Should().Be("TCP-R12LF");
        profile.SupportedCapabilities.Should().Contain(CapabilityType.Gps);
        profile.SupportedCapabilities.Should().Contain(CapabilityType.Health);
        profile.SupportsTransport("TCP-R12LF").Should().BeTrue();
        profile.SupportsTransport("TCP").Should().BeTrue();
        profile.SupportsTransport("MQTT").Should().BeTrue();
        profile.SupportsTransport("BLUETOOTH").Should().BeFalse();
    }

    [Fact]
    public void R12LFProtocolAdapter_DecodesGpsFromTcpBinaryIngestion()
    {
        var adapter = new R12LFProtocolAdapter();

        string json = JsonSerializer.Serialize(new
        {
            Device = new { ChipId = "864200051234567" },
            Timestamp = DateTime.UtcNow.ToString("o"),
            Details = new
            {
                gps_data = new
                {
                    latitude = 18.486058,
                    longitude = -69.931211,
                    speed = 72.4f,
                    heading = 90.0f,
                    is_valid = true
                }
            }
        });

        var transportMsg = new TransportMessage
        {
            MessageId = "tcp-r12lf-msg-01",
            Source = "r12l-f",
            TransportProtocol = "TCP-R12LF",
            TopicOrRoute = "devices/864200051234567/gps",
            Payload = json
        };

        adapter.CanHandle(transportMsg).Should().BeTrue();

        var result = adapter.Decode(transportMsg);
        result.IsSuccess.Should().BeTrue();

        var evt = result.Value;
        evt.DeviceIdentifier.Should().Be("864200051234567");
        evt.Position.Should().NotBeNull();
        evt.Position!.Latitude.Should().BeApproximately(18.486058, 0.0001);
        evt.Position.Longitude.Should().BeApproximately(-69.931211, 0.0001);
        evt.NumericMetrics.Should().ContainKey("speed_kph").WhoseValue.Should().BeApproximately(72.4, 0.1);
        evt.NumericMetrics.Should().ContainKey("heading_deg").WhoseValue.Should().Be(90.0);
        evt.Attributes.Should().ContainKey("transport").WhoseValue.Should().Be("TCP-R12LF");
    }
}
