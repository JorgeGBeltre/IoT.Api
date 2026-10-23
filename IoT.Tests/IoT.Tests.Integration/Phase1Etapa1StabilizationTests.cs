using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using IoT.Api.Controllers;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Domain.ValueObjects;
using IoT.Infrastructure.Security;
using IoT.Protocols;
using IoT.Protocols.Contracts;
using IoT.Worker;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

public class Phase1Etapa1StabilizationTests
{
    // =========================================================================
    // E1-05: Clock Skew & Stale Timestamp Handling Tests
    // =========================================================================

    [Fact]
    public async Task Ingestion_RecordedAtMoreThan5MinutesInFuture_ReturnsClockSkewFailure()
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
        var device = Device.Create(tenantId, "CHIP-SKEW-01", "AA:BB:CC:DD:EE:01", "SkewDevice", "ESP32", "Generic").Value;

        var futureTimestamp = DateTime.UtcNow.AddMinutes(10);
        var telemetryEvent = new NormalizedTelemetryEvent
        {
            DeviceIdentifier = "CHIP-SKEW-01",
            RecordedAt = futureTimestamp,
            ReceivedAt = DateTime.UtcNow,
            Position = GeoPosition.Create(18.48, -69.93, 40, 90, 12).Value
        };

        adapterMock.Setup(a => a.Decode(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Result<NormalizedTelemetryEvent>.Success(telemetryEvent));

        deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("CHIP-SKEW-01", It.IsAny<CancellationToken>()))
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
        var result = await handler.Handle(new IngestTelemetryCommand("devices/telemetry", "{}"), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Ingestion.ClockSkewTooFarInFuture");

        // Verify that live cache and db were NEVER updated
        stateCacheMock.Verify(c => c.TryUpdateLatestPositionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<GeoPosition>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        telemetryStoreMock.Verify(s => s.StorePositionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GeoPosition>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ingestion_StaleHistoricalTimestamp_StoresInPostgres_SkipsRedisLiveCache()
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
        var device = Device.Create(tenantId, "CHIP-STALE-01", "AA:BB:CC:DD:EE:02", "StaleDevice", "ESP32", "Generic").Value;

        var staleTimestamp = DateTime.UtcNow.AddDays(-10); // 10 days in past (> 7 days)
        var telemetryEvent = new NormalizedTelemetryEvent
        {
            DeviceIdentifier = "CHIP-STALE-01",
            RecordedAt = staleTimestamp,
            ReceivedAt = DateTime.UtcNow,
            Position = GeoPosition.Create(18.48, -69.93, 35, 180, 10).Value
        };

        adapterMock.Setup(a => a.Decode(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Result<NormalizedTelemetryEvent>.Success(telemetryEvent));

        deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("CHIP-STALE-01", It.IsAny<CancellationToken>()))
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
        var result = await handler.Handle(new IngestTelemetryCommand("devices/telemetry", "{}"), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // Must NOT update live Redis cache because data is stale historical
        stateCacheMock.Verify(c => c.TryUpdateLatestPositionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<GeoPosition>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);

        // MUST store in PostgreSQL historical table
        telemetryStoreMock.Verify(s => s.StorePositionAsync(
            tenantId, device.Id, staleTimestamp, It.IsAny<DateTime>(), telemetryEvent.Position, 1, "real", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================================
    // E1-04: Worker Tenant Context & Global Lookup Binding
    // =========================================================================

    [Fact]
    public async Task Ingestion_ResolvesDevice_SetsTenantContextToDeviceTenant()
    {
        // Arrange
        var adapterMock = new Mock<ICompositeProtocolAdapter>();
        var deviceRepoMock = new Mock<IDeviceRepository>();
        var tenantRepoMock = new Mock<ITenantRepository>();
        var stateCacheMock = new Mock<IStateCacheService>();
        var telemetryStoreMock = new Mock<ITelemetryStore>();
        var uowMock = new Mock<IUnitOfWork>();
        var loggerMock = new Mock<ILogger<IngestTelemetryCommandHandler>>();

        var expectedTenantId = Guid.NewGuid();
        var device = Device.Create(expectedTenantId, "CHIP-TENANT-01", "AA:BB:CC:DD:EE:03", "TenantDevice", "ESP32", "Generic").Value;

        var telemetryEvent = new NormalizedTelemetryEvent
        {
            DeviceIdentifier = "CHIP-TENANT-01",
            RecordedAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow
        };

        adapterMock.Setup(a => a.Decode(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Result<NormalizedTelemetryEvent>.Success(telemetryEvent));

        deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("CHIP-TENANT-01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        var workerTenantContext = new WorkerTenantContext();
        workerTenantContext.TenantId.Should().Be(Guid.Empty); // initially empty

        var handler = new IngestTelemetryCommandHandler(
            adapterMock.Object,
            deviceRepoMock.Object,
            tenantRepoMock.Object,
            stateCacheMock.Object,
            telemetryStoreMock.Object,
            uowMock.Object,
            loggerMock.Object,
            tenantContext: workerTenantContext);

        // Act
        var result = await handler.Handle(new IngestTelemetryCommand("devices/telemetry", "{}"), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        workerTenantContext.TenantId.Should().Be(expectedTenantId);
    }

    // =========================================================================
    // E1-06: JWT Expiration Configuration Tests
    // =========================================================================

    [Fact]
    public void JwtTokenGenerator_DefaultExpiration_ExpiresInTwoHours()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "ThisIsAValidLongSecretKeyForJwt2026EnterprisePlatform!"
            })
            .Build();

        var generator = new JwtTokenGenerator(config);
        var user = User.Create(Guid.NewGuid(), "operator", "op@test.com", "hash", "Operator", "Operator").Value;

        // Act
        var tokenResult = generator.GenerateToken(user, Guid.NewGuid());

        // Assert
        tokenResult.Should().NotBeNull();
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenResult.Token);

        // Default should be ~2 hours from now (not 7 days)
        var duration = jwt.ValidTo - DateTime.UtcNow;
        duration.TotalHours.Should().BeInRange(1.8, 2.2);
    }

    [Fact]
    public void JwtTokenGenerator_ExpirationHoursConfigured_Respected()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "ThisIsAValidLongSecretKeyForJwt2026EnterprisePlatform!",
                ["Jwt:ExpirationHours"] = "4"
            })
            .Build();

        var generator = new JwtTokenGenerator(config);
        var user = User.Create(Guid.NewGuid(), "operator", "op@test.com", "hash", "Operator", "Operator").Value;

        // Act
        var tokenResult = generator.GenerateToken(user, Guid.NewGuid());

        // Assert
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenResult.Token);

        var duration = jwt.ValidTo - DateTime.UtcNow;
        duration.TotalHours.Should().BeInRange(3.8, 4.2);
    }

    // =========================================================================
    // E1-07: Rate Limiting Annotation Verification
    // =========================================================================

    [Fact]
    public void AuthController_IsDecoratedWithEnableRateLimiting_AuthRateLimit()
    {
        var attr = typeof(AuthController).GetCustomAttribute<EnableRateLimitingAttribute>();

        attr.Should().NotBeNull();
        attr!.PolicyName.Should().Be("AuthRateLimit");
    }
}
