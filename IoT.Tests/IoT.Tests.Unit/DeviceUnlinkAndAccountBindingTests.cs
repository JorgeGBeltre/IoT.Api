using System.Text.Json;
using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Application.Ingestion;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Redis;
using IoT.Infrastructure.Security;
using IoT.Protocols.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace IoT.Tests.Unit;

public sealed class DeviceUnlinkAndAccountBindingTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RootCustomerA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task UnlinkDevice_WhenOnline_PublishesAuthenticatedMqttUnlink_ClearsRetainedStatus_RecordsTombstone_AndBroadcastsSignalR()
    {
        var apiKeySvc = new DeviceApiKeyService();
        var device = Device.Create(TenantA, "B81B05BBBC2C", "2C:BC:BB:05:1B:B8", "led1", "Relay-Wifi", "Generic").Value;
        device.AssignToCustomer(RootCustomerA);
        device.AssignCapability(CapabilityType.Switch);
        var generatedKey = apiKeySvc.Generate();
        device.SetMqttApiKey(generatedKey.Hash, generatedKey.Fingerprint, "admin");
        device.RecordHeartbeat(DateTime.UtcNow);

        const string devKey = "a2a11068-7a28-411f-8c04-d9637c19b1e2";
        string expectedProof = UnlinkDeviceCommandHandler.ComputeUnlinkProofHex(devKey, "B81B05BBBC2C")!;

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var attrRepo = new Mock<IDeviceAttributeRepository>();
        attrRepo.Setup(r => r.ListByDeviceAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                DeviceAttribute.Create(TenantA, device.Id, AttributeScope.Server, "device_key", devKey).Value,
                DeviceAttribute.Create(TenantA, device.Id, AttributeScope.Client, "local_ip", "192.168.2.115").Value
            });

        var clientMap = new Mock<IMqttClientMap>();
        clientMap.Setup(m => m.GetClientIdAsync("B81B05BBBC2C", It.IsAny<CancellationToken>()))
            .ReturnsAsync("B81B05BBBC2C");

        string? publishedUnlinkTopic = null;
        string? publishedUnlinkPayload = null;
        MqttPublishOptions? publishedUnlinkOpts = null;
        bool clearedRetainedStatus = false;

        var mqttPublisher = new Mock<IMqttPublisher>();
        mqttPublisher.Setup(p => p.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<MqttPublishOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, MqttPublishOptions, CancellationToken>((t, pl, opts, _) =>
            {
                publishedUnlinkTopic = t;
                publishedUnlinkPayload = pl;
                publishedUnlinkOpts = opts;
            })
            .Returns(Task.CompletedTask);

        mqttPublisher.Setup(p => p.PublishAsync(
                "status/B81B05BBBC2C",
                string.Empty,
                0,
                true,
                It.IsAny<CancellationToken>()))
            .Callback(() => clearedRetainedStatus = true)
            .Returns(Task.CompletedTask);

        var revoker = new Mock<IMqttSessionRevoker>();
        var auditRepo = new Mock<IAuditLogRepository>();
        var uow = new Mock<IUnitOfWork>();
        var notifications = new Mock<IFleetNotificationService>();
        var registry = new RedisUnlinkedDeviceRegistry(NullLogger<RedisUnlinkedDeviceRegistry>.Instance);

        var handler = new UnlinkDeviceCommandHandler(
            deviceRepo.Object,
            uow.Object,
            auditRepo.Object,
            revoker.Object,
            attrRepo.Object,
            mqttPublisher.Object,
            clientMap.Object,
            peerEndpointRegistry: null,
            unlinkedDeviceRegistry: registry,
            notificationService: notifications.Object);

        // Cross-tenant attempt must fail without mutating or publishing
        var crossTenant = await handler.Handle(new UnlinkDeviceCommand(TenantB, device.Id), CancellationToken.None);
        crossTenant.IsFailure.Should().BeTrue();
        crossTenant.Error.Code.Should().Be("Device.NotFound");
        publishedUnlinkTopic.Should().BeNull();

        // Legitimate owner unlink
        var result = await handler.Handle(
            new UnlinkDeviceCommand(TenantA, device.Id, Guid.NewGuid(), "admin", "192.168.2.50", "IoT.App"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Unlinked.Should().BeTrue();
        result.Value.FactoryResetDispatchedViaMqtt.Should().BeTrue();
        result.Value.PendingOfflineUnlinkTombstoneRecorded.Should().BeTrue();
        result.Value.UnlinkProof.Should().Be(expectedProof);
        result.Value.LocalIp.Should().Be("192.168.2.115");

        publishedUnlinkTopic.Should().Be("devices/B81B05BBBC2C/unlink/command");
        publishedUnlinkOpts.Should().NotBeNull();
        publishedUnlinkOpts!.Qos.Should().Be(1);
        publishedUnlinkOpts.Retain.Should().BeFalse();
        publishedUnlinkOpts.MessageExpiryInterval.Should().Be(60);

        using var doc = JsonDocument.Parse(publishedUnlinkPayload!);
        doc.RootElement.GetProperty("action").GetString().Should().Be("unlink_account");
        doc.RootElement.GetProperty("unlink_proof").GetString().Should().Be(expectedProof);

        clearedRetainedStatus.Should().BeTrue();
        deviceRepo.Verify(r => r.Delete(device), Times.Once);
        revoker.Verify(r => r.RevokeSessionAsync(
            "B81B05BBBC2C",
            $"ACCOUNT_UNLINKED:{expectedProof}",
            It.IsAny<CancellationToken>()), Times.Once);
        notifications.Verify(n => n.NotifyDeviceStatusChangedAsync(
            TenantA,
            device.Id,
            "B81B05BBBC2C",
            "Unlinked",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);

        var tombstone = await registry.GetAsync("b81b05bbbc2c");
        tombstone.Should().NotBeNull();
        tombstone!.UnlinkProof.Should().Be(expectedProof);
        apiKeySvc.Verify(generatedKey.PlainKey, tombstone.MqttApiKeyHash!).Should().BeTrue();
    }

    [Fact]
    public async Task UnlinkDevice_WhenOffline_RecordsTombstone_AndReProvisioningClearsTombstone()
    {
        var apiKeySvc = new DeviceApiKeyService();
        var device = Device.Create(TenantA, "B81B05BBBC2C", "2C:BC:BB:05:1B:B8", "led1", "Relay-Wifi", "Generic").Value;
        device.AssignToCustomer(RootCustomerA);
        var generatedKey = apiKeySvc.Generate();
        device.SetMqttApiKey(generatedKey.Hash, generatedKey.Fingerprint, "admin");

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var clientMap = new Mock<IMqttClientMap>();
        clientMap.Setup(m => m.GetClientIdAsync("B81B05BBBC2C", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var mqttPublisher = new Mock<IMqttPublisher>();
        var uow = new Mock<IUnitOfWork>();
        var registry = new RedisUnlinkedDeviceRegistry(NullLogger<RedisUnlinkedDeviceRegistry>.Instance);

        var unlinkHandler = new UnlinkDeviceCommandHandler(
            deviceRepo.Object,
            uow.Object,
            mqttPublisher: mqttPublisher.Object,
            mqttClientMap: clientMap.Object,
            unlinkedDeviceRegistry: registry);

        var unlinkRes = await unlinkHandler.Handle(new UnlinkDeviceCommand(TenantA, device.Id), CancellationToken.None);
        unlinkRes.IsSuccess.Should().BeTrue();
        unlinkRes.Value.FactoryResetDispatchedViaMqtt.Should().BeFalse();
        unlinkRes.Value.PendingOfflineUnlinkTombstoneRecorded.Should().BeTrue();
        unlinkRes.Value.UnlinkProof.Should().HaveLength(32);

        (await registry.GetAsync("B81B05BBBC2C")).Should().NotBeNull();

        // Re-provisioning the unlinked device clears its tombstone
        var custRepo = new Mock<ICustomerRepository>();
        custRepo.Setup(c => c.GetRootCustomerIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync(RootCustomerA);
        var connInfo = new Mock<IMqttConnectionInfoProvider>();
        connInfo.SetupGet(c => c.PublicHost).Returns("192.168.2.163");
        connInfo.SetupGet(c => c.Port).Returns(1883);
        var tenantCtx = new Mock<ITenantContext>();
        tenantCtx.SetupGet(t => t.TenantId).Returns(TenantA);

        var provisionHandler = new ProvisionDeviceCommandHandler(
            deviceRepo.Object,
            custRepo.Object,
            apiKeySvc,
            connInfo.Object,
            tenantCtx.Object,
            uow.Object,
            unlinkedDeviceRegistry: registry);

        var provRes = await provisionHandler.Handle(
            new ProvisionDeviceCommand(
                TenantA,
                "B81B05BBBC2C",
                "2C:BC:BB:05:1B:B8",
                "led1",
                "Relay-Wifi",
                "Generic",
                new[] { CapabilityType.Switch },
                "admin"),
            CancellationToken.None);

        provRes.IsSuccess.Should().BeTrue();
        (await registry.GetAsync("B81B05BBBC2C")).Should().BeNull();
    }

    [Fact]
    public void RelaySwitchAdapter_ExtractsAccountBoundClientAttribute_AndDefaultHardwareKeyMatchesFirmwareFormula()
    {
        string? defaultKey = UnlinkDeviceCommandHandler.TryDeriveDefaultEsp32DeviceKey("B81B05BBBC2C");
        defaultKey.Should().Be("05BBBC2C-B81B-45BB-8000-E2415FE1E676");

        var adapter = new RelaySwitchAdapter();
        const string payload = """
        {
          "Device": { "status": "connected", "ChipId": "B81B05BBBC2C", "FirmwareVersion": "v0.0.10" },
          "Timestamp": "2026-11-05T00:00:00Z",
          "Details": {
            "chipId": "B81B05BBBC2C",
            "state_version": 5,
            "account_bound": true,
            "wifi_ssid": "WCD - IN",
            "local_ip": "192.168.2.115",
            "sledOnline": "on",
            "switches": [{ "outlet": 0, "switch": "off" }]
          }
        }
        """;

        var decodeRes = adapter.Decode("status/B81B05BBBC2C", payload);
        decodeRes.IsSuccess.Should().BeTrue();
        decodeRes.Value.Attributes.Should().ContainKey("account_bound").WhoseValue.Should().Be("true");

        var mapped = DeviceClientAttributeMapping.Extract(decodeRes.Value);
        mapped.Should().ContainKey("account_bound").WhoseValue.Should().Be("true");
    }
}
