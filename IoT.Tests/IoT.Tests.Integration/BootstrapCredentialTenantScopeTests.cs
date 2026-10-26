using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Domain.ValueObjects;
using IoT.Infrastructure.Security;
using IoT.Protocols;
using IoT.Protocols.Contracts;
using IoT.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// MEDIUM-2: la credencial de bootstrap ya no es global (una lista en configuración, igual
/// para todos los tenants) sino que está ACOTADA A UN TENANT, colgada de la entidad Tenant.
/// El broker resuelve la credencial contra la BD y recuerda a qué tenant pertenece la sesión;
/// la ingesta rechaza toda trama cuyo dispositivo resuelto sea de OTRO tenant.
///
/// Estos tests fijan la frontera donde de verdad se cierra el agujero: la INGESTA. El ACL de
/// tópicos sigue concediendo publicación a la credencial de bootstrap (su MqttTopicAclTests no
/// cambia), porque el ACL es una clase estática sin BD y NO es la frontera de tenant: lo es la
/// comprobación de abajo, en IngestTelemetryCommand, entre resolver el dispositivo y fijar el
/// contexto de tenant.
/// </summary>
public sealed class BootstrapCredentialTenantScopeTests
{
    private static NormalizedTelemetryEvent EventoCon(string chipId) => new()
    {
        DeviceIdentifier = chipId,
        RecordedAt = DateTime.UtcNow,
        ReceivedAt = DateTime.UtcNow,
        Position = GeoPosition.Create(18.48, -69.93, 35, 180, 10).Value
    };

