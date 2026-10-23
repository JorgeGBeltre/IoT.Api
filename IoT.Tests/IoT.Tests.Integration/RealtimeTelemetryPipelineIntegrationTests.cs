using System.Text.Json;
using FluentAssertions;
using IoT.Application.Commands.Commands;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Domain.ValueObjects;
using IoT.Protocols;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

public class RealtimeTelemetryPipelineIntegrationTests
{
    private readonly Mock<IDeviceRepository> _deviceRepoMock = new();
    private readonly Mock<ITenantRepository> _tenantRepoMock = new();
    private readonly Mock<IStateCacheService> _stateCacheMock = new();
    private readonly Mock<ITelemetryStore> _telemetryStoreMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<IMqttPublisher> _mqttPublisherMock = new();
    private readonly Mock<IDeviceCommandRepository> _commandRepoMock = new();
    private readonly Mock<ILogger<IngestTelemetryCommandHandler>> _ingestLoggerMock = new();
    private readonly Mock<ILogger<SendCommandToDeviceCommandHandler>> _commandLoggerMock = new();
    private readonly ICompositeProtocolAdapter _protocolAdapter = CompositeProtocolAdapter.CreateDefault();

    [Fact]
    public async Task Ingest_Esp8266GpsPayload_ShouldNormalizeAndPersistPosition()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "GPS-DEMO01", "AA:BB:CC:11:22:33", "Fleet Vehicle 01", "ESP8266", "Generic").Value;
        device.AssignCapability(CapabilityType.Gps);

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("GPS-DEMO01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        _stateCacheMock.Setup(c => c.IsDuplicateMessageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _stateCacheMock.Setup(c => c.TryUpdateLatestPositionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<GeoPosition>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var handler = new IngestTelemetryCommandHandler(
            _protocolAdapter,
            _deviceRepoMock.Object,
            _tenantRepoMock.Object,
            _stateCacheMock.Object,
            _telemetryStoreMock.Object,
            _unitOfWorkMock.Object,
            _ingestLoggerMock.Object);

        string gpsJson = """
        {
          "Device": { "ChipId": "GPS-DEMO01" },
          "Timestamp": "2026-10-22T04:15:00Z",
          "Details": {
            "accident": {
              "gps_data": {
                "latitude": 18.4861,
                "longitude": -69.9312,
                "speed": 55.4,
                "heading": 92.0,
                "altitude": 35.0,
                "satellites": 14,
                "is_valid": true
              }
            }
          }
        }
        """;

        var command = new IngestTelemetryCommand("telemetry", gpsJson);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _stateCacheMock.Verify(c => c.TryUpdateLatestPositionAsync(
            tenantId,
            device.Id,
            "GPS-DEMO01",
            It.Is<GeoPosition>(p => p.Latitude == 18.4861 && p.Longitude == -69.9312 && p.Heading == 92.0f),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _telemetryStoreMock.Verify(s => s.StorePositionAsync(
            tenantId,
            device.Id,
            It.IsAny<DateTime>(),
            It.IsAny<DateTime>(),
            It.Is<GeoPosition>(p => p.Latitude == 18.4861),
            It.IsAny<long>(),
            "real",
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ingest_GlpTankMonitorPayload_ShouldStoreTelemetryMetrics()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "GLP-TANK01", "11:22:33:44:55:66", "Industrial GLP Tank 01", "ESP32-S3", "GLP-EKF").Value;
        device.AssignCapability(CapabilityType.Pressure);
        device.AssignCapability(CapabilityType.LevelTelemetry);

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("GLP-TANK01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        _stateCacheMock.Setup(c => c.IsDuplicateMessageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var handler = new IngestTelemetryCommandHandler(
            _protocolAdapter,
            _deviceRepoMock.Object,
            _tenantRepoMock.Object,
            _stateCacheMock.Object,
            _telemetryStoreMock.Object,
            _unitOfWorkMock.Object,
            _ingestLoggerMock.Object);

        string glpJson = """
        {
          "Timestamp": "2026-10-22T04:15:00Z",
          "Details": {
            "sensors_filtered": {
              "pressure_bar": 6.45,
              "temp_celsius": 28.5,
              "level_mm": 845.0
            },
            "volume": {
              "percentage": 81.2
            }
          }
        }
        """;

        var command = new IngestTelemetryCommand("glp/telemetria/GLP-TANK01", glpJson);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _telemetryStoreMock.Verify(s => s.StoreTelemetryMetricsAsync(
            tenantId,
            device.Id,
            It.IsAny<DateTime>(),
            It.IsAny<DateTime>(),
            It.Is<Dictionary<string, double>>(m => m.ContainsKey("pressure_bar") && m["pressure_bar"] == 6.45 && m["fill_percentage"] == 81.2),
            It.IsAny<string>(),
            "real",
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ingest_RelaySwitchPayload_ShouldUpdateDeviceLastSeenAndSaveChanges()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "RELAY-CTRL01", "22:33:44:55:66:77", "Pump Station Relay", "ESP32", "RelayBLE").Value;
        device.AssignCapability(CapabilityType.Switch);

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("RELAY-CTRL01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        _stateCacheMock.Setup(c => c.IsDuplicateMessageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var handler = new IngestTelemetryCommandHandler(
            _protocolAdapter,
            _deviceRepoMock.Object,
            _tenantRepoMock.Object,
            _stateCacheMock.Object,
            _telemetryStoreMock.Object,
            _unitOfWorkMock.Object,
            _ingestLoggerMock.Object);

        string relayJson = """
        {
          "Device": { "ChipId": "RELAY-CTRL01" },
          "Timestamp": "2026-10-22T04:15:00Z",
          "Details": {
            "devices_count": 2,
            "rssi": -62.0,
            "devices": {
              "device_switch_1": { "state": "on", "desired_state": "on" },
              "device_switch_2": { "state": "off", "desired_state": "off" }
            }
          }
        }
        """;

        var command = new IngestTelemetryCommand("relay/telemetria/RELAY-ESP32-CTRL01", relayJson);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendCommand_ToDevice_ShouldPublishToMqttBrokerAndPersistAuditLog()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "RELAY-CTRL01", "22:33:44:55:66:77", "Pump Station Relay", "ESP32", "RelayBLE").Value;
        device.AssignCapability(CapabilityType.Switch);

        _deviceRepoMock.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        _mqttPublisherMock.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), 1, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new SendCommandToDeviceCommandHandler(
            _deviceRepoMock.Object,
            _commandRepoMock.Object,
            _mqttPublisherMock.Object,
            _unitOfWorkMock.Object,
            _commandLoggerMock.Object);

        var command = new SendCommandToDeviceCommand(tenantId, device.Id, "RELAY_SET", "{\"relayId\":1,\"state\":1}");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _mqttPublisherMock.Verify(p => p.PublishAsync(
            $"devices/{device.ChipId}/commands",
            It.Is<string>(payload => payload.Contains("RELAY_SET")),
            It.Is<MqttPublishOptions>(opt => opt.Qos == 1 && opt.MessageExpiryInterval == 300),
            It.IsAny<CancellationToken>()), Times.Once);

        _commandRepoMock.Verify(r => r.AddAsync(It.IsAny<DeviceCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
