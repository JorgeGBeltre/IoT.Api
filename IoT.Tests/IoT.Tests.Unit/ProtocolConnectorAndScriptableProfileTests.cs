using FluentAssertions;
using IoT.Domain.Common;
using IoT.Protocols;
using IoT.Protocols.Adapters;
using IoT.Protocols.Contracts;
using IoT.Protocols.Dynamic;
using IoT.Protocols.Profiles;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

public sealed class ProtocolConnectorAndScriptableProfileTests
{
    private const string SampleJintScript = @"
        function decode(payload, topic, metadata) {
            var data = JSON.parse(payload);
            return {
                deviceId: data.id || 'DEV-DEFAULT',
                telemetry: {
                    temperature: data.temp || 0,
                    humidity: data.hum || 0
                },
                attributes: {
                    firmware: data.fw || 'v1.0.0',
                    carrier: metadata.carrier || 'Unknown'
                },
                position: {
                    latitude: data.lat || 18.4861,
                    longitude: data.lon || -69.9312,
                    speed: data.spd || 0
                }
            };
        }
    ";

    [Fact]
    public void ProtocolConnector_ImplementsContractAndExposesConverters()
    {
        var uplink = new MqttV5JsonAdapter();
        var connector = new ProtocolConnector("MQTT-TEST", "MQTT", uplink);

        connector.ConnectorName.Should().Be("MQTT-TEST");
        connector.TransportType.Should().Be("MQTT");
        connector.UplinkConverter.Should().BeSameAs(uplink);
        connector.DownlinkConverter.Should().BeNull();
    }

    [Fact]
    public void ScriptableProtocolAdapter_DecodesViaJintSandbox()
    {
        var adapter = new ScriptableProtocolAdapter("custom-lora", SampleJintScript);
        adapter.ProtocolName.Should().Be("custom-lora");

        var msg = new TransportMessage
        {
            TopicOrRoute = "lora/app/uplink",
            Payload = "{\"id\":\"LORA-001\",\"temp\":24.5,\"hum\":65,\"lat\":18.48,\"lon\":-69.93,\"spd\":12.5,\"fw\":\"v2.1\"}",
            Headers = new Dictionary<string, string> { ["carrier"] = "Claro-IoT" }
        };

        var result = adapter.Decode(msg);

        result.IsSuccess.Should().BeTrue();
        var ev = result.Value;
        ev.DeviceIdentifier.Should().Be("LORA-001");
        ev.NumericMetrics.Should().ContainKey("temperature").WhoseValue.Should().Be(24.5);
        ev.NumericMetrics.Should().ContainKey("humidity").WhoseValue.Should().Be(65);
        ev.Attributes.Should().ContainKey("firmware").WhoseValue.Should().Be("v2.1");
        ev.Attributes.Should().ContainKey("carrier").WhoseValue.Should().Be("Claro-IoT");
        ev.Position.Should().NotBeNull();
        ev.Position!.Latitude.Should().BeApproximately(18.48, 0.01);
        ev.Position!.Longitude.Should().BeApproximately(-69.93, 0.01);
        ev.Position!.SpeedKph.Should().Be(12.5f);
    }

    [Fact]
    public void ScriptableDeviceProfile_CanBeRegisteredAndResolvedInRegistry()
    {
        var registry = new ProtocolRegistry();
        var profile = new ScriptableDeviceProfile(
            "env-sensor-jint",
            "Acme",
            "EnvSensor-01",
            "MQTT",
            ["sensors/{sensorId}/data"],
            "sensors/{sensorId}/cmd",
            SampleJintScript);

        registry.RegisterProfile(profile);

        var retrieved = registry.GetProfile("env-sensor-jint");
        retrieved.Should().NotBeNull();
        retrieved!.Manufacturer.Should().Be("Acme");
        retrieved.Model.Should().Be("EnvSensor-01");
        retrieved.DisplayName.Should().Contain("Dynamic Jint");

        var matched = registry.FindProfileForTopic("sensors/SN-999/data");
        matched.Should().NotBeNull();
        matched!.ProfileId.Should().Be("env-sensor-jint");

        var msg = TransportMessage.FromMqtt("sensors/SN-999/data", "{\"id\":\"SN-999\",\"temp\":22.0,\"hum\":50}");
        var decoded = matched.Adapter.Decode(msg);
        decoded.IsSuccess.Should().BeTrue();
        decoded.Value.DeviceIdentifier.Should().Be("SN-999");
        decoded.Value.NumericMetrics["temperature"].Should().Be(22.0);
    }

    [Fact]
    public void ProtocolRegistry_ManagesConnectors_AndFindsByHeader()
    {
        var registry = ProtocolRegistry.CreateDefault();
        var connectors = registry.GetAllConnectors();

        connectors.Should().NotBeEmpty();
        connectors.Should().Contain(c => c.ConnectorName == "MQTT-DEFAULT");
        connectors.Should().Contain(c => c.ConnectorName == "TCP-GT06");

        var gt06Conn = registry.GetConnector("TCP-GT06");
        gt06Conn.Should().NotBeNull();
        gt06Conn!.TransportType.Should().Be("TCP");

        // Búsqueda por header explícito 'connector'
        var msgWithConnector = new TransportMessage
        {
            TopicOrRoute = "devices/123/telemetry",
            Payload = "{}",
            Headers = new Dictionary<string, string> { ["connector"] = "TCP-GT06" }
        };
        var found = registry.FindConnector(msgWithConnector);
        found.Should().NotBeNull();
        found!.ConnectorName.Should().Be("TCP-GT06");
    }

    [Fact]
    public void CompositeProtocolAdapter_RoutesToConnectorWhenHeaderPresent()
    {
        var registry = ProtocolRegistry.CreateDefault();
        var customConnector = new ProtocolConnector(
            "CUSTOM-JINT-CONN",
            "HTTP",
            new ScriptableUplinkConverter("custom-jint", SampleJintScript));
        registry.RegisterConnector(customConnector);

        var composite = new CompositeProtocolAdapter(registry);

        var msg = new TransportMessage
        {
            TopicOrRoute = "api/v1/webhook/telemetry",
            Payload = "{\"id\":\"HTTP-SENS-42\",\"temp\":31.2}",
            Headers = new Dictionary<string, string> { ["connector"] = "CUSTOM-JINT-CONN" }
        };

        var result = composite.Decode(msg);

        result.IsSuccess.Should().BeTrue();
        result.Value.DeviceIdentifier.Should().Be("HTTP-SENS-42");
        result.Value.NumericMetrics["temperature"].Should().Be(31.2);
    }
}
