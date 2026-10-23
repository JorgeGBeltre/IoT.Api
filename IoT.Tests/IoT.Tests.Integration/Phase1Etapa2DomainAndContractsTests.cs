using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Domain.ValueObjects;
using IoT.Infrastructure.Mqtt;
using IoT.Infrastructure.Persistence;
using IoT.Infrastructure.Persistence.Entities;
using IoT.Protocols;
using IoT.Protocols.Adapters;
using IoT.Protocols.Contracts;
using IoT.Protocols.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

public class Phase1Etapa2DomainAndContractsTests
{
    // =========================================================================
    // E2-01: Transport / Adapter Separation & IDeviceTransport
    // =========================================================================

    [Fact]
    public void TransportMessage_Envelope_IntegrityAndDefaults()
    {
        var headers = new Dictionary<string, string> { ["device-mac"] = "AA:BB:CC:DD:EE:FF" };
        var message = TransportMessage.Create(
            topicOrRoute: "devices/alpha/telemetry",
            payload: "{\"status\":\"ok\"}",
            messageId: "msg-explicit-12345",
            headers: headers,
            transportProtocol: "MQTT",
            source: "real");

        message.TopicOrRoute.Should().Be("devices/alpha/telemetry");
        message.Payload.Should().Be("{\"status\":\"ok\"}");
        message.MessageId.Should().Be("msg-explicit-12345");
        message.TransportProtocol.Should().Be("MQTT");
        message.Source.Should().Be("real");
        message.Headers.Should().ContainKey("device-mac");
        message.Headers["device-mac"].Should().Be("AA:BB:CC:DD:EE:FF");
        message.ReceivedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void TransportMessage_Create_LeavesMessageIdNullWhenOmitted()
    {
        // Contrato corregido: si el protocolo no trae MessageId se deja NULL para que el
        // pipeline calcule un hash determinista (device+tiempo+payload). Autogenerar un GUID
        // aquí anulaba la deduplicación (cada mensaje parecía único).
        var message = TransportMessage.Create("telemetry", "{}");
        message.MessageId.Should().BeNull();

        // Cuando el protocolo SÍ lo trae, se respeta.
        var withId = TransportMessage.Create("telemetry", "{}", messageId: "dev-msg-1");
        withId.MessageId.Should().Be("dev-msg-1");
    }

    [Fact]
    public async Task MqttDeviceTransport_ImplementsIDeviceTransportAndIMqttPublisher()
    {
        var configMock = new Mock<IConfiguration>();
        var loggerMock = new Mock<ILogger<MqttDeviceTransport>>();

        await using var transport = new MqttDeviceTransport(configMock.Object, loggerMock.Object);

        transport.Should().BeAssignableTo<IDeviceTransport>();
        transport.Should().BeAssignableTo<IMqttPublisher>();
        transport.TransportName.Should().Be("MQTT");
        transport.IsConnected.Should().BeFalse();
    }

    // =========================================================================
    // E2-02: Declarative Device Profiles & Registry
    // =========================================================================

    [Fact]
    public void ProtocolRegistry_ResolvesDeclarativeProfiles_ByTopicPatterns()
    {
        var profiles = new IDeviceProfile[]
        {
            new GlpPressureProfile(),
            new GpsTrackerProfile(),
            new RelaySwitchProfile()
        };

        var registry = new ProtocolRegistry(profiles);

        // 1. GLP topics
        var glpProfile = registry.FindProfileForTopic("glp/telemetria/GLP-TANK-01");
        glpProfile.Should().NotBeNull();
        glpProfile!.ProfileId.Should().Be("pressure-level-sensor-v1");
        glpProfile.SupportedCapabilities.Should().Contain(CapabilityType.Pressure);
        glpProfile.SupportedCapabilities.Should().Contain(CapabilityType.LevelTelemetry);

        // 2. GPS topics
        var gpsProfile1 = registry.FindProfileForTopic("telemetry");
        gpsProfile1.Should().NotBeNull();
        gpsProfile1!.ProfileId.Should().Be("mqtt-gps-tracker");
        gpsProfile1.SupportedCapabilities.Should().Contain(CapabilityType.Gps);

        var gpsProfile2 = registry.FindProfileForTopic("devices/CHIP123/gps");
        gpsProfile2.Should().NotBeNull();
        gpsProfile2!.ProfileId.Should().Be("mqtt-gps-tracker");

        // 3. Relay topics
        var relayProfile = registry.FindProfileForTopic("relays/devices/RELAY01/action");
        relayProfile.Should().NotBeNull();
        relayProfile!.ProfileId.Should().Be("relay-switch");
        relayProfile.SupportedCapabilities.Should().Contain(CapabilityType.Switch);

        // 4. Unknown topic returns null
        var unknown = registry.FindProfileForTopic("unregistered/path/device");
        unknown.Should().BeNull();
    }

    [Fact]
    public void CompositeProtocolAdapter_WithRegistry_RoutesPayloadsDeclaratively()
    {
        var profiles = new IDeviceProfile[]
        {
            new GlpPressureProfile(),
            new GpsTrackerProfile(),
            new RelaySwitchProfile()
        };
        var registry = new ProtocolRegistry(profiles);
        var adapter = new CompositeProtocolAdapter(registry);

        // Test GLP payload decoding through registry
        string glpJson = """
        {
          "Timestamp": "2026-10-22T08:00:00Z",
          "Details": {
            "sensors_filtered": {
              "pressure_bar": 5.8,
              "temp_celsius": 26.0,
              "level_mm": 600.0
            },
            "volume": {
              "percentage": 75.0
            }
          }
        }
        """;

        var glpResult = adapter.Decode("glp/telemetria/GLP-TANK-AUTO", glpJson);
        glpResult.IsSuccess.Should().BeTrue();
        glpResult.Value.DeviceIdentifier.Should().Be("GLP-TANK-AUTO");
        glpResult.Value.NumericMetrics["pressure_bar"].Should().Be(5.8);

        // Test GPS payload decoding through registry
        string gpsJson = """
        {
          "Device": { "ChipId": "GPS-REG-TEST" },
          "Timestamp": "2026-10-22T08:05:00Z",
          "Details": {
            "accident": {
              "gps_data": { "latitude": 18.4861, "longitude": -69.9312 }
            }
          }
        }
        """;

        var gpsResult = adapter.Decode("devices/GPS-REG-TEST/gps", gpsJson);
        gpsResult.IsSuccess.Should().BeTrue();
        gpsResult.Value.DeviceIdentifier.Should().Be("GPS-REG-TEST");
        gpsResult.Value.Position.Should().NotBeNull();
        gpsResult.Value.Position!.Latitude.Should().Be(18.4861);
    }

    // =========================================================================
    // E2-03: Cryptographic SHA-256 Deduplication & Deterministic MessageId
    // =========================================================================

    [Fact]
    public async Task Ingestion_GeneratesDeterministicSha256MessageId_WhenNoneInPayload()
    {
        // Arrange
        var adapterMock = new Mock<ICompositeProtocolAdapter>();
        var deviceRepoMock = new Mock<IDeviceRepository>();
        var tenantRepoMock = new Mock<ITenantRepository>();
        var stateCacheMock = new Mock<IStateCacheService>();
        var telemetryStoreMock = new Mock<ITelemetryStore>();
        var uowMock = new Mock<IUnitOfWork>();
        var loggerMock = new Mock<ILogger<IngestTelemetryCommandHandler>>();

        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "CHIP-SHA256-01", "AA:BB:CC:11:22:33", "ShaDevice", "ESP32", "Generic").Value;

        var recordedAt = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        string rawPayload = "{\"temperature\": 24.5}";

        var telemetryEvent = new NormalizedTelemetryEvent
        {
            DeviceIdentifier = "CHIP-SHA256-01",
            RecordedAt = recordedAt,
            ReceivedAt = DateTime.UtcNow,
            RawPayload = rawPayload,
            NumericMetrics = new Dictionary<string, double> { ["temperature"] = 24.5 },
            Position = GeoPosition.Create(18.48, -69.93, 30, 90, 5).Value
        };

        adapterMock.Setup(a => a.Decode(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Result<NormalizedTelemetryEvent>.Success(telemetryEvent));

        deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("CHIP-SHA256-01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        var handler = new IngestTelemetryCommandHandler(
            adapterMock.Object,
            deviceRepoMock.Object,
            tenantRepoMock.Object,
            stateCacheMock.Object,
            telemetryStoreMock.Object,
            uowMock.Object,
            loggerMock.Object);

        // Act
        var result = await handler.Handle(new IngestTelemetryCommand("devices/telemetry", rawPayload), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // Calculate expected SHA-256
        string expectedInput = $"CHIP-SHA256-01:{recordedAt.Ticks}:{rawPayload}";
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(expectedInput));
        string expectedSha256Hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        expectedSha256Hex.Length.Should().Be(64);

        // Verify that StorePositionAsync received this exact SHA-256 MessageId
        telemetryStoreMock.Verify(s => s.StorePositionAsync(
            tenantId,
            device.Id,
            recordedAt,
            It.IsAny<DateTime>(),
            telemetryEvent.Position,
            1,
            "real",
            expectedSha256Hex,
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify that StoreTelemetryMetricsAsync received this exact SHA-256 MessageId
        telemetryStoreMock.Verify(s => s.StoreTelemetryMetricsAsync(
            tenantId,
            device.Id,
            recordedAt,
            It.IsAny<DateTime>(),
            telemetryEvent.NumericMetrics,
            rawPayload,
            "real",
            expectedSha256Hex,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================================
    // E2-04: Multi-Tenant Compound Uniqueness Index on Persistence Entities
    // =========================================================================

    [Fact]
    public void EFCore_ModelConfiguration_HasMessageIdAndCompoundIndices()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=test_metadata_db;Username=dummy;Password=dummy", o => o.UseNetTopologySuite())
            .Options;

        var tenantContextMock = new Mock<ITenantContext>();
        tenantContextMock.Setup(c => c.TenantId).Returns(Guid.NewGuid());

        using var context = new ApplicationDbContext(options, tenantContextMock.Object);
        var model = context.Model;

        // 1. PositionEntity
        var positionEntity = model.FindEntityType(typeof(PositionEntity));
        positionEntity.Should().NotBeNull();
        var posMsgIdProp = positionEntity!.FindProperty(nameof(PositionEntity.MessageId));
        posMsgIdProp.Should().NotBeNull();
        posMsgIdProp!.GetMaxLength().Should().Be(128);

        // Verify compound index: (TenantId, DeviceId, MessageId)
        var posIndices = positionEntity.GetIndexes().ToList();
        var compoundPosIdx = posIndices.FirstOrDefault(idx =>
            idx.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "DeviceId", "MessageId" }));
        compoundPosIdx.Should().NotBeNull("PositionEntity must have a compound index on (TenantId, DeviceId, MessageId)");

        // 2. TelemetryRecordEntity
        var telemetryEntity = model.FindEntityType(typeof(TelemetryRecordEntity));
        telemetryEntity.Should().NotBeNull();
        var telMsgIdProp = telemetryEntity!.FindProperty(nameof(TelemetryRecordEntity.MessageId));
        telMsgIdProp.Should().NotBeNull();
        telMsgIdProp!.GetMaxLength().Should().Be(128);

        // Verify compound index: (TenantId, DeviceId, MessageId)
        var telIndices = telemetryEntity.GetIndexes().ToList();
        var compoundTelIdx = telIndices.FirstOrDefault(idx =>
            idx.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "DeviceId", "MessageId" }));
        compoundTelIdx.Should().NotBeNull("TelemetryRecordEntity must have a compound index on (TenantId, DeviceId, MessageId)");
    }

    // =========================================================================
    // E2-05: Audited Discovery of Unprovisioned Devices (Fail-Closed)
    // =========================================================================

    [Fact]
    public async Task Ingestion_UnprovisionedDevice_GeneratesAuditLog_AndReturnsDeviceNotProvisionedFailure()
    {
        // Arrange
        var adapterMock = new Mock<ICompositeProtocolAdapter>();
        var deviceRepoMock = new Mock<IDeviceRepository>();
        var tenantRepoMock = new Mock<ITenantRepository>();
        var stateCacheMock = new Mock<IStateCacheService>();
        var telemetryStoreMock = new Mock<ITelemetryStore>();
        var uowMock = new Mock<IUnitOfWork>();
        var auditRepoMock = new Mock<IAuditLogRepository>();
        var loggerMock = new Mock<ILogger<IngestTelemetryCommandHandler>>();

        const string unprovisionedChipId = "UNKNOWN-CHIP-ROGUE-99";
        var telemetryEvent = new NormalizedTelemetryEvent
        {
            DeviceIdentifier = unprovisionedChipId,
            RecordedAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow
        };

        adapterMock.Setup(a => a.Decode(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Result<NormalizedTelemetryEvent>.Success(telemetryEvent));

        // Device is NOT found in the database
        deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync(unprovisionedChipId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Device?)null);

        var handler = new IngestTelemetryCommandHandler(
            adapterMock.Object,
            deviceRepoMock.Object,
            tenantRepoMock.Object,
            stateCacheMock.Object,
            telemetryStoreMock.Object,
            uowMock.Object,
            loggerMock.Object,
            notificationService: null,
            tenantContext: null,
            auditLogRepository: auditRepoMock.Object);

        // Act
        var result = await handler.Handle(
            new IngestTelemetryCommand("devices/telemetry", "{\"raw\":\"payload\"}"),
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Ingestion.DeviceNotProvisioned");

        // 1. Audit log was added with action "DeviceDiscovered"
        auditRepoMock.Verify(a => a.AddAsync(
            It.Is<AuditLog>(log =>
                log.Action == "DeviceDiscovered" &&
                log.Username == "System.Ingestion" &&
                log.Details.Contains(unprovisionedChipId) &&
                log.TenantId == Guid.Empty),
            It.IsAny<CancellationToken>()), Times.Once);

        // 2. Unit of work saved the audit record
        uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        // 3. No tenant was auto-created
        tenantRepoMock.Verify(t => t.AddAsync(It.IsAny<Tenant>(), It.IsAny<CancellationToken>()), Times.Never);

        // 4. No device was auto-created
        deviceRepoMock.Verify(d => d.AddAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()), Times.Never);

        // 5. Telemetry was never stored
        telemetryStoreMock.Verify(s => s.StorePositionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.IsAny<GeoPosition>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
