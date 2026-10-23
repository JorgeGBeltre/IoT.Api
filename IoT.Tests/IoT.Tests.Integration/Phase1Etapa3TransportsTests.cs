using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using IoT.Application.Commands.Commands;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Domain.ValueObjects;
using IoT.Infrastructure.Http;
using IoT.Infrastructure.Mqtt;
using IoT.Infrastructure.Tcp;
using IoT.Protocols.Binary;
using IoT.Protocols.Contracts;
using IoT.Protocols.Profiles;
using IoT.Protocols.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

public class Phase1Etapa3TransportsTests
{
    // =========================================================================
    // E3-01: MQTT Protocol Version v5.0 & MqttPublishOptions
    // =========================================================================

    [Fact]
    public void MqttPublishOptions_HoldsV5Properties()
    {
        var options = new MqttPublishOptions
        {
            Qos = 2,
            Retain = true,
            MessageExpiryInterval = 600,
            ContentType = "application/json",
            ResponseTopic = "devices/response/topic",
            CorrelationData = Encoding.UTF8.GetBytes("req-12345"),
            UserProperties = new Dictionary<string, string> { ["tenant-id"] = "tenant-abc" }
        };

        options.Qos.Should().Be(2);
        options.Retain.Should().BeTrue();
        options.MessageExpiryInterval.Should().Be(600);
        options.ContentType.Should().Be("application/json");
        options.ResponseTopic.Should().Be("devices/response/topic");
        options.CorrelationData.Should().NotBeNull();
        options.UserProperties.Should().ContainKey("tenant-id");
    }

    // =========================================================================
    // E3-02: Message Expiry & Central Topic Pattern Routing
    // =========================================================================

