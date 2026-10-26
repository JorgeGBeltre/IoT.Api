using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Domain.ValueObjects;
using IoT.Infrastructure.Persistence;
using IoT.Infrastructure.Persistence.Entities;
using IoT.Infrastructure.Repositories;
using IoT.Infrastructure.Retention;
using IoT.Protocols;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

public class RetentionAndCommandCorrelationTests
{
    private readonly Mock<IDeviceRepository> _deviceRepoMock = new();
    private readonly Mock<ITenantRepository> _tenantRepoMock = new();
    private readonly Mock<IStateCacheService> _stateCacheMock = new();
    private readonly Mock<ITelemetryStore> _telemetryStoreMock = new();
    private readonly Mock<IDeviceCommandRepository> _commandRepoMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<IFleetNotificationService> _notificationMock = new();
    private readonly Mock<ILogger<IngestTelemetryCommandHandler>> _ingestLoggerMock = new();
    private readonly Mock<ILogger<TelemetryRetentionService>> _retentionLoggerMock = new();
    private readonly ICompositeProtocolAdapter _protocolAdapter = CompositeProtocolAdapter.CreateDefault();

    [Fact]
    public async Task RetentionService_PurgesExpiredData_AggregatesCountsAcrossStores()
    {
        // Arrange
        _telemetryStoreMock.Setup(s => s.PurgePositionsOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(25);
        _telemetryStoreMock.Setup(s => s.PurgeTelemetryRecordsOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(75);
        _commandRepoMock.Setup(c => c.PurgeClosedCommandsOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(10);

        var retentionService = new TelemetryRetentionService(
            _telemetryStoreMock.Object,
            _commandRepoMock.Object,
            _retentionLoggerMock.Object);

        var options = new RetentionPolicyOptions
        {
            PositionsTtl = TimeSpan.FromDays(90),
            TelemetryRecordsTtl = TimeSpan.FromDays(90),
            ClosedCommandsTtl = TimeSpan.FromDays(30),
            SecurityTokensTtl = TimeSpan.FromHours(24)
        };

        // Act
        var result = await retentionService.PurgeExpiredDataAsync(options, CancellationToken.None);

        // Assert
        result.DeletedPositions.Should().Be(25);
        result.DeletedTelemetryRecords.Should().Be(75);
        result.DeletedClosedCommands.Should().Be(10);
        result.DeletedSecurityTokens.Should().Be(0);

        _telemetryStoreMock.Verify(s => s.PurgePositionsOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _telemetryStoreMock.Verify(s => s.PurgeTelemetryRecordsOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _commandRepoMock.Verify(c => c.PurgeClosedCommandsOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CommandCorrelation_WhenTerminalRespondsToCommandInSentStatus_ConfirmsCommandFIFO()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "GT06-8801", "868200000000001", "Delivery Truck 1", "GT06", "Generic").Value;

        var command = DeviceCommand.Create(tenantId, device.Id, "RELAY_OFF", "{\"relay\":0}", "admin").Value;
        command.MarkAsSent(); // Command is in-flight (Status = Sent)

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("GT06-8801", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        _stateCacheMock.Setup(c => c.IsDuplicateMessageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _stateCacheMock.Setup(c => c.TryUpdateLatestPositionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<GeoPosition>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _commandRepoMock.Setup(c => c.GetPendingCommandsByDeviceAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceCommand> { command });

        var handler = new IngestTelemetryCommandHandler(
            _protocolAdapter,
            _deviceRepoMock.Object,
            _tenantRepoMock.Object,
            _stateCacheMock.Object,
            _telemetryStoreMock.Object,
            _unitOfWorkMock.Object,
            _ingestLoggerMock.Object,
            _notificationMock.Object,
            commandRepository: _commandRepoMock.Object);

        // Terminal response JSON for GT06
        string payload = $$"""
        {
          "chipId": "GT06-8801",
          "timestamp": "{{DateTime.UtcNow:O}}",
          "latitude": -12.0464,
          "longitude": -77.0428,
          "command_response": "RELAY,0:OK"
        }
        """;

        var ingestCmd = new IngestTelemetryCommand(
            Topic: "gt06/events/GT06-8801",
            Payload: payload,
            TransportDeviceIdentifier: "GT06-8801");

        // Act
        var result = await handler.Handle(ingestCmd, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        command.Status.Should().Be(CommandStatus.Confirmed);
        command.ResultJson.Should().Be("RELAY,0:OK");

        _commandRepoMock.Verify(c => c.Update(It.Is<DeviceCommand>(cmd => cmd.Id == command.Id && cmd.Status == CommandStatus.Confirmed)), Times.Once);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _notificationMock.Verify(n => n.NotifyCommandStatusUpdatedAsync(
            tenantId,
            device.Id,
            command.Id,
            "Confirmed",
            "RELAY,0:OK",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CommandCorrelation_WhenTerminalRespondsToCommandInPendingStatus_ConfirmsCommand()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "GT06-8802", "868200000000002", "Delivery Truck 2", "GT06", "Generic").Value;

        var command = DeviceCommand.Create(tenantId, device.Id, "PARAM_CHECK", "{}", "admin").Value;
        // Command is in status Pending

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("GT06-8802", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        _stateCacheMock.Setup(c => c.IsDuplicateMessageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _commandRepoMock.Setup(c => c.GetPendingCommandsByDeviceAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceCommand> { command });

        var handler = new IngestTelemetryCommandHandler(
            _protocolAdapter,
            _deviceRepoMock.Object,
            _tenantRepoMock.Object,
            _stateCacheMock.Object,
            _telemetryStoreMock.Object,
            _unitOfWorkMock.Object,
            _ingestLoggerMock.Object,
            _notificationMock.Object,
            commandRepository: _commandRepoMock.Object);

        string payload = $$"""
        {
          "chipId": "GT06-8802",
          "timestamp": "{{DateTime.UtcNow:O}}",
          "command_response": "PARAM,APN:internet"
        }
        """;

        var ingestCmd = new IngestTelemetryCommand(
            Topic: "gt06/events/GT06-8802",
            Payload: payload,
            TransportDeviceIdentifier: "GT06-8802");

        // Act
        var result = await handler.Handle(ingestCmd, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        command.Status.Should().Be(CommandStatus.Confirmed);
        command.ResultJson.Should().Be("PARAM,APN:internet");

        _commandRepoMock.Verify(c => c.Update(It.Is<DeviceCommand>(cmd => cmd.Id == command.Id && cmd.Status == CommandStatus.Confirmed)), Times.Once);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CommandCorrelation_WhenNoInFlightCommandExists_HandlesResponseGracefully()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "GT06-8803", "868200000000003", "Delivery Truck 3", "GT06", "Generic").Value;

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("GT06-8803", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        _stateCacheMock.Setup(c => c.IsDuplicateMessageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _commandRepoMock.Setup(c => c.GetPendingCommandsByDeviceAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceCommand>()); // No pending or sent commands

        var handler = new IngestTelemetryCommandHandler(
            _protocolAdapter,
            _deviceRepoMock.Object,
            _tenantRepoMock.Object,
            _stateCacheMock.Object,
            _telemetryStoreMock.Object,
            _unitOfWorkMock.Object,
            _ingestLoggerMock.Object,
            _notificationMock.Object,
            commandRepository: _commandRepoMock.Object);

        string payload = $$"""
        {
          "chipId": "GT06-8803",
          "timestamp": "{{DateTime.UtcNow:O}}",
          "command_response": "UNSOLICITED_OK"
        }
        """;

        var ingestCmd = new IngestTelemetryCommand(
            Topic: "gt06/events/GT06-8803",
            Payload: payload,
            TransportDeviceIdentifier: "GT06-8803");

        // Act
        var result = await handler.Handle(ingestCmd, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _commandRepoMock.Verify(c => c.Update(It.IsAny<DeviceCommand>()), Times.Never);
    }

    [Fact]
    public void EntityConfigurations_SecurityTokensAndCommands_ImplementITenantScoped()
    {
        typeof(OtpCode).Should().Implement<ITenantScoped>();
        typeof(PasswordResetToken).Should().Implement<ITenantScoped>();
        typeof(DeviceCommand).Should().Implement<ITenantScoped>();
        typeof(PositionEntity).Should().Implement<ITenantScoped>();
        typeof(TelemetryRecordEntity).Should().Implement<ITenantScoped>();
        typeof(TenantSettings).Should().Implement<ITenantScoped>();
    }
}
