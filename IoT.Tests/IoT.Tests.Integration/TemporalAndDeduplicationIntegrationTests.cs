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

namespace IoT.Tests.Integration;

public class TemporalAndDeduplicationIntegrationTests
{
    private readonly Mock<IDeviceRepository> _deviceRepoMock = new();
    private readonly Mock<ITenantRepository> _tenantRepoMock = new();
    private readonly Mock<IStateCacheService> _stateCacheMock = new();
    private readonly Mock<ITelemetryStore> _telemetryStoreMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ILogger<IngestTelemetryCommandHandler>> _loggerMock = new();
    private readonly ICompositeProtocolAdapter _protocolAdapter = CompositeProtocolAdapter.CreateDefault();

    [Fact]
    public async Task TemporalProtection_DelayedPackageFromDeadZone_ShouldNotUpdateCurrentLiveMarker()
    {
        // Scenario: A vehicle sends a position at 10:05:00.
        // Later, an out-of-order packet arrives that was recorded at 10:02:00 in a cellular dead zone.
        // The delayed packet MUST still be stored in the historical log, but the live position cache MUST reject updating the current marker.
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "GPS-DEMO01", "AA:BB:CC:11:22:33", "Fleet Vehicle 01", "ESP8266", "Generic").Value;
        device.AssignCapability(CapabilityType.Gps);

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("GPS-DEMO01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        _stateCacheMock.Setup(c => c.IsDuplicateMessageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Simulate Redis returning false because the existing cached timestamp is newer than 10:02:00
        _stateCacheMock.Setup(c => c.TryUpdateLatestPositionAsync(
                tenantId,
                device.Id,
                "GPS-DEMO01",
                It.IsAny<GeoPosition>(),
                It.Is<DateTime>(dt => dt < new DateTime(2026, 10, 22, 10, 5, 0, DateTimeKind.Utc)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // Cache rejects backwards time step

        var handler = new IngestTelemetryCommandHandler(
            _protocolAdapter,
            _deviceRepoMock.Object,
            _tenantRepoMock.Object,
            _stateCacheMock.Object,
            _telemetryStoreMock.Object,
            _unitOfWorkMock.Object,
            _loggerMock.Object);

        // Delayed packet from 10:02:00
        string delayedGpsJson = """
        {
          "Device": { "ChipId": "GPS-DEMO01" },
          "Timestamp": "2026-10-22T10:02:00Z",
          "Details": {
            "accident": {
              "gps_data": {
                "latitude": 18.4800,
                "longitude": -69.9350,
                "speed": 35.0,
                "is_valid": true
              }
            }
          }
        }
        """;

        var command = new IngestTelemetryCommand("telemetry", delayedGpsJson);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // 1. Position IS preserved in history (telemetry store) for audits and playback
        _telemetryStoreMock.Verify(s => s.StorePositionAsync(
            tenantId,
            device.Id,
            It.Is<DateTime>(dt => dt.Minute == 2),
            It.IsAny<DateTime>(),
            It.IsAny<GeoPosition>(),
            It.IsAny<long>(),
            "real",
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // 2. The cache was called, and returned false without crashing or rolling back
        _stateCacheMock.Verify(c => c.TryUpdateLatestPositionAsync(
            tenantId,
            device.Id,
            "GPS-DEMO01",
            It.IsAny<GeoPosition>(),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Deduplication_IdenticalPacketWithinWindow_ShouldBeDiscarded()
    {
        // Scenario: Device retransmits exact same MQTT frame due to QoS 1 retry
        _stateCacheMock.Setup(c => c.IsDuplicateMessageAsync("GPS-DEMO01", It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // Flagged as duplicate!

        var handler = new IngestTelemetryCommandHandler(
            _protocolAdapter,
            _deviceRepoMock.Object,
            _tenantRepoMock.Object,
            _stateCacheMock.Object,
            _telemetryStoreMock.Object,
            _unitOfWorkMock.Object,
            _loggerMock.Object);

        string gpsJson = """
        {
          "Device": { "ChipId": "GPS-DEMO01" },
          "Timestamp": "2026-10-22T10:00:00Z",
          "Details": {
            "accident": {
              "gps_data": { "latitude": 18.4861, "longitude": -69.9312 }
            }
          }
        }
        """;

        var command = new IngestTelemetryCommand("telemetry", gpsJson);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        // Repository should never be queried for a duplicate packet
        _deviceRepoMock.Verify(r => r.GetByChipIdGlobalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // Telemetry should never be written to DB twice
        _telemetryStoreMock.Verify(s => s.StorePositionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GeoPosition>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
