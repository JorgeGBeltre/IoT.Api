using FluentAssertions;
using IoT.Application.Commands.Commands;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Retention;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

public class Phase1Etapa5CommandsAndPersistenceTests
{
    // =========================================================================
    // E5-01: Full DeviceCommand Lifecycle (Pending -> Sent -> Delivered -> Confirmed)
    // =========================================================================

    [Fact]
    public void DeviceCommand_FullLifecycleTransitions_ExecutePredictably()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        string clientReqId = "req-lifecycle-001";

        // 1. Pending
        var createResult = DeviceCommand.Create(tenantId, deviceId, "valve:open", "{\"flow\":100}", "Operator", clientReqId);
        createResult.IsSuccess.Should().BeTrue();
        var command = createResult.Value;

        command.Status.Should().Be(CommandStatus.Pending);
        command.SentAt.Should().BeNull();
        command.DeliveredAt.Should().BeNull();
        command.ConfirmedAt.Should().BeNull();
        command.ClientRequestId.Should().Be(clientReqId);

        // 2. Sent
        command.MarkAsSent();
        command.Status.Should().Be(CommandStatus.Sent);
        command.SentAt.Should().NotBeNull();

        // 3. Delivered (Device ACK)
        command.MarkAsDelivered("{\"ack\":\"received_by_esp32\"}");
        command.Status.Should().Be(CommandStatus.Delivered);
        command.DeliveredAt.Should().NotBeNull();
        command.ResultJson.Should().Be("{\"ack\":\"received_by_esp32\"}");

