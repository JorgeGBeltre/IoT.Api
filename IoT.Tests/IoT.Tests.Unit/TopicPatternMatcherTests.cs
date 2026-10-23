using FluentAssertions;
using IoT.Protocols.Routing;
using Xunit;

namespace IoT.Tests.Unit;

public class TopicPatternMatcherTests
{
    [Theory]
    [InlineData("devices/+/telemetry", "devices/0000A1B2C3D4/telemetry", true)]
    [InlineData("devices/+/telemetry", "devices/0000A1B2C3D4/state", false)]
    [InlineData("glp/telemetria/+", "glp/telemetria/GLP-001", true)]
    [InlineData("devices/#", "devices/0000A1B2C3D4/commands/response", true)]
    [InlineData("telemetry/+", "telemetry/device123", true)]
    public void MatchesMqttTopic_ShouldEvaluateCorrectly(string pattern, string topic, bool expected)
    {
        bool result = TopicPatternMatcher.MatchesMqttTopic(pattern, topic);
        result.Should().Be(expected);
    }

    [Fact]
    public void TryExtractParameters_WithMatchingTemplate_ShouldExtractDeviceId()
    {
        // Act
        bool matched = TopicPatternMatcher.TryExtractParameters("devices/{deviceId}/telemetry", "devices/ESP32-CHIP99/telemetry", out var parameters);

        // Assert
        matched.Should().BeTrue();
        parameters.Should().ContainKey("deviceId");
        parameters["deviceId"].Should().Be("ESP32-CHIP99");
    }

    [Fact]
    public void TryExtractParameters_WithGlpTemplate_ShouldExtractDeviceId()
    {
        // Act
        bool matched = TopicPatternMatcher.TryExtractParameters("glp/telemetria/{deviceId}", "glp/telemetria/GLP-TANK-42", out var parameters);

        // Assert
        matched.Should().BeTrue();
        parameters.Should().ContainKey("deviceId");
        parameters["deviceId"].Should().Be("GLP-TANK-42");
    }
}
