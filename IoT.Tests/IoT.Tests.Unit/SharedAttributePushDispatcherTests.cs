using System.Text.Json;
using FluentAssertions;
using IoT.Application.Commands.Commands;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Mqtt;
using IoT.Infrastructure.Services;
using IoT.Protocols.Contracts;
using IoT.Protocols.Profiles;
using IoT.Protocols.Routing;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Unit;

/// <summary>
/// Pruebas unitarias para el envío (push) de atributos compartidos al dispositivo (ADR-015, P1c).
/// </summary>
public sealed class SharedAttributePushDispatcherTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static Device NewDevice(Guid tenantId, string model = "MQTT-Relay-BLE", string chipId = "RELAY-01")
    {
        var device = Device.Create(tenantId, chipId, "00:11:22:33:44:55", chipId, model, "Custom", "1.0.0").Value;
        device.AssignToCustomer(Guid.NewGuid());
        return device;
    }

    // =========================================================================
    // 1. Integración con SetDeviceAttributesCommandHandler
    // =========================================================================

    [Fact]
    public async Task Set_WithSharedScope_DispatchesPushToDevice()
    {
        var dev = new Mock<IDeviceRepository>();
        var attr = new Mock<IDeviceAttributeRepository>();
        var tenant = new Mock<ITenantContext>();
        tenant.Setup(t => t.TenantId).Returns(Tenant);
        var uow = new Mock<IUnitOfWork>();
        var pushDispatcher = new Mock<ISharedAttributePushDispatcher>();

        var device = NewDevice(Tenant);
        dev.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var handler = new SetDeviceAttributesCommandHandler(dev.Object, attr.Object, tenant.Object, uow.Object, pushDispatcher.Object);
        var attributes = new Dictionary<string, string?>
        {
            ["upload_interval"] = "30",
            ["relay_state"] = "on"
        };
        var cmd = new SetDeviceAttributesCommand(device.Id, AttributeScope.Shared, attributes);

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        pushDispatcher.Verify(p => p.PushSharedAttributesAsync(
            device,
            It.Is<IReadOnlyDictionary<string, string?>>(d => d["upload_interval"] == "30"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Set_WithServerScope_DoesNotDispatchPushToDevice()
    {
        var dev = new Mock<IDeviceRepository>();
        var attr = new Mock<IDeviceAttributeRepository>();
        var tenant = new Mock<ITenantContext>();
        tenant.Setup(t => t.TenantId).Returns(Tenant);
        var uow = new Mock<IUnitOfWork>();
        var pushDispatcher = new Mock<ISharedAttributePushDispatcher>();

        var device = NewDevice(Tenant);
        dev.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var handler = new SetDeviceAttributesCommandHandler(dev.Object, attr.Object, tenant.Object, uow.Object, pushDispatcher.Object);
        var attributes = new Dictionary<string, string?> { ["note"] = "internal metadata" };
        var cmd = new SetDeviceAttributesCommand(device.Id, AttributeScope.Server, attributes);

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        pushDispatcher.Verify(p => p.PushSharedAttributesAsync(
            It.IsAny<Device>(), It.IsAny<IReadOnlyDictionary<string, string?>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Set_WhenPushDispatcherThrowsException_StillSucceedsAndPersistsDesiredState()
    {
        var dev = new Mock<IDeviceRepository>();
        var attr = new Mock<IDeviceAttributeRepository>();
        var tenant = new Mock<ITenantContext>();
        tenant.Setup(t => t.TenantId).Returns(Tenant);
        var uow = new Mock<IUnitOfWork>();
        var pushDispatcher = new Mock<ISharedAttributePushDispatcher>();

        var device = NewDevice(Tenant);
        dev.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        pushDispatcher.Setup(p => p.PushSharedAttributesAsync(
            It.IsAny<Device>(), It.IsAny<IReadOnlyDictionary<string, string?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Network timeout during push"));

        var handler = new SetDeviceAttributesCommandHandler(dev.Object, attr.Object, tenant.Object, uow.Object, pushDispatcher.Object);
        var attributes = new Dictionary<string, string?> { ["upload_interval"] = "60" };
        var cmd = new SetDeviceAttributesCommand(device.Id, AttributeScope.Shared, attributes);

        var result = await handler.Handle(cmd, CancellationToken.None);

        // Desired state persistido en BD sin fallar (ADR-015 D1)
        result.IsSuccess.Should().BeTrue();
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================================
    // 2. SharedAttributePushDispatcher: Canal MQTT
    // =========================================================================

    [Fact]
    public async Task Push_MqttDevice_PublishesToAttributeTopicWithQoS1()
    {
        var mqtt = new Mock<IMqttPublisher>();
        var routing = new TopicRoutingService(ProtocolRegistry.CreateDefault());
        var registry = ProtocolRegistry.CreateDefault();
        var logger = new Mock<ILogger<SharedAttributePushDispatcher>>();

        string? publishedTopic = null;
        string? publishedPayload = null;
        MqttPublishOptions? publishedOptions = null;

        mqtt.Setup(m => m.PublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MqttPublishOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, MqttPublishOptions, CancellationToken>((t, p, o, _) =>
            {
                publishedTopic = t;
                publishedPayload = p;
                publishedOptions = o;
            })
            .Returns(Task.CompletedTask);

        var dispatcher = new SharedAttributePushDispatcher(mqtt.Object, routing, registry, logger.Object);
        var device = NewDevice(Tenant, model: "MQTT-Relay-BLE", chipId: "ESP32-RELAY-1");

        var attributes = new Dictionary<string, string?>
        {
            ["relay_1"] = "on",
            ["timer_seconds"] = "120"
        };

        var result = await dispatcher.PushSharedAttributesAsync(device, attributes);

        result.Dispatched.Should().BeTrue();
        result.Transport.Should().Be("mqtt");
        publishedTopic.Should().Be("devices/ESP32-RELAY-1/attributes");
        publishedOptions.Should().NotBeNull();
        publishedOptions!.Qos.Should().Be(1);
        publishedOptions.ContentType.Should().Be("application/json");
        publishedOptions.UserProperties.Should().ContainKey("scope").WhoseValue.Should().Be("shared");

        var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(publishedPayload!);
        deserialized!["relay_1"].Should().Be("on");
        deserialized["timer_seconds"].Should().Be("120");
    }

    // =========================================================================
    // 3. SharedAttributePushDispatcher: Canal GT06 (TCP Binario)
    // =========================================================================

    [Fact]
    public async Task Push_Gt06Device_WhenConnected_TranslatesAndSendsCommands()
    {
        var mqtt = new Mock<IMqttPublisher>();
        var routing = new TopicRoutingService(ProtocolRegistry.CreateDefault());
        var registry = ProtocolRegistry.CreateDefault();
        var logger = new Mock<ILogger<SharedAttributePushDispatcher>>();
        var tcpGateway = new Mock<ITcpCommandGateway>();

        tcpGateway.Setup(g => g.IsConnected("860000000000001")).Returns(true);
        tcpGateway.Setup(g => g.SendCommandAsync(
            "860000000000001", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var dispatcher = new SharedAttributePushDispatcher(mqtt.Object, routing, registry, logger.Object, tcpGateway.Object);
        var device = NewDevice(Tenant, model: "R12L-F", chipId: "860000000000001");

        var attributes = new Dictionary<string, string?>
        {
            ["upload_interval"] = "60",
            ["relay_cut"] = "true"
        };

        var result = await dispatcher.PushSharedAttributesAsync(device, attributes);

        result.Dispatched.Should().BeTrue();
        result.Transport.Should().Be("tcp");
        tcpGateway.Verify(g => g.SendCommandAsync("860000000000001", "TIMER,60#", It.IsAny<CancellationToken>()), Times.Once);
        tcpGateway.Verify(g => g.SendCommandAsync("860000000000001", "DYD,000000#", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Push_Gt06Device_WhenDisconnected_ReturnsOfflineWithoutFailing()
    {
        var mqtt = new Mock<IMqttPublisher>();
        var routing = new TopicRoutingService(ProtocolRegistry.CreateDefault());
        var registry = ProtocolRegistry.CreateDefault();
        var logger = new Mock<ILogger<SharedAttributePushDispatcher>>();
        var tcpGateway = new Mock<ITcpCommandGateway>();

        tcpGateway.Setup(g => g.IsConnected("860000000000001")).Returns(false);

        var dispatcher = new SharedAttributePushDispatcher(mqtt.Object, routing, registry, logger.Object, tcpGateway.Object);
        var device = NewDevice(Tenant, model: "GT06N", chipId: "860000000000001");

        var attributes = new Dictionary<string, string?>
        {
            ["upload_interval"] = "60"
        };

        var result = await dispatcher.PushSharedAttributesAsync(device, attributes);

        // Dispositivo sin socket TCP: no falla, reporta estado offline
        result.Dispatched.Should().BeFalse();
        result.Error.Should().Be("DeviceOfflineTcp");
        tcpGateway.Verify(g => g.SendCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================================
    // 4. MqttTopicAcl: Reglas de Autorización de Atributos
    // =========================================================================

    [Fact]
    public void MqttTopicAcl_DeviceCanSubscribeToItsOwnAttributeTopic()
    {
        string topic = "devices/CHIP-001/attributes";
        var decision = MqttTopicAcl.CanSubscribe(topic, authenticatedChipId: "CHIP-001");
        decision.Should().Be(MqttTopicAcl.Decision.Allow);
    }

    [Fact]
    public void MqttTopicAcl_DeviceCannotSubscribeToAnotherDeviceAttributeTopic()
    {
        string topic = "devices/CHIP-002/attributes";
        var decision = MqttTopicAcl.CanSubscribe(topic, authenticatedChipId: "CHIP-001");
        decision.Should().Be(MqttTopicAcl.Decision.Deny);
    }

    [Fact]
    public void MqttTopicAcl_ExternalDeviceCannotPublishToAttributeDownlinkTopic()
    {
        string topic = "devices/CHIP-001/attributes";
        var decision = MqttTopicAcl.CanPublish(topic, isServerOrigin: false, authenticatedChipId: "CHIP-001");
        decision.Should().Be(MqttTopicAcl.Decision.Deny);
    }

    [Fact]
    public void MqttTopicAcl_ServerCanPublishToAttributeDownlinkTopic()
    {
        string topic = "devices/CHIP-001/attributes";
        var decision = MqttTopicAcl.CanPublish(topic, isServerOrigin: true, authenticatedChipId: null);
        decision.Should().Be(MqttTopicAcl.Decision.Allow);
    }

    // =========================================================================
    // 5. ITopicRoutingService: Generación de Tópico de Atributos
    // =========================================================================

    [Fact]
    public void TopicRoutingService_BuildAttributeTopic_ReturnsStandardPattern()
    {
        var routing = new TopicRoutingService(ProtocolRegistry.CreateDefault());
        string topic = routing.BuildAttributeTopic("MQTT-Relay-BLE", "DEV-42", Tenant);
        topic.Should().Be("devices/DEV-42/attributes");
    }
}
