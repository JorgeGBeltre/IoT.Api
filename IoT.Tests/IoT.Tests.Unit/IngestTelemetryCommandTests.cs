using FluentAssertions;
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

namespace IoT.Tests.Unit;

public class IngestTelemetryCommandTests
{
    private readonly Mock<IDeviceRepository> _deviceRepoMock = new();
    private readonly Mock<ITenantRepository> _tenantRepoMock = new();
    private readonly Mock<IStateCacheService> _stateCacheMock = new();
    private readonly Mock<ITelemetryStore> _telemetryStoreMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ILogger<IngestTelemetryCommandHandler>> _loggerMock = new();
    private readonly ICompositeProtocolAdapter _protocolAdapter = CompositeProtocolAdapter.CreateDefault();

    [Fact]
    public async Task Handle_WithValidGpsPayload_ShouldUpdateCacheAndStorePosition()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "GPS-ESP8266-99", "AA:BB:CC", "Unit 99", "ESP8266", "Generic").Value;
        device.AssignCapability(CapabilityType.Gps);

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("GPS-ESP8266-99", It.IsAny<CancellationToken>()))
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
            _loggerMock.Object);

        string payload = """
        {
          "Device": { "ChipId": "GPS-ESP8266-99" },
          "Timestamp": "2026-10-21T10:00:00Z",
          "Details": {
            "accident": {
              "gps_data": { "latitude": 18.4861, "longitude": -69.9312, "speed": 60.0, "is_valid": true }
            }
          }
        }
        """;

        var command = new IngestTelemetryCommand("telemetry", payload);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _stateCacheMock.Verify(c => c.TryUpdateLatestPositionAsync(tenantId, device.Id, "GPS-ESP8266-99", It.Is<GeoPosition>(p => p.Latitude == 18.4861), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _telemetryStoreMock.Verify(s => s.StorePositionAsync(tenantId, device.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GeoPosition>(), It.IsAny<long>(), "real", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithDuplicateMessage_ShouldDiscardWithoutStoring()
    {
        // Arrange
        _stateCacheMock.Setup(c => c.IsDuplicateMessageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // Duplicate!

        var handler = new IngestTelemetryCommandHandler(
            _protocolAdapter,
            _deviceRepoMock.Object,
            _tenantRepoMock.Object,
            _stateCacheMock.Object,
            _telemetryStoreMock.Object,
            _unitOfWorkMock.Object,
            _loggerMock.Object);

        string payload = """
        {
          "Device": { "ChipId": "GPS-ESP8266-99" },
          "Timestamp": "2026-10-21T10:00:00Z",
          "Details": {
            "accident": { "gps_data": { "latitude": 18.4861, "longitude": -69.9312 } }
          }
        }
        """;

        var command = new IngestTelemetryCommand("telemetry", payload);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _deviceRepoMock.Verify(r => r.GetByChipIdGlobalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _telemetryStoreMock.Verify(s => s.StorePositionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GeoPosition>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
