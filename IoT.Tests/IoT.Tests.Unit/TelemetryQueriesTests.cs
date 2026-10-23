using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Telemetry.Queries;
using Moq;
using Xunit;

namespace IoT.Tests.Unit;

public class TelemetryQueriesTests
{
    private readonly Mock<ITelemetryStore> _telemetryStoreMock = new();

    [Fact]
    public async Task GetDevicePositionHistoryQuery_ShouldForwardSourceFilterAndReturnPositions()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var from = DateTime.UtcNow.AddHours(-2);
        var to = DateTime.UtcNow;
        var expectedPositions = new List<HistoricalPositionDto>
        {
            new(
                RecordedAt: DateTime.UtcNow.AddMinutes(-30),
                Latitude: 18.4861,
                Longitude: -69.9312,
                SpeedKph: 55.4f,
                Heading: 120.5f,
                AltitudeM: 15.0f,
                Satellites: 9,
                IsValid: true,
                Source: "simulated",
                MessageId: "msg-sim-001"
            )
        };

        _telemetryStoreMock
            .Setup(s => s.GetPositionsAsync(tenantId, deviceId, from, to, 100, "simulated", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPositions);

        var handler = new GetDevicePositionHistoryQueryHandler(_telemetryStoreMock.Object);
        var query = new GetDevicePositionHistoryQuery(tenantId, deviceId, from, to, 100, "simulated");

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Source.Should().Be("simulated");
        result.Value[0].MessageId.Should().Be("msg-sim-001");
        result.Value[0].Latitude.Should().Be(18.4861);

        _telemetryStoreMock.Verify(s => s.GetPositionsAsync(tenantId, deviceId, from, to, 100, "simulated", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetDeviceTelemetryHistoryQuery_ShouldForwardSourceFilterAndReturnMetrics()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow;
        var metricsDict = new Dictionary<string, double> { ["battery_level"] = 92.5, ["temperature"] = 28.3 };
        var expectedTelemetry = new List<HistoricalTelemetryDto>
        {
            new(
                RecordedAt: DateTime.UtcNow.AddMinutes(-10),
                Metrics: metricsDict,
                RawPayload: "{\"temperature\":28.3}",
                Source: "real",
                MessageId: "msg-real-123"
            )
        };

        _telemetryStoreMock
            .Setup(s => s.GetTelemetryAsync(tenantId, deviceId, from, to, 50, "real", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedTelemetry);

        var handler = new GetDeviceTelemetryHistoryQueryHandler(_telemetryStoreMock.Object);
        var query = new GetDeviceTelemetryHistoryQuery(tenantId, deviceId, from, to, 50, "real");

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Source.Should().Be("real");
        result.Value[0].MessageId.Should().Be("msg-real-123");
        result.Value[0].Metrics["battery_level"].Should().Be(92.5);

        _telemetryStoreMock.Verify(s => s.GetTelemetryAsync(tenantId, deviceId, from, to, 50, "real", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void HistoricalDtos_DefaultValues_ShouldBeRealSourceAndNullMessageId()
    {
        var pos = new HistoricalPositionDto(
            RecordedAt: DateTime.UtcNow,
            Latitude: 18.0,
            Longitude: -69.0,
            SpeedKph: null,
            Heading: null,
            AltitudeM: null,
            Satellites: null,
            IsValid: true);

        pos.Source.Should().Be("real");
        pos.MessageId.Should().BeNull();

        var tel = new HistoricalTelemetryDto(
            RecordedAt: DateTime.UtcNow,
            Metrics: new Dictionary<string, double>(),
            RawPayload: null);

        tel.Source.Should().Be("real");
        tel.MessageId.Should().BeNull();
    }
}
