using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using Moq;
using Xunit;

namespace IoT.Tests.Unit;

public sealed class DeviceTimersAndWifiCommandsTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string ChipId = "B81B05BBBC2C";

    private static Device CreateOnlineSwitch(Guid tenantId)
    {
        var device = Device.Create(tenantId, ChipId, "2C:BC:BB:05:1B:B8", "led1", "Relay-Wifi", "Generic").Value;
        device.AssignToCustomer(Guid.NewGuid());
        device.RecordHeartbeat(DateTime.UtcNow);
        return device;
    }

    [Fact]
    public async Task UpsertAndGetTimers_ValidScheduleCountdownAndLoop_PersistsAndPublishesOverMqtt5()
    {
        var device = CreateOnlineSwitch(TenantA);
        string? storedJson = null;
        string? publishedTopic = null;
        string? publishedPayload = null;
        MqttPublishOptions? publishedOptions = null;

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var attrRepo = new Mock<IDeviceAttributeRepository>();
        attrRepo.Setup(r => r.UpsertAsync(
                TenantA,
                device.Id,
                AttributeScope.Shared,
                "timers_json",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, AttributeScope, string, string, CancellationToken>((_, _, _, _, val, _) => storedJson = val)
            .ReturnsAsync((Guid tid, Guid did, AttributeScope scope, string key, string val, CancellationToken _) =>
                DeviceAttribute.Create(tid, did, scope, key, val).Value);

        attrRepo.Setup(r => r.GetByDeviceAndScopeAsync(device.Id, AttributeScope.Shared, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => storedJson is null
                ? Array.Empty<DeviceAttribute>()
                : new[] { DeviceAttribute.Create(TenantA, device.Id, AttributeScope.Shared, "timers_json", storedJson).Value });

        var mqttPub = new Mock<IMqttPublisher>();
        mqttPub.Setup(p => p.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<MqttPublishOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, MqttPublishOptions, CancellationToken>((t, pl, opt, _) =>
            {
                publishedTopic = t;
                publishedPayload = pl;
                publishedOptions = opt;
            })
            .Returns(Task.CompletedTask);

        var clientMap = new Mock<IMqttClientMap>();
        clientMap.Setup(m => m.GetClientIdAsync(ChipId, It.IsAny<CancellationToken>())).ReturnsAsync(ChipId);

        var uow = new Mock<IUnitOfWork>();

        var upsertHandler = new UpsertDeviceTimersCommandHandler(
            deviceRepo.Object, attrRepo.Object, uow.Object, mqttPub.Object, clientMap.Object);

        long futureEpoch = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds();
        var timers = new[]
        {
            new DeviceTimerItemDto(
                Id: "sched_1",
                Enabled: true,
                Type: "repeat",
                Channel: 1,
                Hour: 11,
                Minute: 30,
                Weekdays: 0b0111110,
                Do: new DeviceTimerActionDto("on")),
            new DeviceTimerItemDto(
                Id: "cnt_1",
                Enabled: true,
                Type: "once",
                Channel: 0,
                At: futureEpoch,
                Do: new DeviceTimerActionDto("off")),
            new DeviceTimerItemDto(
                Id: "loop_1",
                Enabled: true,
                Type: "duration",
                Channel: 2,
                PeriodSec: 2700,
                OnDurationSec: 1800,
                StartDo: new DeviceTimerActionDto("on"),
                EndDo: new DeviceTimerActionDto("off"))
        };

        var res = await upsertHandler.Handle(
            new UpsertDeviceTimersCommand(TenantA, device.Id, timers),
            CancellationToken.None);

        res.IsSuccess.Should().BeTrue();
        res.Value.DeliveredViaMqtt.Should().BeTrue();
        res.Value.Timers.Should().HaveCount(3);
        publishedTopic.Should().Be($"devices/{ChipId}/timers/command");
        publishedOptions.Should().NotBeNull();
        publishedOptions!.Qos.Should().Be(1);
        publishedOptions.Retain.Should().BeFalse();
        publishedPayload.Should().Contain("\"action\":\"set_timers\"");
        publishedPayload.Should().Contain("\"sched_1\"");
        publishedPayload.Should().Contain("\"cnt_1\"");
        publishedPayload.Should().Contain("\"loop_1\"");

        var queryHandler = new GetDeviceTimersQueryHandler(deviceRepo.Object, attrRepo.Object);
        var loaded = await queryHandler.Handle(new GetDeviceTimersQuery(TenantA, device.Id), CancellationToken.None);
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Timers.Should().HaveCount(3);
        loaded.Value.Timers[0].Type.Should().Be("repeat");
        loaded.Value.Timers[1].Type.Should().Be("once");
        loaded.Value.Timers[2].Type.Should().Be("duration");
        loaded.Value.Timers[2].Channel.Should().Be(2);
    }

    [Fact]
    public async Task ChangeDeviceWifi_WhenCallerOnSameSubnetAndMatchingSsid_DispatchesMqtt5AndRedactsAudit()
    {
        var device = CreateOnlineSwitch(TenantA);
        string? publishedTopic = null;
        string? publishedPayload = null;
        MqttPublishOptions? publishedOptions = null;
        AuditLog? capturedAudit = null;

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var attrRepo = new Mock<IDeviceAttributeRepository>();
        attrRepo.Setup(r => r.ListByDeviceAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                DeviceAttribute.Create(TenantA, device.Id, AttributeScope.Client, "wifi_ssid", "Casa_2.4G").Value,
                DeviceAttribute.Create(TenantA, device.Id, AttributeScope.Client, "local_ip", "192.168.2.150").Value,
                DeviceAttribute.Create(TenantA, device.Id, AttributeScope.Server, "device_key", "dev-key-1234").Value
            });

        var mqttPub = new Mock<IMqttPublisher>();
        mqttPub.Setup(p => p.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<MqttPublishOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, MqttPublishOptions, CancellationToken>((t, pl, opt, _) =>
            {
                publishedTopic = t;
                publishedPayload = pl;
                publishedOptions = opt;
            })
            .Returns(Task.CompletedTask);

        var clientMap = new Mock<IMqttClientMap>();
        clientMap.Setup(m => m.GetClientIdAsync(ChipId, It.IsAny<CancellationToken>())).ReturnsAsync(ChipId);

        var auditRepo = new Mock<IAuditLogRepository>();
        auditRepo.Setup(a => a.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((log, _) => capturedAudit = log)
            .Returns(Task.CompletedTask);

        var handler = new ChangeDeviceWifiCommandHandler(
            deviceRepo.Object,
            attrRepo.Object,
            mqttPub.Object,
            new Mock<IUnitOfWork>().Object,
            clientMap.Object,
            peerEndpointRegistry: null,
            auditLogRepository: auditRepo.Object);

        const string secretPass = "SuperSecretWifi#2026";
        var result = await handler.Handle(
            new ChangeDeviceWifiCommand(
                TenantA,
                device.Id,
                TargetSsid: "NuevaRed_IoT",
                TargetPassword: secretPass,
                CallerIpAddress: "192.168.2.160",
                ClientWifiSsid: "Casa_2.4G",
                LanProof: null,
                ClientPlatform: "mobile"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        publishedTopic.Should().Be($"devices/{ChipId}/wifi/command");
        publishedOptions!.Qos.Should().Be(1);
        publishedOptions.Retain.Should().BeFalse();
        publishedOptions.MessageExpiryInterval.Should().Be(60u);
        publishedPayload.Should().Contain("\"ssid\":\"NuevaRed_IoT\"");
        publishedPayload.Should().Contain(secretPass);

        capturedAudit.Should().NotBeNull();
        capturedAudit!.Details.Should().Contain("NuevaRed_IoT");
        capturedAudit.Details.Should().NotContain(secretPass);
    }

    [Fact]
    public async Task ChangeDeviceWifi_WhenWebCallerProvidesValidLanProof_Succeeds()
    {
        var device = CreateOnlineSwitch(TenantA);
        const string devKey = "f9e8d7c6-b5a4-4321-9876-112233445566";
        const string devIp = "192.168.2.150";
        string validProof = ChangeDeviceWifiCommandHandler.ComputeLanProofHex(devKey, ChipId, devIp)!;

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var attrRepo = new Mock<IDeviceAttributeRepository>();
        attrRepo.Setup(r => r.ListByDeviceAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                DeviceAttribute.Create(TenantA, device.Id, AttributeScope.Client, "wifi_ssid", "Casa_2.4G").Value,
                DeviceAttribute.Create(TenantA, device.Id, AttributeScope.Client, "local_ip", devIp).Value,
                DeviceAttribute.Create(TenantA, device.Id, AttributeScope.Server, "device_key", devKey).Value
            });

        var clientMap = new Mock<IMqttClientMap>();
        clientMap.Setup(m => m.GetClientIdAsync(ChipId, It.IsAny<CancellationToken>())).ReturnsAsync(ChipId);

        var handler = new ChangeDeviceWifiCommandHandler(
            deviceRepo.Object,
            attrRepo.Object,
            new Mock<IMqttPublisher>().Object,
            new Mock<IUnitOfWork>().Object,
            clientMap.Object);

        var result = await handler.Handle(
            new ChangeDeviceWifiCommand(
                TenantA,
                device.Id,
                TargetSsid: "Oficina_2.4G",
                TargetPassword: "PasswordValido123",
                CallerIpAddress: "::ffff:192.168.2.168",
                ClientWifiSsid: null,
                LanProof: validProof,
                ClientPlatform: "web"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("190.166.45.10", "Casa_2.4G", null)]       // Datos móviles / IP pública WAN
    [InlineData("192.168.100.25", "Casa_2.4G", null)]      // Otra subred LAN distinta (192.168.100.x vs 192.168.2.x)
    [InlineData("192.168.2.160", "OtraRed_Vecino", null)]  // Misma máscara privada pero conectado a otro SSID
    [InlineData("192.168.2.168", "Casa_2.4G", "00000000000000000000000000000000")] // LanProof falsificado
    [InlineData("192.168.2.160", null, null)]              // Sin SSID verificado ni LanProof local
    public async Task ChangeDeviceWifi_WhenCallerNotOnSameWifiOrLan_RejectsAndNeverPublishes(
        string callerIp,
        string? callerSsid,
        string? lanProof)
    {
        var device = CreateOnlineSwitch(TenantA);

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var attrRepo = new Mock<IDeviceAttributeRepository>();
        attrRepo.Setup(r => r.ListByDeviceAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                DeviceAttribute.Create(TenantA, device.Id, AttributeScope.Client, "wifi_ssid", "Casa_2.4G").Value,
                DeviceAttribute.Create(TenantA, device.Id, AttributeScope.Client, "local_ip", "192.168.2.150").Value,
                DeviceAttribute.Create(TenantA, device.Id, AttributeScope.Server, "device_key", "dev-key-1234").Value
            });

        var mqttPub = new Mock<IMqttPublisher>();
        var clientMap = new Mock<IMqttClientMap>();
        clientMap.Setup(m => m.GetClientIdAsync(ChipId, It.IsAny<CancellationToken>())).ReturnsAsync(ChipId);

        var handler = new ChangeDeviceWifiCommandHandler(
            deviceRepo.Object,
            attrRepo.Object,
            mqttPub.Object,
            new Mock<IUnitOfWork>().Object,
            clientMap.Object);

        var result = await handler.Handle(
            new ChangeDeviceWifiCommand(
                TenantA,
                device.Id,
                TargetSsid: "NuevaRed",
                TargetPassword: "PasswordValido123",
                CallerIpAddress: callerIp,
                ClientWifiSsid: callerSsid,
                LanProof: lanProof,
                ClientPlatform: "mobile"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.WifiNotSameNetwork");
        mqttPub.Verify(
            p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MqttPublishOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ChangeDeviceWifi_CrossTenantAccess_ReturnsNotFound()
    {
        var device = CreateOnlineSwitch(TenantB);

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var handler = new ChangeDeviceWifiCommandHandler(
            deviceRepo.Object,
            new Mock<IDeviceAttributeRepository>().Object,
            new Mock<IMqttPublisher>().Object,
            new Mock<IUnitOfWork>().Object);

        var result = await handler.Handle(
            new ChangeDeviceWifiCommand(TenantA, device.Id, "NuevaRed", "Password123", "192.168.2.160", "Casa_2.4G"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.NotFound");
    }

    [Fact]
    public void CompositeProtocolAdapter_OnRelayStatusTopicWithFreeHeap_RoutesToRelaySwitchAdapterAndExtractsWifiAttributes()
    {
        const string liveStatusPayload = """
        {
          "Device": {
            "ChipId": "B81B05BBBC2C",
            "MacAddress": "2C:BC:BB:05:1B:B8",
            "FirmwareVersion": "v0.0.8"
          },
          "Timestamp": "2026-11-04T20:04:22Z",
          "Details": {
            "rssi": -49,
            "free_heap": 182400,
            "uptime": 420,
            "wifi_ssid": "WCD - IN",
            "wifi_bssid": "E0:D3:62:81:E9:31",
            "local_ip": "192.168.2.115",
            "startup": "on",
            "pulse": "off",
            "pulseWidth": 500,
            "sledOnline": "on",
            "devices_count": 1,
            "channel_count": 1,
            "switches": [
              { "outlet": 0, "switch": "off" }
            ]
          }
        }
        """;

        var composite = global::IoT.Protocols.CompositeProtocolAdapter.CreateDefault();
        var decodeResult = composite.Decode("status/B81B05BBBC2C", liveStatusPayload);

        decodeResult.IsSuccess.Should().BeTrue();
        var ev = decodeResult.Value;
        ev.DeviceIdentifier.Should().Be("B81B05BBBC2C");
        ev.Attributes.Should().ContainKey("wifi_ssid").WhoseValue.Should().Be("WCD - IN");
        ev.Attributes.Should().ContainKey("wifi_bssid").WhoseValue.Should().Be("E0:D3:62:81:E9:31");
        ev.Attributes.Should().ContainKey("local_ip").WhoseValue.Should().Be("192.168.2.115");
        ev.Attributes.Should().ContainKey("startup").WhoseValue.Should().Be("on");
        ev.Attributes.Should().ContainKey("pulse").WhoseValue.Should().Be("off");
        ev.Attributes.Should().ContainKey("sledOnline").WhoseValue.Should().Be("on");
        ev.Attributes.Should().ContainKey("firmware_version").WhoseValue.Should().Be("v0.0.8");

        var clientAttrs = global::IoT.Application.Ingestion.DeviceClientAttributeMapping.Extract(ev);
        clientAttrs.Should().ContainKey("wifi_ssid").WhoseValue.Should().Be("WCD - IN");
        clientAttrs.Should().ContainKey("local_ip").WhoseValue.Should().Be("192.168.2.115");
        clientAttrs.Should().ContainKey("startup").WhoseValue.Should().Be("on");
    }
}
