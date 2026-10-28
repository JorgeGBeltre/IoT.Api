using FluentAssertions;
using IoT.Protocols.Contracts;
using IoT.Protocols.Dynamic;
using Xunit;

namespace IoT.Tests.Unit;

public class DynamicProtocolConverterTests
{
    [Fact]
    public void ScriptableUplinkConverter_DecodesJsonTelemetryAndPositionSuccessfully()
    {
        // Arrange
        const string script = @"
            function decode(payload, topic, metadata) {
                var data = JSON.parse(payload);
                return {
                    deviceId: data.device_id,
                    timestamp: '2026-10-27T20:00:00Z',
                    telemetry: {
                        temperature: data.temp,
                        battery_level: data.battery
                    },
                    attributes: {
                        firmware: data.fw,
                        sensor_type: metadata.sensor_type || 'default'
                    },
                    position: {
                        latitude: data.gps.lat,
                        longitude: data.gps.lon,
                        speed: 45.5,
                        heading: 180.0,
                        altitude: 120.0,
                        isValid: true
                    }
                };
            }
        ";

        var converter = new ScriptableUplinkConverter("CUSTOM-TEST-CONVERTER", script);

        var headers = new Dictionary<string, string>
        {
            { "sensor_type", "lorawan-multitech" }
        };

        const string jsonPayload = "{\"device_id\":\"DEV-9988\",\"temp\":24.5,\"battery\":98.0,\"fw\":\"v1.2.3\",\"gps\":{\"lat\":18.4861,\"lon\":-69.9312}}";
        var message = TransportMessage.Create("telemetry/lorawan", jsonPayload, headers: headers);

        // Act
        var result = converter.ConvertUplink(message);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var evt = result.Value;
        evt.DeviceIdentifier.Should().Be("DEV-9988");
        evt.RecordedAt.Should().Be(DateTime.Parse("2026-10-27T20:00:00Z").ToUniversalTime());
        evt.NumericMetrics["temperature"].Should().Be(24.5);
        evt.NumericMetrics["battery_level"].Should().Be(98.0);
        evt.Attributes["firmware"].Should().Be("v1.2.3");
        evt.Attributes["sensor_type"].Should().Be("lorawan-multitech");

        evt.Position.Should().NotBeNull();
        evt.Position!.Latitude.Should().Be(18.4861);
        evt.Position.Longitude.Should().Be(-69.9312);
        evt.Position.SpeedKph.Should().Be(45.5f);
        evt.Position.Heading.Should().Be(180.0f);
        evt.Position.AltitudeM.Should().Be(120.0f);
        evt.Position.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ScriptableUplinkConverter_ReturnsFailure_WhenScriptThrowsSyntaxOrRuntimeError()
    {
        // Arrange
        const string brokenScript = @"
            function decode(payload, topic, metadata) {
                throw new Error('Sensor CRC mismatch');
            }
        ";

        var converter = new ScriptableUplinkConverter("BROKEN-CONVERTER", brokenScript);
        var message = TransportMessage.Create("telemetry/test", "{}");

        // Act
        var result = converter.ConvertUplink(message);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ScriptExecutionException");
        result.Error.Message.Should().Contain("Sensor CRC mismatch");
    }

    [Fact]
    public void ScriptableUplinkConverter_ReturnsFailure_WhenDeviceIdIsMissing()
    {
        // Arrange
        const string script = @"
            function decode(payload, topic, metadata) {
                return {
                    telemetry: { v: 1 }
                };
            }
        ";

        var converter = new ScriptableUplinkConverter("NO-ID-CONVERTER", script);
        var message = TransportMessage.Create("telemetry/test", "{}");

        // Act
        var result = converter.ConvertUplink(message);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MissingDeviceId");
    }

    [Fact]
    public void ScriptableDownlinkConverter_EncodesCommandToJsonFrame()
    {
        // Arrange
        const string script = @"
            function encode(commandName, params, sequence) {
                return {
                    topic: 'devices/' + params.deviceId + '/commands',
                    payload: JSON.stringify({
                        cmd: commandName,
                        target: params.targetValue,
                        seq: sequence
                    })
                };
            }
        ";

        var converter = new ScriptableDownlinkConverter("DOWNLINK-JSON", script);
        var request = new DownlinkCommandRequest("DEV-1234", "SET_THRESHOLD", "{\"deviceId\":\"DEV-1234\",\"targetValue\":42}", Sequence: 7);

        // Act
        var result = converter.ConvertDownlink(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var frame = result.Value;
        frame.TargetTopicOrAddress.Should().Be("devices/DEV-1234/commands");
        frame.TextPayload.Should().Contain("\"cmd\":\"SET_THRESHOLD\"");
        frame.TextPayload.Should().Contain("\"target\":42");
        frame.TextPayload.Should().Contain("\"seq\":7");
        frame.IsBinary.Should().BeFalse();
    }

    [Fact]
    public void ScriptableDownlinkConverter_EncodesCommandToBase64BinaryFrame()
    {
        // Arrange (produces binary frame encoded as Base64 in JS)
        const string script = @"
            function encode(commandName, params, sequence) {
                // Return 4 bytes: 0xAA, 0xBB, 0x01, 0x02 -> Base64 'qrsBAg=='
                return {
                    topic: 'tcp://192.168.1.50:5023',
                    payload: 'qrsBAg==',
                    isBinary: true
                };
            }
        ";

        var converter = new ScriptableDownlinkConverter("DOWNLINK-BIN", script);
        var request = new DownlinkCommandRequest("DEV-BIN", "RAW_RESET");

        // Act
        var result = converter.ConvertDownlink(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var frame = result.Value;
        frame.IsBinary.Should().BeTrue();
        frame.RawBytes.Should().Equal(new byte[] { 0xAA, 0xBB, 0x01, 0x02 });
    }
}
