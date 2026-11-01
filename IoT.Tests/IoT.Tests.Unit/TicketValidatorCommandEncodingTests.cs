using System.Text.Json;
using FluentAssertions;
using IoT.Protocols.Profiles;
using IoT.Protocols.Routing;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

/// <summary>
/// Codificación de comandos de bajada para el firmware de flota (Toour/Fligo), cuyo CommandParser
/// sólo acepta el sobre <c>{"EventType":...,"Details":...}</c>. Verifica el mapeo nombre→EventType
/// del perfil y que el routing lo expone sólo para los perfiles que lo definen (el resto usa el
/// envelope genérico de la plataforma).
/// </summary>
public sealed class TicketValidatorCommandEncodingTests
{
    private static readonly TicketValidatorProfile Profile = new();

    [Theory]
    [InlineData("GET_STATUS", "GET_STATUS")]
    [InlineData("status", "GET_STATUS")]        // alias, case-insensitive
    [InlineData("REBOOT", "REBOOT")]
    [InlineData("restart", "REBOOT")]
    [InlineData("GET_TELEMETRY", "GET_TELEMETRY")]
    [InlineData("ota", "UpdateFirmwareDevice")]
    public void EncodeCommandPayload_KnownCommand_EmitsEventTypeEnvelopeWithEmptyDetails(string command, string expectedEventType)
    {
        var json = Profile.EncodeCommandPayload(command, null);

        json.Should().NotBeNull();
        using var doc = JsonDocument.Parse(json!);
        doc.RootElement.GetProperty("EventType").GetString().Should().Be(expectedEventType);
        doc.RootElement.GetProperty("Details").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public void EncodeCommandPayload_PassesThroughPayloadAsDetails()
    {
        var json = Profile.EncodeCommandPayload("TicketValidationResult", "{\"IsValid\":true,\"Message\":\"ok\"}");

        json.Should().NotBeNull();
        using var doc = JsonDocument.Parse(json!);
        doc.RootElement.GetProperty("EventType").GetString().Should().Be("TicketValidationResult");
        var details = doc.RootElement.GetProperty("Details");
        details.GetProperty("IsValid").GetBoolean().Should().BeTrue();
        details.GetProperty("Message").GetString().Should().Be("ok");
    }

    [Theory]
    [InlineData("FOO")]            // no reconocido
    [InlineData("")]
    [InlineData("   ")]
    public void EncodeCommandPayload_UnknownOrBlank_ReturnsNull_SoPlatformUsesGenericEnvelope(string command)
    {
        Profile.EncodeCommandPayload(command, null).Should().BeNull();
    }

    [Fact]
    public void EncodeCommandPayload_InvalidPayloadJson_FallsBackToEmptyDetails()
    {
        var json = Profile.EncodeCommandPayload("GET_STATUS", "no-es-json {");

        json.Should().NotBeNull();
        using var doc = JsonDocument.Parse(json!);
        doc.RootElement.GetProperty("Details").ValueKind.Should().Be(JsonValueKind.Object);
        doc.RootElement.GetProperty("Details").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void RoutingService_BuildCommandPayload_UsesProfileEncoderForTicketValidator()
    {
        var routing = new TopicRoutingService(ProtocolRegistry.CreateDefault());

        // Por modelo del firmware de flota -> sobre EventType.
        var payload = routing.BuildCommandPayload("MQTT-LTE-TicketValidator", "GET_STATUS");
        payload.Should().NotBeNull();
        using var doc = JsonDocument.Parse(payload!);
        doc.RootElement.GetProperty("EventType").GetString().Should().Be("GET_STATUS");

        // Y el tópico de bajada es el que el firmware escucha.
        routing.BuildCommandTopic("MQTT-LTE-TicketValidator", "IMEI123").Should().Be("event/IMEI123");
    }

    [Theory]
    [InlineData("MQTT-GPS-Tracker")]   // perfil sin codificación propia -> envelope genérico
    [InlineData("modelo-inexistente")]
    [InlineData(null)]
    public void RoutingService_BuildCommandPayload_ReturnsNullForProfilesWithoutCustomEncoder(string? model)
    {
        var routing = new TopicRoutingService(ProtocolRegistry.CreateDefault());
        routing.BuildCommandPayload(model, "GET_STATUS").Should().BeNull();
    }
}