    [Fact]
    public void TopicRoutingService_BuildsCommandTopics_PerDeviceProfile()
    {
        var registry = ProtocolRegistry.CreateDefault();
        var routingService = new TopicRoutingService(registry);

        // GLP device model resolves GLP command pattern
        string glpTopic = routingService.BuildCommandTopic("MQTT-PressureLevelSensor", "GLP-001");
        glpTopic.Should().Be("glp/config/GLP-001");

        // Relay device model resolves Relay switch command pattern
        string relayTopic = routingService.BuildCommandTopic("MQTT-Relay-BLE", "RELAY-99");
        relayTopic.Should().Be("relays/devices/RELAY-99/commands");

        // GPS tracker model resolves GPS command pattern
        string gpsTopic = routingService.BuildCommandTopic("MQTT-GPS-Tracker", "GPS-456");
        gpsTopic.Should().Be("devices/GPS-456/commands");

        // Unmatched/null model defaults to standard pattern
        string defaultTopic = routingService.BuildCommandTopic(null, "DEVICE-UNKNOWN");
        defaultTopic.Should().Be("devices/DEVICE-UNKNOWN/commands");

        // Command expiry defaults to 5 minutes
        routingService.GetDefaultCommandExpiry("MQTT-PressureLevelSensor").Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task SendCommandToDevice_UsesTopicRoutingService_AndSetsMessageExpiry()
    {
        // Arrange
        var deviceRepoMock = new Mock<IDeviceRepository>();
        var commandRepoMock = new Mock<IDeviceCommandRepository>();
        var mqttPublisherMock = new Mock<IMqttPublisher>();
        var uowMock = new Mock<IUnitOfWork>();
        var loggerMock = new Mock<ILogger<SendCommandToDeviceCommandHandler>>();

        var registry = ProtocolRegistry.CreateDefault();
        var routingService = new TopicRoutingService(registry);

        var tenantId = Guid.NewGuid();
        // Create GLP device so routing service matches glp/config/{deviceId}
        var device = Device.Create(tenantId, "GLP-TANK-123", "AA:BB:CC:11:22:33", "GLP Tank 1", "MQTT-PressureLevelSensor", "Custom IoT").Value;

        deviceRepoMock.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        var handler = new SendCommandToDeviceCommandHandler(
            deviceRepoMock.Object,
            commandRepoMock.Object,
            mqttPublisherMock.Object,
            uowMock.Object,
            loggerMock.Object,
            routingService);

        var command = new SendCommandToDeviceCommand(
            tenantId,
            device.Id,
            "VALVE_CLOSE",
            "{\"duration\": 60}",
            "AdminUser");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // Must publish to the GLP-specific topic, NOT hardcoded devices/{chipId}/commands
        mqttPublisherMock.Verify(m => m.PublishAsync(
            "glp/config/GLP-TANK-123",
            It.IsAny<string>(),
            It.Is<MqttPublishOptions>(opt => opt.MessageExpiryInterval == 300 && opt.ContentType == "application/json"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================================
    // E3-03: Subscriptions Routed by Profile Topic Patterns (No '#')
    // =========================================================================

    [Fact]
    public void TopicRoutingService_GetAllTelemetrySubscriptionPatterns_ExcludesRootHashWildcard()
    {
        var registry = ProtocolRegistry.CreateDefault();
        var routingService = new TopicRoutingService(registry);

        var patterns = routingService.GetAllTelemetrySubscriptionPatterns();

        patterns.Should().NotBeEmpty();
        patterns.Should().NotContain("#", "Worker must NEVER subscribe to global '#' root wildcard");

        // Must contain profile-specific routes
        patterns.Should().Contain(p => p.StartsWith("glp/telemetria/"));
        patterns.Should().Contain(p => p.Contains("relays/devices/+/action"));
    }

    // =========================================================================
    // E3-04: HTTP/HTTPS Transport (HttpDeviceTransport)
    // =========================================================================

    [Fact]
    public async Task HttpDeviceTransport_IngestAsync_TranslatesToTransportMessage()
    {
        var loggerMock = new Mock<ILogger<HttpDeviceTransport>>();
        var transport = new HttpDeviceTransport(loggerMock.Object);

        TransportMessage? receivedMessage = null;
        transport.OnMessageReceived = msg =>
        {
            receivedMessage = msg;
            return Task.FromResult(Result.Success());
        };

        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer token123" };
        var result = await transport.IngestAsync(
            route: "telemetry/http-device-1",
            payload: "{\"temp\": 25.5}",
            headers: headers,
            messageId: "http-msg-999");

        result.IsSuccess.Should().BeTrue();
        receivedMessage.Should().NotBeNull();
        receivedMessage!.TopicOrRoute.Should().Be("telemetry/http-device-1");
        receivedMessage.Payload.Should().Be("{\"temp\": 25.5}");
        receivedMessage.MessageId.Should().Be("http-msg-999");
        receivedMessage.TransportProtocol.Should().Be("HTTP");
        receivedMessage.Source.Should().Be("http");
        receivedMessage.Headers.Should().ContainKey("Authorization");
    }

    [Fact]
    public async Task HttpDeviceTransport_PublishCommandAsync_BuffersForPollingDevice()
    {
        var loggerMock = new Mock<ILogger<HttpDeviceTransport>>();
        var transport = new HttpDeviceTransport(loggerMock.Object);

        const string chipId = "HTTP-POLL-01";
        var options = new DeviceTransportOptions(
            MessageExpiry: TimeSpan.FromMinutes(5),
            CorrelationId: "cmd-uuid-001");

        // Act: publish to polling device
        var result = await transport.PublishCommandAsync($"devices/{chipId}/commands", "{\"relay\": 1}", options);
        result.IsSuccess.Should().BeTrue();

        // Device polls for commands
        var polled = transport.PollNextCommand(chipId);
        polled.Should().NotBeNull();
        polled!.CommandId.Should().Be("cmd-uuid-001");
        polled.Payload.Should().Be("{\"relay\": 1}");
        polled.ExpiresAt.Should().BeAfter(DateTime.UtcNow);

        // Next poll returns null (queue emptied)
        var nextPoll = transport.PollNextCommand(chipId);
        nextPoll.Should().BeNull();
    }

    // =========================================================================
    // E3-05: Binary TCP Transport for R12L-F
    // =========================================================================

    [Fact]
    public void R12LFCrc16_ComputesAccurateCrcItu()
    {
        // Test with sample data: 0x05 0x01 0x00 0x01
        byte[] testData = [0x05, 0x01, 0x00, 0x01];
        ushort crc = R12LFCrc16.Compute(testData);

        // Verify CRC is non-zero and deterministic
        crc.Should().NotBe(0);
        R12LFCrc16.Compute(testData).Should().Be(crc);
    }

    [Fact]
    public void R12LFFrame_BuildAck_CreatesExact10ByteResponse()
    {
        const byte protocol = R12LFFrame.ProtocolLogin;
        const ushort serial = 0x0001;

        byte[] ack = R12LFFrame.BuildAck(protocol, serial);

        ack.Length.Should().Be(10);
        ack[0].Should().Be(0x78);
        ack[1].Should().Be(0x78);
        ack[2].Should().Be(0x05); // Packet length
        ack[3].Should().Be(0x01); // Protocol number (Login)
        ack[4].Should().Be(0x00); // Serial Hi
        ack[5].Should().Be(0x01); // Serial Lo
        ack[8].Should().Be(0x0D); // Stop Bit 1 (\r)
        ack[9].Should().Be(0x0A); // Stop Bit 2 (\n)

        // CRC check over bytes 2 to 5
        ushort ackCrc = BinaryPrimitives.ReadUInt16BigEndian(ack.AsSpan(6, 2));
        ushort expectedCrc = R12LFCrc16.Compute(ack.AsSpan(2, 4));
        ackCrc.Should().Be(expectedCrc);
    }

    [Fact]
    public void R12LFFrame_TryParse_ParsesValidLoginPacket_AndExtractsImei()
    {
        // Build valid Login Packet:
        // Start: 0x78 0x78
        // Length: 0x0D (13 bytes: 1 protocol + 8 IMEI + 2 serial + 2 CRC)
        // Protocol: 0x01
        // IMEI (8 bytes): 0x01 0x23 0x45 0x67 0x89 0x01 0x23 0x45 (IMEI 123456789012345)
        // Serial (2 bytes): 0x00 0x01
        // CRC (2 bytes): calculated over [0x0D, 0x01, ... 8 bytes IMEI ..., 0x00, 0x01]
        // Stop: 0x0D 0x0A

        byte[] toHash = [0x0D, 0x01, 0x01, 0x23, 0x45, 0x67, 0x89, 0x01, 0x23, 0x45, 0x00, 0x01];
        ushort crc = R12LFCrc16.Compute(toHash);

        byte[] rawPacket =
        [
            0x78, 0x78,
            0x0D,
            0x01,
            0x01, 0x23, 0x45, 0x67, 0x89, 0x01, 0x23, 0x45,
            0x00, 0x01,
            (byte)(crc >> 8), (byte)(crc & 0xFF),
            0x0D, 0x0A
        ];

        bool parsed = R12LFFrame.TryParse(rawPacket, out var frame, out int consumed);

        parsed.Should().BeTrue();
        consumed.Should().Be(rawPacket.Length);
        frame.Should().NotBeNull();
        frame!.ProtocolNumber.Should().Be(R12LFFrame.ProtocolLogin);
        frame.SerialNumber.Should().Be(1);

        string imei = frame.ExtractImeiFromLoginPacket();
        imei.Should().Be("123456789012345");
    }

    [Fact]
    public void R12LFFrame_DecodeLocationPacket_ParsesGpsCoordinatesCorrectly()
    {
        // Date: 2026-10-22 14:30:00 (26, 10, 22, 14, 30, 00)
        // Satellites: 8 (0x88)
        // Lat: 18.4861 * 1800000 = 33274980 (0x01FB0264)
        // Lon: 69.9312 * 1800000 = 125876160 (0x0780D3C0)
        // Speed: 60 km/h (0x3C)
        // Course/Status: 0x1C00 → Bit4(0x1000) posicionado, Bit3(0x0800) Oeste, Bit2(0x0400) Norte
        // (equipo en República Dominicana: 18.4861 N, -69.9312 O)

        byte[] infoContent =
        [
            26, 10, 22, 14, 30, 0, // YY MM DD HH MM SS
            0x88,                  // satellites
            0x01, 0xFB, 0xBC, 0x64,// Lat: 33274980 / 1800000 = 18.4861
            0x07, 0x80, 0xB7, 0xC0,// Lon: 125876160 / 1800000 = 69.9312
            60,                    // Speed km/h
            0x1C, 0x00             // Status: posicionado + Norte + Oeste
        ];

        var frame = new R12LFFrame
        {
            ProtocolNumber = R12LFFrame.ProtocolLocation,
            InformationContent = infoContent,
            SerialNumber = 42
        };

        var (position, recordedAt) = frame.DecodeLocationPacket();

        position.Should().NotBeNull();
        position!.Latitude.Should().BeApproximately(18.4861, 0.001);
        position.Longitude.Should().BeApproximately(-69.9312, 0.001); // Bit3 (0x0800)=1 → Oeste
        position.SpeedKph.Should().Be(60);
        position.Satellites.Should().Be(8);
        position.IsValid.Should().BeTrue();

        recordedAt.Year.Should().Be(2026);
        recordedAt.Month.Should().Be(10);
        recordedAt.Day.Should().Be(22);
        recordedAt.Hour.Should().Be(14);
        recordedAt.Minute.Should().Be(30);
    }
}
