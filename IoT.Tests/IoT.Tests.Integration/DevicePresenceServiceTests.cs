using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

public sealed class DevicePresenceServiceTests
{
    private readonly Mock<IDeviceRepository> _deviceRepoMock = new();
    private readonly Mock<IUnitOfWork> _uowMock = new();
    private readonly Mock<IFleetNotificationService> _notifMock = new();
    private readonly IConfiguration _configuration;

    public DevicePresenceServiceTests()
    {
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Worker:PresenceGracePeriodSeconds"] = "180"
        }).Build();
    }

    [Fact]
    public async Task CheckAndMarkOfflineDevicesAsync_WhenStaleDevicesExist_MarksThemOfflineAndPersists()
    {
        var tenantId = Guid.NewGuid();
        var dev1 = Device.Create(tenantId, "DEV_01", "AA:BB:CC:11:22:01", "Sensor A", "Generic", "m1").Value;
        var dev2 = Device.Create(tenantId, "DEV_02", "AA:BB:CC:11:22:02", "Sensor B", "Generic", "m2").Value;

        // Simular que estaban Online hace 10 minutos
        dev1.RecordTelemetry(DateTime.UtcNow.AddMinutes(-10));
        dev2.RecordTelemetry(DateTime.UtcNow.AddMinutes(-5));

        dev1.OperationalStatus.Should().Be(DeviceOperationalStatus.Online);
        dev2.OperationalStatus.Should().Be(DeviceOperationalStatus.Online);

        _deviceRepoMock.Setup(r => r.GetOnlineDevicesOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Device> { dev1, dev2 });

        var service = new DevicePresenceService(
            _deviceRepoMock.Object,
            _uowMock.Object,
            NullLogger<DevicePresenceService>.Instance,
            _configuration,
            _notifMock.Object);

        int result = await service.CheckAndMarkOfflineDevicesAsync(TimeSpan.FromMinutes(3));

        result.Should().Be(2);
        dev1.OperationalStatus.Should().Be(DeviceOperationalStatus.Offline);
        dev2.OperationalStatus.Should().Be(DeviceOperationalStatus.Offline);

        _deviceRepoMock.Verify(r => r.Update(dev1), Times.Once);
        _deviceRepoMock.Verify(r => r.Update(dev2), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        _notifMock.Verify(n => n.NotifyDeviceStatusChangedAsync(
            tenantId, dev1.Id, dev1.ChipId, "Offline", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _notifMock.Verify(n => n.NotifyDeviceStatusChangedAsync(
            tenantId, dev2.Id, dev2.ChipId, "Offline", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAndMarkOfflineDevicesAsync_WhenNoStaleDevices_ReturnsZero()
    {
        _deviceRepoMock.Setup(r => r.GetOnlineDevicesOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Device>());

        var service = new DevicePresenceService(
            _deviceRepoMock.Object,
            _uowMock.Object,
            NullLogger<DevicePresenceService>.Instance,
            _configuration,
            _notifMock.Object);

        int result = await service.CheckAndMarkOfflineDevicesAsync(TimeSpan.FromMinutes(3));

        result.Should().Be(0);
        _deviceRepoMock.Verify(r => r.Update(It.IsAny<Device>()), Times.Never);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _notifMock.Verify(n => n.NotifyDeviceStatusChangedAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAndMarkOfflineDevicesAsync_WhenNotificationFails_StillPersistsAndDoesNotThrow()
    {
        var tenantId = Guid.NewGuid();
        var dev = Device.Create(tenantId, "DEV_ERR", "AA:BB:CC:11:22:03", "Tracker", "Generic", "m3").Value;
        dev.RecordTelemetry(DateTime.UtcNow.AddMinutes(-10));

        _deviceRepoMock.Setup(r => r.GetOnlineDevicesOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Device> { dev });

        _notifMock.Setup(n => n.NotifyDeviceStatusChangedAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SignalR unreachable"));

        var service = new DevicePresenceService(
            _deviceRepoMock.Object,
            _uowMock.Object,
            NullLogger<DevicePresenceService>.Instance,
            _configuration,
            _notifMock.Object);

        int result = await service.CheckAndMarkOfflineDevicesAsync(TimeSpan.FromMinutes(3));

        result.Should().Be(1);
        dev.OperationalStatus.Should().Be(DeviceOperationalStatus.Offline);
        _deviceRepoMock.Verify(r => r.Update(dev), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
