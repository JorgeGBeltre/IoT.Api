using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Events.Queries;
using Moq;
using Xunit;

namespace IoT.Tests.Unit;

public class EventsQueryTests
{
    private readonly Mock<IEventQueryService> _eventQueryServiceMock = new();

    [Fact]
    public async Task GetEventsQuery_ShouldFail_WhenTenantIdIsEmpty()
    {
        var handler = new GetEventsQueryHandler(_eventQueryServiceMock.Object);
        var query = new GetEventsQuery(Guid.Empty);

        var result = await handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Events.InvalidTenant");
        _eventQueryServiceMock.Verify(s => s.GetEventsAsync(
            It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetEventsQuery_ShouldForwardFiltersAndClampLimit()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var fromUtc = DateTime.UtcNow.AddHours(-12);
        var toUtc = DateTime.UtcNow;

        var expectedEvents = new List<EventItemDto>
        {
            new(
                Id: Guid.NewGuid(),
                DeviceId: deviceId,
                DeviceName: "GPS-Truck-101",
                ChipId: "CHIP-101",
                EventType: "command",
                Severity: "info",
                Title: "Comando: CUT_ENGINE",
                Message: "Comando 'CUT_ENGINE' (Confirmed) solicitado por admin",
                DataJson: "{\"status\":\"ok\"}",
                TimestampUtc: DateTime.UtcNow.AddMinutes(-10)
            ),
            new(
                Id: Guid.NewGuid(),
                DeviceId: deviceId,
                DeviceName: "GPS-Truck-101",
                ChipId: "CHIP-101",
                EventType: "alert",
                Severity: "critical",
                Title: "DeviceAlert.SOS",
                Message: "Alerta de Emergencia / SOS activada en GPS-Truck-101",
                DataJson: null,
                TimestampUtc: DateTime.UtcNow.AddMinutes(-5)
            )
        };

        _eventQueryServiceMock
            .Setup(s => s.GetEventsAsync(
                tenantId,
                deviceId,
                "critical",
                "alert",
                fromUtc,
                toUtc,
                1000,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedEvents);

        var handler = new GetEventsQueryHandler(_eventQueryServiceMock.Object);
        // Limit 5000 should be clamped to 1000
        var query = new GetEventsQuery(
            tenantId,
            deviceId,
            Severity: "critical",
            EventType: "alert",
            FromUtc: fromUtc,
            ToUtc: toUtc,
            Limit: 5000);

        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value[1].EventType.Should().Be("alert");
        result.Value[1].Severity.Should().Be("critical");

        _eventQueryServiceMock.Verify(s => s.GetEventsAsync(
            tenantId,
            deviceId,
            "critical",
            "alert",
            fromUtc,
            toUtc,
            1000,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
