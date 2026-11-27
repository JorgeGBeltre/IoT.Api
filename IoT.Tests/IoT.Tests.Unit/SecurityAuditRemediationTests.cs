using System.Net;
using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Services;
using IoT.Protocols.Binary;
using IoT.Protocols.Tcp;
using Moq;

namespace IoT.Tests.Unit;

public sealed class SecurityAuditRemediationTests
{
    [Fact]
    public void Gt06TcpHandler_BuildCommand_RejectsEmptyOrOversizedPayloads_WithoutStackOverflow()
    {
        var handler = new Gt06TcpHandler();

        handler.BuildCommand("", 1).Should().BeNull();
        handler.BuildCommand("   ", 2).Should().BeNull();
        handler.BuildCommand(new string('A', 241), 3).Should().BeNull();
        handler.BuildCommand(new string('A', 2_000_000), 4).Should().BeNull();

        var validFrame = handler.BuildCommand("RELAY,1#", 5);
        validFrame.Should().NotBeNull();
        validFrame![0].Should().Be(0x78);
        validFrame[1].Should().Be(0x78);
    }

    [Fact]
    public void R12LFFrame_TryParse_RejectsUndersizedShortAndExtendedLengthFields()
    {
        ReadOnlySpan<byte> shortZeroLen = [0x78, 0x78, 0x00, 0x0D, 0x0A];
        R12LFFrame.TryParse(shortZeroLen, out _, out _).Should().BeFalse();

        ReadOnlySpan<byte> shortFourLen = [0x78, 0x78, 0x04, 0x01, 0x00, 0x01, 0x00, 0x0D, 0x0A];
        R12LFFrame.TryParse(shortFourLen, out _, out _).Should().BeFalse();

        ReadOnlySpan<byte> extendedThreeLen = [0x79, 0x79, 0x00, 0x03, 0x94, 0x00, 0x01, 0x0D, 0x0A];
        R12LFFrame.TryParse(extendedThreeLen, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("::ffff:169.254.169.254", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.18.0.2", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("93.184.216.34", false)]
    public void OutboundWebhookDispatcher_IsDisallowedTargetAddress_EnforcesPrivateAndImdsBlocklist(
        string addressText,
        bool expectedDisallowed)
    {
        var ip = IPAddress.Parse(addressText);
        OutboundWebhookDispatcher.IsDisallowedTargetAddress(ip).Should().Be(expectedDisallowed);
    }

    [Fact]
    public void Device_Rename_Enforces100CharacterDatabaseColumnLimit()
    {
        var device = Device.Create(
            Guid.NewGuid(),
            "ESP32-AA11BB22",
            "AA:BB:CC:DD:EE:FF",
            "Nodo Inicial",
            "ESP32_RELAY_WIFI",
            "VESIS").Value;

        var tooLongResult = device.Rename(new string('X', 101));
        tooLongResult.IsFailure.Should().BeTrue();
        tooLongResult.Error.Code.Should().Be("Device.InvalidName");

        var maxAllowed = new string('X', 100);
        var okResult = device.Rename(maxAllowed);
        okResult.IsSuccess.Should().BeTrue();
        device.Name.Should().Be(maxAllowed);
    }

    [Fact]
    public async Task UpsertDeviceTimers_TerminatesDeterministically_WhenLongIdsCollide()
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(
            tenantId,
            "ESP32-TIMER01",
            "11:22:33:44:55:66",
            "Relay 1",
            "ESP32_RELAY_WIFI",
            "VESIS").Value;

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var attrRepo = new Mock<IDeviceAttributeRepository>();
        attrRepo
            .Setup(r => r.UpsertAsync(
                tenantId,
                device.Id,
                AttributeScope.Shared,
                "timers_json",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid tId, Guid dId, AttributeScope sc, string k, string v, CancellationToken _) =>
                DeviceAttribute.Create(tId, dId, sc, k, v).Value);

        var uow = new Mock<IUnitOfWork>();
        var handler = new UpsertDeviceTimersCommandHandler(
            deviceRepo.Object,
            attrRepo.Object,
            uow.Object);

        const string basePrefix = "tmr_123456789012345"; // 19 chars (> 18 truncation boundary)
        var timers = new List<DeviceTimerItemDto>
        {
            new($"{basePrefix}_3", true, "once", Channel: 1, At: 1893456000, Do: new DeviceTimerActionDto("on")),
            new(basePrefix, true, "once", Channel: 1, At: 1893459600, Do: new DeviceTimerActionDto("off")),
            new(basePrefix, true, "once", Channel: 1, At: 1893463200, Do: new DeviceTimerActionDto("on"))
        };

        var result = await handler.Handle(
            new UpsertDeviceTimersCommand(tenantId, device.Id, timers),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Timers.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(3);
    }
}
