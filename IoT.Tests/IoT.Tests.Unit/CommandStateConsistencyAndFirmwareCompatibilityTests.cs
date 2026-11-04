using System.Text.Json;
using FluentAssertions;
using IoT.Application.Commands.Commands;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Mqtt;
using IoT.Infrastructure.Redis;
using IoT.Infrastructure.Services;
using IoT.Protocols;
using IoT.Protocols.Profiles;
using IoT.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace IoT.Tests.Unit;

public sealed class CommandStateConsistencyAndFirmwareCompatibilityTests
{
    [Fact]
    public async Task SendCommandToDevice_RelaySwitch_EmitsStructuredDesiredStateWithMonotonicSequenceAndShortExpiry()
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "B81B05BBBC2C", "2C:BC:BB:05:1B:B8", "led1", "Relay-Wifi", "Generic").Value;
        device.RecordHeartbeat(DateTime.UtcNow);

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var cmdRepo = new Mock<IDeviceCommandRepository>();
        var uow = new Mock<IUnitOfWork>();
        var mqtt = new Mock<IMqttPublisher>();
        var seqGen = new Mock<ICommandSequenceGenerator>();
        long seqCounter = 100;
        seqGen.Setup(s => s.NextSequenceAsync(device.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(() => Interlocked.Increment(ref seqCounter));

        var publishedPayloads = new List<string>();
        var publishedOptions = new List<MqttPublishOptions>();
        mqtt.Setup(m => m.PublishAsync(
                "commands/B81B05BBBC2C",
                It.IsAny<string>(),
                It.IsAny<MqttPublishOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, MqttPublishOptions, CancellationToken>((_, payload, opts, _) =>
            {
                publishedPayloads.Add(payload);
                publishedOptions.Add(opts);
            })
            .Returns(Task.CompletedTask);

        var routing = new TopicRoutingService(ProtocolRegistry.CreateDefault());
        var handler = new SendCommandToDeviceCommandHandler(
            deviceRepo.Object,
            cmdRepo.Object,
            mqtt.Object,
            uow.Object,
            new Mock<ILogger<SendCommandToDeviceCommandHandler>>().Object,
            routing,
            sequenceGenerator: seqGen.Object);

        var offRes = await handler.Handle(new SendCommandToDeviceCommand(
            tenantId, device.Id, "SET_POWER", """{"channel":1,"state":"OFF"}""", "mobile", "req-off-1", AllowOfflineQueueing: false),
            CancellationToken.None);

        var onRes = await handler.Handle(new SendCommandToDeviceCommand(
            tenantId, device.Id, "SET_POWER", """{"channel":1,"state":"ON"}""", "mobile", "req-on-2", AllowOfflineQueueing: false),
            CancellationToken.None);

        offRes.IsSuccess.Should().BeTrue();
        onRes.IsSuccess.Should().BeTrue();
        publishedPayloads.Should().HaveCount(2);

        using var doc1 = JsonDocument.Parse(publishedPayloads[0]);
        doc1.RootElement.GetProperty("commandId").GetString().Should().Be(offRes.Value.ToString());
        doc1.RootElement.GetProperty("desiredState").GetBoolean().Should().BeFalse();
        doc1.RootElement.GetProperty("command").GetString().Should().Be("switch1:off");
        doc1.RootElement.GetProperty("sequence").GetInt64().Should().Be(101);

        using var doc2 = JsonDocument.Parse(publishedPayloads[1]);
        doc2.RootElement.GetProperty("desiredState").GetBoolean().Should().BeTrue();
        doc2.RootElement.GetProperty("sequence").GetInt64().Should().Be(102);

        publishedOptions.Should().AllSatisfy(o =>
        {
            o.Retain.Should().BeFalse();
            o.MessageExpiryInterval.Should().Be(15u);
        });
    }

    [Fact]
    public async Task IngestTelemetry_CommandAck_CorrelatesAppliedAndRejectsStaleWithoutOverwritingReportedState()
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "B81B05BBBC2C", "2C:BC:BB:05:1B:B8", "led1", "Relay-Wifi", "Generic").Value;
        var appliedCmd = DeviceCommand.Create(tenantId, device.Id, "SET_POWER", """{"state":"OFF"}""", "mobile", "cid-applied").Value;
        appliedCmd.MarkAsSent();
        var staleCmd = DeviceCommand.Create(tenantId, device.Id, "SET_POWER", """{"state":"ON"}""", "mobile", "cid-stale").Value;
        staleCmd.MarkAsSent();

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByChipIdGlobalAsync("B81B05BBBC2C", It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var cmdRepo = new Mock<IDeviceCommandRepository>();
        cmdRepo.Setup(r => r.GetByIdAsync(appliedCmd.Id, It.IsAny<CancellationToken>())).ReturnsAsync(appliedCmd);
        cmdRepo.Setup(r => r.GetByIdAsync(staleCmd.Id, It.IsAny<CancellationToken>())).ReturnsAsync(staleCmd);

        var stateCache = new Mock<IStateCacheService>();
        var telemetryStore = new Mock<ITelemetryStore>();
        var storedMetrics = new List<Dictionary<string, double>>();
        telemetryStore
            .Setup(s => s.StoreTelemetryMetricsAsync(
                tenantId, device.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<Dictionary<string, double>>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, DateTime, DateTime, Dictionary<string, double>, string?, string, string?, CancellationToken>((_, _, _, _, m, _, _, _, _) =>
                storedMetrics.Add(new Dictionary<string, double>(m)))
            .Returns(Task.CompletedTask);

        var notifications = new Mock<IFleetNotificationService>();
        var handler = new IngestTelemetryCommandHandler(
            CompositeProtocolAdapter.CreateDefault(),
            deviceRepo.Object,
            new Mock<ITenantRepository>().Object,
            stateCache.Object,
            telemetryStore.Object,
            new Mock<IUnitOfWork>().Object,
            new Mock<ILogger<IngestTelemetryCommandHandler>>().Object,
            notificationService: notifications.Object,
            commandRepository: cmdRepo.Object);

        // 1. APPLIED ACK confirms command and updates reported state to OFF (0.0)
        string appliedAckJson = JsonSerializer.Serialize(new
        {
            commandId = appliedCmd.Id.ToString(),
            deviceId = "B81B05BBBC2C",
            channel = 1,
            reportedState = false,
            state = "off",
            sequence = 200,
            stateVersion = 15,
            source = "REMOTE_COMMAND",
            execUs = 42,
            duplicate = false,
            status = "APPLIED",
            switches = new[] { new { outlet = 0, @switch = "off" } }
        });

        var res1 = await handler.Handle(new IngestTelemetryCommand(
            "devices/B81B05BBBC2C/commands/ack",
            appliedAckJson,
            TransportDeviceIdentifier: "B81B05BBBC2C"), CancellationToken.None);

        res1.IsSuccess.Should().BeTrue();
        appliedCmd.Status.Should().Be(global::IoT.Domain.Enums.CommandStatus.Confirmed);
        appliedCmd.ConfirmedAt.Should().NotBeNull();
        storedMetrics.Should().ContainSingle();
        storedMetrics[0]["switch_1"].Should().Be(0.0);
        storedMetrics[0]["state_version"].Should().Be(15.0);

        // 2. Duplicate APPLIED ACK is idempotent and preserves original ConfirmedAt
        var firstConfirmedAt = appliedCmd.ConfirmedAt;
        string dupAckJson = JsonSerializer.Serialize(new
        {
            commandId = appliedCmd.Id.ToString(),
            deviceId = "B81B05BBBC2C",
            channel = 1,
            reportedState = false,
            sequence = 200,
            stateVersion = 15,
            duplicate = true,
            status = "APPLIED"
        });
        await handler.Handle(new IngestTelemetryCommand(
            "devices/B81B05BBBC2C/commands/ack",
            dupAckJson,
            TransportDeviceIdentifier: "B81B05BBBC2C"), CancellationToken.None);
        appliedCmd.ConfirmedAt.Should().Be(firstConfirmedAt);

        // 3. STALE_REJECTED ACK fails the stale command and NEVER writes switch_1=1.0
        string staleAckJson = JsonSerializer.Serialize(new
        {
            commandId = staleCmd.Id.ToString(),
            deviceId = "B81B05BBBC2C",
            channel = 1,
            reportedState = false,
            sequence = 199,
            stateVersion = 15,
            status = "STALE_REJECTED",
            error = "Out-of-order stale command sequence rejected",
            switches = new[] { new { outlet = 0, @switch = "on" } }
        });

        var res3 = await handler.Handle(new IngestTelemetryCommand(
            "devices/B81B05BBBC2C/commands/ack",
            staleAckJson,
            TransportDeviceIdentifier: "B81B05BBBC2C"), CancellationToken.None);

        res3.IsSuccess.Should().BeTrue();
        staleCmd.Status.Should().Be(global::IoT.Domain.Enums.CommandStatus.Failed);
        storedMetrics.Last().ContainsKey("switch_1").Should().BeFalse();

        notifications.Verify(n => n.NotifyCommandStatusUpdatedAsync(
            tenantId, device.Id, appliedCmd.Id, "Confirmed", It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        notifications.Verify(n => n.NotifyCommandStatusUpdatedAsync(
            tenantId, device.Id, staleCmd.Id, "Failed", It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IngestTelemetry_StaleStateVersion_StripsSwitchMetricsAndPreventsStaleOverwrite()
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "B81B05BBBC2C", "2C:BC:BB:05:1B:B8", "led1", "Relay-Wifi", "Generic").Value;

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByChipIdGlobalAsync("B81B05BBBC2C", It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var stateCache = new Mock<IStateCacheService>();
        stateCache.Setup(c => c.IsStaleDeviceStateVersionAsync(tenantId, device.Id, 9L, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(true);

        var notifications = new Mock<IFleetNotificationService>();
        var handler = new IngestTelemetryCommandHandler(
            CompositeProtocolAdapter.CreateDefault(),
            deviceRepo.Object,
            new Mock<ITenantRepository>().Object,
            stateCache.Object,
            new Mock<ITelemetryStore>().Object,
            new Mock<IUnitOfWork>().Object,
            new Mock<ILogger<IngestTelemetryCommandHandler>>().Object,
            notificationService: notifications.Object);

        string stalePayload = """
        {
          "Device": { "ChipId": "B81B05BBBC2C", "FirmwareVersion": "v0.0.9" },
          "Details": {
            "state_version": 9,
            "source": "PHYSICAL_BUTTON",
            "switches": [{ "outlet": 0, "switch": "on" }]
          }
        }
        """;

        var res = await handler.Handle(new IngestTelemetryCommand(
            "status/B81B05BBBC2C",
            stalePayload,
            TransportDeviceIdentifier: "B81B05BBBC2C"), CancellationToken.None);

        res.IsSuccess.Should().BeTrue();
        notifications.Verify(n => n.NotifyTelemetryReceivedAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.Is<IReadOnlyDictionary<string, double>>(m => m.ContainsKey("switch_1")),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteScheduleOrTimer_NeverPublishesRelayControlCommand_AndBroadcastsTimersUpdated()
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "B81B05BBBC2C", "2C:BC:BB:05:1B:B8", "led1", "Relay-Wifi", "Generic").Value;
        device.RecordHeartbeat(DateTime.UtcNow);

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var attrRepo = new Mock<IDeviceAttributeRepository>();
        attrRepo.Setup(a => a.UpsertAsync(tenantId, device.Id, AttributeScope.Shared, "timers_json", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid t, Guid d, AttributeScope s, string k, string v, CancellationToken _) =>
                    DeviceAttribute.Create(t, d, s, k, v).Value);

        var mqtt = new Mock<IMqttPublisher>();
        var clientMap = new Mock<IMqttClientMap>();
        clientMap.Setup(m => m.GetClientIdAsync("B81B05BBBC2C", It.IsAny<CancellationToken>())).ReturnsAsync("B81B05BBBC2C");

        var notifications = new Mock<IFleetNotificationService>();

        var handler = new UpsertDeviceTimersCommandHandler(
            deviceRepo.Object,
            attrRepo.Object,
            new Mock<IUnitOfWork>().Object,
            mqtt.Object,
            clientMap.Object,
            notificationService: notifications.Object);

        // Delete all schedules/timers (empty list after timer/schedule executed OFF)
        var res = await handler.Handle(new UpsertDeviceTimersCommand(
            tenantId, device.Id, Array.Empty<DeviceTimerItemDto>(), null, "admin"), CancellationToken.None);

        res.IsSuccess.Should().BeTrue();
        mqtt.Verify(m => m.PublishAsync(
            "devices/B81B05BBBC2C/timers/command",
            It.Is<string>(p => p.Contains("\"timers\":[]") && !p.Contains("switch1:")),
            It.Is<MqttPublishOptions>(o => o.Retain == false && o.Qos == 1),
            It.IsAny<CancellationToken>()), Times.Once);

        // Zero control commands on commands/{chipId} or any relay channel topic
        mqtt.Verify(m => m.PublishAsync(
            It.Is<string>(t => t.StartsWith("commands/") || t.EndsWith("/state")),
            It.IsAny<string>(),
            It.IsAny<MqttPublishOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);

        notifications.Verify(n => n.NotifyDeviceAlertAsync(
            tenantId, device.Id, "B81B05BBBC2C", "TIMERS_UPDATED", "[]", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void EmbeddedMqttBroker_IsNonRetainableCommandTopic_BlocksCommandsWhilePreservingStatusAvailability()
    {
        EmbeddedMqttBroker.IsNonRetainableCommandTopic("commands/B81B05BBBC2C").Should().BeTrue();
        EmbeddedMqttBroker.IsNonRetainableCommandTopic("devices/B81B05BBBC2C/timers/command").Should().BeTrue();
        EmbeddedMqttBroker.IsNonRetainableCommandTopic("devices/B81B05BBBC2C/wifi/command").Should().BeTrue();
        EmbeddedMqttBroker.IsNonRetainableCommandTopic("devices/B81B05BBBC2C/commands/ack").Should().BeTrue();

        EmbeddedMqttBroker.IsNonRetainableCommandTopic("status/B81B05BBBC2C").Should().BeFalse();
        EmbeddedMqttBroker.IsNonRetainableCommandTopic("glp/status/TANK-1").Should().BeFalse();
    }

    [Fact]
    public async Task RedisFleetNotificationPublisher_RoundTripsTelemetryAndCommandAckEnvelopes()
    {
        var sub = new Mock<ISubscriber>();
        string? capturedJson = null;
        sub.Setup(s => s.PublishAsync(
                RedisChannel.Literal(RedisFleetNotificationPublisher.ChannelName),
                It.IsAny<RedisValue>(),
                CommandFlags.FireAndForget))
           .Callback<RedisChannel, RedisValue, CommandFlags>((_, val, _) => capturedJson = val.ToString())
           .ReturnsAsync(1L);

        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(m => m.GetSubscriber(It.IsAny<object?>())).Returns(sub.Object);

        var publisher = new RedisFleetNotificationPublisher(
            mux.Object,
            new Mock<ILogger<RedisFleetNotificationPublisher>>().Object);

        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        await publisher.NotifyTelemetryReceivedAsync(
            tenantId,
            deviceId,
            "B81B05BBBC2C",
            new Dictionary<string, double> { ["switch_1"] = 1.0, ["state_version"] = 22.0 },
            DateTime.UtcNow);

        capturedJson.Should().NotBeNullOrWhiteSpace();
        var env = RedisFleetNotificationPublisher.DeserializeEnvelope(capturedJson!);
        env.Should().NotBeNull();
        env!.Kind.Should().Be("TelemetryReceived");
        env.TenantId.Should().Be(tenantId);
        env.DeviceId.Should().Be(deviceId);
        env.Metrics!["switch_1"].Should().Be(1.0);
        env.Metrics["state_version"].Should().Be(22.0);
    }

    [Fact]
    public async Task GetAvailableDeviceFirmware_EnforcesModelHardwarePolicyAndSemVerWithoutExposingUrlsOrHashes()
    {
        var tenantId = Guid.NewGuid();
        var relayDevice = Device.Create(tenantId, "B81B05BBBC2C", "2C:BC:BB:05:1B:B8", "led1", "Relay-Wifi", "Generic").Value;

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(relayDevice.Id, It.IsAny<CancellationToken>())).ReturnsAsync(relayDevice);

        var attrRepo = new Mock<IDeviceAttributeRepository>();
        attrRepo.Setup(a => a.GetByDeviceAndScopeAsync(relayDevice.Id, AttributeScope.Client, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[]
                {
                    DeviceAttribute.Create(tenantId, relayDevice.Id, AttributeScope.Client, "firmware_version", "v0.0.9").Value,
                    DeviceAttribute.Create(tenantId, relayDevice.Id, AttributeScope.Client, "chip_type", "ESP32-D0WD-V3").Value
                });

        string sha = new string('a', 64);
        FirmwareStorage.TryBuildRelativeFilePath(tenantId, "v0.1.0", sha, ".bin", out string relPath, out string absPath).Should().BeTrue();
        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
        await File.WriteAllBytesAsync(absPath, new byte[] { 0xE9, 1, 2, 3 });

        try
        {
            var gpsRecord = FirmwareVersionRecord.Create(tenantId, "v2.0.0", relPath, 4, sha, 1, 4096, "GPS-ESP32", "GPS update").Value;
            var s3Record = FirmwareVersionRecord.Create(tenantId, "v0.2.0", relPath, 4, sha, 1, 4096, "Relay-Wifi@ESP32-S3", "Wrong MCU").Value;
            var sameVerRecord = FirmwareVersionRecord.Create(tenantId, "v0.0.9", relPath, 4, sha, 1, 4096, "Relay-Wifi", "Current").Value;

            var fwRepo = new Mock<IFirmwareVersionRecordRepository>();
            fwRepo.Setup(r => r.GetRecordsAsync(tenantId, null, 0, 200, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<FirmwareVersionRecord> { gpsRecord, s3Record, sameVerRecord });

            var svc = new FirmwareOtaService(
                fwRepo.Object,
                deviceRepo.Object,
                new Mock<IUnitOfWork>().Object,
                new Mock<IMqttPublisher>().Object,
                new Mock<IFleetNotificationService>().Object,
                new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                new Mock<ILogger<FirmwareOtaService>>().Object,
                attributeRepository: attrRepo.Object);

            // 1. Incompatible models/hardware and equal version -> UpdateAvailable == false
            var noUpdate = await svc.GetAvailableDeviceFirmwareAsync(tenantId, relayDevice.Id, hasUpdatePolicy: true);
            noUpdate.IsSuccess.Should().BeTrue();
            noUpdate.Value.UpdateAvailable.Should().BeFalse();
            noUpdate.Value.CurrentVersion.Should().Be("v0.0.9");
            noUpdate.Value.AvailableVersion.Should().BeNull();

            // 2. Add compatible v0.1.0 -> UpdateAvailable == true when policy allowed, false when policy denied
            var compatibleRecord = FirmwareVersionRecord.Create(tenantId, "v0.1.0", relPath, 4, sha, 1, 4096, "Relay-Wifi@ESP32", "Mejoras de latencia y estabilidad.").Value;
            fwRepo.Setup(r => r.GetRecordsAsync(tenantId, null, 0, 200, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<FirmwareVersionRecord> { gpsRecord, s3Record, sameVerRecord, compatibleRecord });

            var deniedByPolicy = await svc.GetAvailableDeviceFirmwareAsync(tenantId, relayDevice.Id, hasUpdatePolicy: false);
            deniedByPolicy.Value.UpdateAvailable.Should().BeFalse();

            var available = await svc.GetAvailableDeviceFirmwareAsync(tenantId, relayDevice.Id, hasUpdatePolicy: true);
            available.IsSuccess.Should().BeTrue();
            available.Value.UpdateAvailable.Should().BeTrue();
            available.Value.AvailableVersion.Should().Be("v0.1.0");
            available.Value.ReleaseNotes.Should().Be("Mejoras de latencia y estabilidad.");

            // Verify serialized JSON contains zero URLs, file paths, or SHA-256 hashes
            string json = JsonSerializer.Serialize(available.Value);
            json.Should().NotContain(sha);
            json.Should().NotContain(".bin");
            json.Should().NotContain("Src");
        }
        finally
        {
            if (File.Exists(absPath)) File.Delete(absPath);
        }
    }
}