    private static (IngestTelemetryCommandHandler Handler,
                    Mock<IStateCacheService> Cache,
                    Mock<ITelemetryStore> Store,
                    Mock<IUnitOfWork> Uow,
                    WorkerTenantContext TenantContext)
        Armar(Device deviceResuelto, string chipId)
    {
        var adapter = new Mock<ICompositeProtocolAdapter>();
        adapter.Setup(a => a.Decode(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Result<NormalizedTelemetryEvent>.Success(EventoCon(chipId)));

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByChipIdGlobalAsync(chipId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceResuelto);

        var cache = new Mock<IStateCacheService>();
        // Forzado a false EXPLÍCITAMENTE: si fuera duplicado, el handler devolvería éxito
        // antes de resolver el dispositivo y el test pasaría por la razón equivocada.
        cache.Setup(c => c.IsDuplicateMessageAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var store = new Mock<ITelemetryStore>();
        var uow = new Mock<IUnitOfWork>();
        var tenantRepo = new Mock<ITenantRepository>();
        var logger = new Mock<ILogger<IngestTelemetryCommandHandler>>();
        var tenantContext = new WorkerTenantContext();

        var handler = new IngestTelemetryCommandHandler(
            adapter.Object,
            deviceRepo.Object,
            tenantRepo.Object,
            cache.Object,
            store.Object,
            uow.Object,
            logger.Object,
            tenantContext: tenantContext);

        return (handler, cache, store, uow, tenantContext);
    }

    // =====================================================================
    // El test que decide si MEDIUM-2 está cerrado
    // =====================================================================

    [Fact]
    public async Task UnaCredencialDelTenantANoPuedeInyectarTelemetriaDeUnEquipoDelTenantB()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        // ChipIds DISTINTOS a propósito: si fueran el mismo, GetByChipIdGlobalAsync devolvería
        // null por colisión entre tenants y el test pasaría por la razón equivocada.
        var deviceDeB = Device.Create(tenantB, "CHIP-DE-B", "AA:BB:CC:DD:EE:0B", "EquipoB", "ESP32", "Generic").Value;

        var (handler, cache, store, uow, tenantContext) = Armar(deviceDeB, "CHIP-DE-B");

        var result = await handler.Handle(
            new IngestTelemetryCommand("telemetry", "{}", CredentialTenantId: tenantA),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue("la credencial del tenant A no puede hablar por un equipo del tenant B");

        // NADA se escribe: ni cache viva, ni histórico, ni métricas, ni SaveChanges.
        cache.Verify(c => c.TryUpdateLatestPositionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<GeoPosition>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.StorePositionAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GeoPosition>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.StoreTelemetryMetricsAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<Dictionary<string, double>>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);

        // Y el contexto de tenant NO se movió de Guid.Empty: es lo que detecta el error de
        // escribir el rechazo DESPUÉS de fijar el tenant (ahí se compararía A contra A).
        tenantContext.TenantId.Should().Be(Guid.Empty);
    }

    // =====================================================================
    // Control positivo: sin esto, el test de arriba podría pasar porque el arreglo rechaza TODO
    // =====================================================================

    [Fact]
    public async Task UnaCredencialDelTenantASiPuedePublicarPorUnEquipoDelTenantA()
    {
        var tenantA = Guid.NewGuid();
        var deviceDeA = Device.Create(tenantA, "CHIP-DE-A", "AA:BB:CC:DD:EE:0A", "EquipoA", "ESP32", "Generic").Value;

        var (handler, _, store, _, tenantContext) = Armar(deviceDeA, "CHIP-DE-A");

        var result = await handler.Handle(
            new IngestTelemetryCommand("telemetry", "{}", CredentialTenantId: tenantA),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        store.Verify(s => s.StorePositionAsync(
            tenantA, deviceDeA.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GeoPosition>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        tenantContext.TenantId.Should().Be(tenantA);
    }

    // =====================================================================
    // No-regresión: las vías SIN credencial acotada (HTTP, webhook, TCP, worker) siguen ingiriendo
    // =====================================================================

    [Fact]
    public async Task SinCredencialAcotadaLaIngestaNoRechazaPorTenant()
    {
        // Es el modo de ruptura más probable del arreglo: tratar "null = rechazar" tumbaría
        // HTTP/webhook/los dos gateways TCP/worker, que no tienen credencial de bootstrap y
        // hoy no tienen ni un test extremo-a-extremo que lo detecte.
        var tenant = Guid.NewGuid();
        var device = Device.Create(tenant, "CHIP-HTTP", "AA:BB:CC:DD:EE:0C", "EquipoHttp", "ESP32", "Generic").Value;

        var (handler, _, store, _, tenantContext) = Armar(device, "CHIP-HTTP");

        var result = await handler.Handle(
            new IngestTelemetryCommand("telemetry", "{}", CredentialTenantId: null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        store.Verify(s => s.StorePositionAsync(
            tenant, device.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GeoPosition>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        tenantContext.TenantId.Should().Be(tenant);
    }

    // =====================================================================
    // Dominio y servicio: las piezas de las que depende la resolución por tenant
    // =====================================================================

    [Fact]
    public void ElTenantGuardaYRevocaSuCredencialDeBootstrap()
    {
        var tenant = Tenant.CreateWithId(Guid.NewGuid(), "Prueba", "PRUEBA").Value;
        tenant.HasBootstrapApiKey.Should().BeFalse();

        tenant.SetBootstrapApiKey("hash-largo", "huella01", "admin").IsSuccess.Should().BeTrue();
        tenant.HasBootstrapApiKey.Should().BeTrue();
        tenant.BootstrapApiKeyFingerprint.Should().Be("huella01");
        tenant.BootstrapApiKeyIssuedBy.Should().Be("admin");

        tenant.ClearBootstrapApiKey();
        tenant.HasBootstrapApiKey.Should().BeFalse();
        tenant.BootstrapApiKeyHash.Should().BeNull();
    }

    [Fact]
    public void ElHashEsDeterministaYDistinguePorClave()
    {
        var svc = new DeviceApiKeyService();
        // Determinista: la misma clave da el mismo hash, que es lo que permite BUSCAR el tenant
        // por él. Es la base de la resolución indexada del broker en el CONNECT.
        svc.ComputeHash("clave-del-tenant-a").Should().Be(svc.ComputeHash("clave-del-tenant-a"));
        // Dos claves distintas no colisionan: cada tenant tiene su hash.
        svc.ComputeHash("clave-del-tenant-a").Should().NotBe(svc.ComputeHash("clave-del-tenant-b"));
    }

    // =====================================================================
    // Forma de la configuración: detecta un binding roto de BootstrapCredentials
    // =====================================================================

    [Fact]
    public void LaSeccionBootstrapCredentialsBindeaComoListaDeTenantYClave()
    {
        // Si la sección pasara a otra forma (p.ej. lista plana de cadenas) el binding
        // devolvería vacío EN SILENCIO y toda credencial de bootstrap quedaría sin resolver,
        // con el simulador sin conectar y el síntoma atribuido al arreglo equivocado.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MqttBroker:BootstrapCredentials:0:TenantCode"] = "CARIBE-LOG",
                ["MqttBroker:BootstrapCredentials:0:ApiKey"] = "clave-caribe",
                ["MqttBroker:BootstrapCredentials:1:TenantCode"] = "DOM-GLP",
                ["MqttBroker:BootstrapCredentials:1:ApiKey"] = "clave-glp"
            })
            .Build();

        var bound = config.GetSection("MqttBroker:BootstrapCredentials").Get<List<BindProbe>>();

        bound.Should().NotBeNull();
        var list = bound!;
        list.Should().HaveCount(2);
        list.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.TenantCode) && !string.IsNullOrWhiteSpace(c.ApiKey));
        list[0].TenantCode.Should().Be("CARIBE-LOG");
    }

    private sealed class BindProbe
    {
        public string? TenantCode { get; set; }
        public string? ApiKey { get; set; }
    }
}
