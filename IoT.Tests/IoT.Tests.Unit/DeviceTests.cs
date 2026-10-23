using FluentAssertions;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using Xunit;

namespace IoT.Tests.Unit;

public class DeviceTests
{
    [Fact]
    public void Create_WithValidParameters_ShouldReturnActiveOfflineDevice()
    {
        // Arrange
        var tenantId = Guid.NewGuid();

        // Act
        var result = Device.Create(
            tenantId,
            chipId: "0000A1B2C3D4",
            macAddress: "24:6F:28:11:22:33",
            name: "GPS-Unit-101",
            model: "ESP8266-Accident",
            manufacturer: "Generic",
            firmwareVersion: "v1.0.3");

        // Assert
        result.IsSuccess.Should().BeTrue();
        var device = result.Value;
        device.TenantId.Should().Be(tenantId);
        device.ChipId.Should().Be("0000A1B2C3D4");
        device.AdminStatus.Should().Be(DeviceAdminStatus.Active);
        device.OperationalStatus.Should().Be(DeviceOperationalStatus.Offline);
        device.Capabilities.Should().BeEmpty();
    }

    [Fact]
    public void AssignCapability_ShouldAddCapabilityAndMarkAsPresent()
    {
        // Arrange
        var device = Device.Create(Guid.NewGuid(), "CHIP01", "MAC01", "Device1", "M1", "Manuf1").Value;

        // Act
        var assignGps = device.AssignCapability(CapabilityType.Gps);
        var assignSwitch = device.AssignCapability(CapabilityType.Switch, "{\"relayPin\": 4}");

        // Assert
        assignGps.IsSuccess.Should().BeTrue();
        assignSwitch.IsSuccess.Should().BeTrue();
        device.Capabilities.Should().HaveCount(2);
        device.HasCapability(CapabilityType.Gps).Should().BeTrue();
        device.HasCapability(CapabilityType.Switch).Should().BeTrue();
        device.HasCapability(CapabilityType.Pressure).Should().BeFalse();
    }

    [Fact]
    public void RemoveCapability_ShouldRemoveSuccessfully()
    {
        // Arrange
        var device = Device.Create(Guid.NewGuid(), "CHIP01", "MAC01", "Device1", "M1", "Manuf1").Value;
        device.AssignCapability(CapabilityType.Gps);

        // Act
        var removeResult = device.RemoveCapability(CapabilityType.Gps);

        // Assert
        removeResult.IsSuccess.Should().BeTrue();
        device.HasCapability(CapabilityType.Gps).Should().BeFalse();
    }

    [Fact]
    public void RecordTelemetry_ShouldUpdateTimestampsAndSetOnline()
    {
        // Arrange
        var device = Device.Create(Guid.NewGuid(), "CHIP01", "MAC01", "Device1", "M1", "Manuf1").Value;
        var now = DateTime.UtcNow;

        // Act
        device.RecordTelemetry(now);

        // Assert
        device.OperationalStatus.Should().Be(DeviceOperationalStatus.Online);
        device.LastTelemetryAt.Should().Be(now);
        device.LastCommunicationAt.Should().Be(now);
    }
}
