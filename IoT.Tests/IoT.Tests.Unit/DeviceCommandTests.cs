using FluentAssertions;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using Xunit;

namespace IoT.Tests.Unit;

public class DeviceCommandTests
{
    [Fact]
    public void CreateCommand_ShouldInitializeInPendingStatus()
    {
        // Act
        var result = DeviceCommand.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            commandName: "switch:set",
            payloadJson: "{\"state\":\"on\"}",
            requestedBy: "admin@example.do");

        // Assert
        result.IsSuccess.Should().BeTrue();
        var command = result.Value;
        command.Status.Should().Be(CommandStatus.Pending);
        command.SentAt.Should().BeNull();
        command.ConfirmedAt.Should().BeNull();
    }

    [Fact]
    public void CommandLifecycle_PendingToSentToConfirmed_ShouldTransitionCorrectly()
    {
        // Arrange
        var command = DeviceCommand.Create(Guid.NewGuid(), Guid.NewGuid(), "switch:set", "{}", "admin").Value;

        // Act 1: Send
        command.MarkAsSent();
        command.Status.Should().Be(CommandStatus.Sent);
        command.SentAt.Should().NotBeNull();

        // Act 2: Confirm
        command.Confirm("{\"currentState\":\"on\"}");
        command.Status.Should().Be(CommandStatus.Confirmed);
        command.ConfirmedAt.Should().NotBeNull();
        command.ResultJson.Should().Be("{\"currentState\":\"on\"}");
    }
}