        // 4. Confirmed (Execution finished on physical actuator)
        command.Confirm("{\"status\":\"valve_fully_opened\",\"flow_rate\":99.8}");
        command.Status.Should().Be(CommandStatus.Confirmed);
        command.ConfirmedAt.Should().NotBeNull();
        command.ResultJson.Should().Be("{\"status\":\"valve_fully_opened\",\"flow_rate\":99.8}");
    }

    [Fact]
    public async Task AcknowledgeDeviceCommand_CorrelatesAndUpdatesCommandStatus()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        var command = DeviceCommand.Create(tenantId, deviceId, "pump:start", null, "Admin", "client-req-999").Value;
        command.MarkAsSent();

        var repoMock = new Mock<IDeviceCommandRepository>();
        repoMock.Setup(r => r.GetByIdAsync(command.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(command);

        var deviceRepoMock = new Mock<IDeviceRepository>();
        var uowMock = new Mock<IUnitOfWork>();
        var notifMock = new Mock<IFleetNotificationService>();
        var loggerMock = new Mock<ILogger<AcknowledgeDeviceCommandHandler>>();

        var handler = new AcknowledgeDeviceCommandHandler(
            repoMock.Object,
            deviceRepoMock.Object,
            uowMock.Object,
            loggerMock.Object,
            notifMock.Object);

        // Act 1: Acknowledge delivery
        var deliveryAck = new AcknowledgeDeviceCommand(
            CommandId: command.Id,
            ClientRequestId: null,
            Status: "delivered",
            ResultPayload: "{\"received\":true}");

        var res1 = await handler.Handle(deliveryAck, CancellationToken.None);
        res1.IsSuccess.Should().BeTrue();
        command.Status.Should().Be(CommandStatus.Delivered);

        // Act 2: Acknowledge execution completion
        var confirmAck = new AcknowledgeDeviceCommand(
            CommandId: command.Id,
            ClientRequestId: null,
            Status: "confirmed",
            ResultPayload: "{\"executed\":true,\"rpm\":3000}");

        var res2 = await handler.Handle(confirmAck, CancellationToken.None);
        res2.IsSuccess.Should().BeTrue();
        command.Status.Should().Be(CommandStatus.Confirmed);

        // Verify SignalR notifications dispatched
        notifMock.Verify(n => n.NotifyCommandStatusUpdatedAsync(
            tenantId,
            deviceId,
            command.Id,
            "Confirmed",
            It.IsAny<string>(),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendCommandToDevice_WithDuplicateClientRequestId_ReturnsExistingCommandIdWithoutRepublishing()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        string clientReqId = "idempotent-key-unique-42";

        var existingCommand = DeviceCommand.Create(tenantId, deviceId, "reboot", null, "Admin", clientReqId).Value;
        existingCommand.MarkAsSent();

        var repoMock = new Mock<IDeviceCommandRepository>();
        repoMock.Setup(r => r.GetByClientRequestIdAsync(tenantId, clientReqId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingCommand);

        var deviceRepoMock = new Mock<IDeviceRepository>();
        var mqttMock = new Mock<IMqttPublisher>();
        var uowMock = new Mock<IUnitOfWork>();
        var loggerMock = new Mock<ILogger<SendCommandToDeviceCommandHandler>>();

        var handler = new SendCommandToDeviceCommandHandler(
            deviceRepoMock.Object,
            repoMock.Object,
            mqttMock.Object,
            uowMock.Object,
            loggerMock.Object);

        var request = new SendCommandToDeviceCommand(
            TenantId: tenantId,
            DeviceId: deviceId,
            CommandName: "reboot",
            ClientRequestId: clientReqId);

        var result = await handler.Handle(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(existingCommand.Id);

        // Assert MQTT publisher was NOT invoked again (idempotency preserved)
        mqttMock.Verify(m => m.PublishAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<MqttPublishOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================================
    // E5-02: Device Admin and Operational Status Validation on Dispatch
    // =========================================================================

    [Theory]
    [InlineData(DeviceAdminStatus.Suspended)]
    [InlineData(DeviceAdminStatus.Revoked)]
    public async Task SendCommandToDevice_WhenAdminStatusNotActive_RejectsDispatch(DeviceAdminStatus adminStatus)
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "CHIP-SUSPENDED-1", "00:11:22:33:44:55", "Suspended Device", "ESP32", "Generic").Value;
        device.SetAdminStatus(adminStatus);

        var deviceRepoMock = new Mock<IDeviceRepository>();
        deviceRepoMock.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        var repoMock = new Mock<IDeviceCommandRepository>();
        var mqttMock = new Mock<IMqttPublisher>();
        var uowMock = new Mock<IUnitOfWork>();
        var loggerMock = new Mock<ILogger<SendCommandToDeviceCommandHandler>>();

        var handler = new SendCommandToDeviceCommandHandler(
            deviceRepoMock.Object,
            repoMock.Object,
            mqttMock.Object,
            uowMock.Object,
            loggerMock.Object);

        var request = new SendCommandToDeviceCommand(tenantId, device.Id, "relay:on");

        var result = await handler.Handle(request, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.Suspended");
        mqttMock.Verify(m => m.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MqttPublishOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendCommandToDevice_WhenDeviceInErrorState_RejectsDispatch()
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "CHIP-ERROR-1", "00:11:22:33:44:56", "Faulty Device", "ESP32", "Generic").Value;
        device.SetOperationalStatus(DeviceOperationalStatus.Error);

        var deviceRepoMock = new Mock<IDeviceRepository>();
        deviceRepoMock.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        var repoMock = new Mock<IDeviceCommandRepository>();
        var mqttMock = new Mock<IMqttPublisher>();
        var uowMock = new Mock<IUnitOfWork>();
        var loggerMock = new Mock<ILogger<SendCommandToDeviceCommandHandler>>();

        var handler = new SendCommandToDeviceCommandHandler(
            deviceRepoMock.Object,
            repoMock.Object,
            mqttMock.Object,
            uowMock.Object,
            loggerMock.Object);

        var request = new SendCommandToDeviceCommand(tenantId, device.Id, "actuate");

        var result = await handler.Handle(request, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.InErrorState");
    }

    [Fact]
    public async Task SendCommandToDevice_WhenDeviceOffline_RejectsUnlessOfflineQueueingAllowed()
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "CHIP-OFFLINE-1", "00:11:22:33:44:57", "Sleeping Device", "ESP32", "Generic").Value;
        device.SetOperationalStatus(DeviceOperationalStatus.Offline);

        var deviceRepoMock = new Mock<IDeviceRepository>();
        deviceRepoMock.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        var repoMock = new Mock<IDeviceCommandRepository>();
        var mqttMock = new Mock<IMqttPublisher>();
        var uowMock = new Mock<IUnitOfWork>();
        var loggerMock = new Mock<ILogger<SendCommandToDeviceCommandHandler>>();

        var handler = new SendCommandToDeviceCommandHandler(
            deviceRepoMock.Object,
            repoMock.Object,
            mqttMock.Object,
            uowMock.Object,
            loggerMock.Object);

        // Case A: Disallow offline queueing (default) -> Rejection
        var reqReject = new SendCommandToDeviceCommand(tenantId, device.Id, "ping", AllowOfflineQueueing: false);
        var resReject = await handler.Handle(reqReject, CancellationToken.None);
        resReject.IsFailure.Should().BeTrue();
        resReject.Error.Code.Should().Be("Device.Offline");

        // Case B: Explicitly allow offline queueing -> Dispatched to broker queue with MQTT expiry
        var reqAllow = new SendCommandToDeviceCommand(tenantId, device.Id, "config:update", AllowOfflineQueueing: true);
        var resAllow = await handler.Handle(reqAllow, CancellationToken.None);
        resAllow.IsSuccess.Should().BeTrue();
        mqttMock.Verify(m => m.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MqttPublishOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================================
    // E5-03: Data Retention Policy & Maintenance Purge
    // =========================================================================

    [Fact]
    public async Task TelemetryRetentionService_PurgesExpiredPositionsAndClosedCommands()
    {
        var telemetryStoreMock = new Mock<ITelemetryStore>();
        telemetryStoreMock.Setup(s => s.PurgePositionsOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(14); // 14 expired positions purged
        telemetryStoreMock.Setup(s => s.PurgeTelemetryRecordsOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(82); // 82 expired telemetry records purged

        var cmdRepoMock = new Mock<IDeviceCommandRepository>();
        cmdRepoMock.Setup(c => c.PurgeClosedCommandsOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5); // 5 closed commands purged

        var loggerMock = new Mock<ILogger<TelemetryRetentionService>>();
        var retentionService = new TelemetryRetentionService(
            telemetryStoreMock.Object,
            cmdRepoMock.Object,
            loggerMock.Object);

        var purgeOptions = new RetentionPolicyOptions
        {
            PositionsTtl = TimeSpan.FromDays(90),
            TelemetryRecordsTtl = TimeSpan.FromDays(90),
            ClosedCommandsTtl = TimeSpan.FromDays(30),
            SecurityTokensTtl = TimeSpan.FromHours(24)
        };

        // Act
        var result = await retentionService.PurgeExpiredDataAsync(purgeOptions, CancellationToken.None);

        // Assert
        result.DeletedPositions.Should().Be(14);
        result.DeletedTelemetryRecords.Should().Be(82);
        result.DeletedClosedCommands.Should().Be(5);

        telemetryStoreMock.Verify(s => s.PurgePositionsOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        telemetryStoreMock.Verify(s => s.PurgeTelemetryRecordsOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        cmdRepoMock.Verify(c => c.PurgeClosedCommandsOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
