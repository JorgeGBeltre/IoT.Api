using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Common;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Mqtt;
using IoT.Infrastructure.Services;
using IoT.Protocols;
using IoT.Protocols.Contracts;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// LOW-3 (amplificación de auditoría por trama), LOW-6 (el ACL permitía ota/&lt;yo&gt;/cualquier-cosa).
/// LOW-5 (re-verificación del sha256 antes de emitir) se cubre en FirmwareOtaIntegrationTests.
/// </summary>
public sealed class LowSeverityHardeningTests
{
    // -----------------------------------------------------------------
    // LOW-3: cortafuegos de auditoría de no aprovisionados
    // -----------------------------------------------------------------

    [Fact]
    public void TheThrottleAuditsOncePerIdentifierPerWindow()
    {
        var throttle = new UnprovisionedDeviceAuditThrottle(TimeSpan.FromMinutes(60), maxEntries: 100);

        throttle.ShouldAudit("GHOST-IMEI").Should().BeTrue("la primera vez sí");
        throttle.ShouldAudit("GHOST-IMEI").Should().BeFalse("dentro de la ventana, no");
        throttle.ShouldAudit("GHOST-IMEI").Should().BeFalse();
        // Otro identificador distinto SÍ se audita (no se contagia).
        throttle.ShouldAudit("OTHER-GHOST").Should().BeTrue();
    }

    [Fact]
    public void TheThrottleReAuditsAfterTheWindowElapses()
    {
        // Ventana de cero: cada llamada supera la ventana, así que siempre audita (equivale a
        // "ha pasado el intervalo"). Prueba el camino de re-auditoría sin esperar tiempo real.
        var throttle = new UnprovisionedDeviceAuditThrottle(TimeSpan.Zero, maxEntries: 100);

        throttle.ShouldAudit("GHOST-IMEI").Should().BeTrue();
        throttle.ShouldAudit("GHOST-IMEI").Should().BeTrue("con ventana cero, ya pasó el intervalo");
    }

    [Fact]
    public async Task UnprovisionedFramesFromTheSameDeviceAreAuditedOnlyOnce()
    {
        // Dos tramas de un equipo NO registrado: el rechazo ocurre las dos veces, pero la fila
        // de AuditLog se escribe UNA sola (LOW-3: antes era una por trama).
        var adapter = new Mock<ICompositeProtocolAdapter>();
        adapter.Setup(a => a.Decode(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Result<NormalizedTelemetryEvent>.Success(new NormalizedTelemetryEvent
            {
                DeviceIdentifier = "GHOST-IMEI-0001",
                RecordedAt = DateTime.UtcNow,
                ReceivedAt = DateTime.UtcNow
            }));

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByChipIdGlobalAsync("GHOST-IMEI-0001", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IoT.Domain.Entities.Device?)null); // no aprovisionado

        var cache = new Mock<IStateCacheService>();
        cache.Setup(c => c.IsDuplicateMessageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var audit = new Mock<IAuditLogRepository>();

        var handler = new IngestTelemetryCommandHandler(
            adapter.Object, deviceRepo.Object, new Mock<ITenantRepository>().Object,
            cache.Object, new Mock<ITelemetryStore>().Object, new Mock<IUnitOfWork>().Object,
            new Mock<ILogger<IngestTelemetryCommandHandler>>().Object,
            auditLogRepository: audit.Object,
            unprovisionedAuditThrottle: new UnprovisionedDeviceAuditThrottle(TimeSpan.FromMinutes(60), 100));

        var r1 = await handler.Handle(new IngestTelemetryCommand("telemetry", "{}"), CancellationToken.None);
        var r2 = await handler.Handle(new IngestTelemetryCommand("telemetry", "{}"), CancellationToken.None);

        r1.IsFailure.Should().BeTrue();
        r2.IsFailure.Should().BeTrue();
        audit.Verify(a => a.AddAsync(It.IsAny<IoT.Domain.Entities.AuditLog>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // -----------------------------------------------------------------
    // LOW-6: un dispositivo no puede publicar en ota/<yo>/<cualquier-cosa>
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("ota/A1B2C3D4E5F6/progress")]
    [InlineData("ota/A1B2C3D4E5F6/error")]
    [InlineData("ota/A1B2C3D4E5F6/success")]
    [InlineData("ota/A1B2C3D4E5F6/state")]
    [InlineData("ota/A1B2C3D4E5F6/ack")]
    public void ADeviceMayPublishItsOwnKnownOtaReports(string topic)
    {
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, "A1B2C3D4E5F6")
            .Should().Be(MqttTopicAcl.Decision.Allow);
    }

    [Theory]
    [InlineData("ota/A1B2C3D4E5F6/basura")]       // sufijo desconocido
    [InlineData("ota/A1B2C3D4E5F6/success/extra")] // más de 3 segmentos
    [InlineData("ota/A1B2C3D4E5F6")]               // sin sufijo
    public void ADeviceCannotPublishArbitraryOtaSubtopics(string topic)
    {
        // Antes bastaba "prefijo ota + 3+ segmentos + propiedad", así que ota/<yo>/basura se
        // admitía y recorría el pipeline escribiendo histórico con acción "unknown" (LOW-6).
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, "A1B2C3D4E5F6")
            .Should().Be(MqttTopicAcl.Decision.Deny);
    }
}
