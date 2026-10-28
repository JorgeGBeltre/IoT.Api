using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Application.Protocols;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Domain.ValueObjects;
using IoT.Protocols;
using IoT.Protocols.Contracts;
using IoT.Protocols.Dynamic;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Unit;

/// <summary>
/// Costura de convertidor dinámico por tenant dentro de la ingesta (TB-2): cuando el tenant tiene un
/// script habilitado para el perfil/protocolo, la decodificación de contenido la hace el script; si
/// no, el adaptador nativo; y si el script falla, se cae al nativo (FAIL-SAFE) sin tumbar la ingesta.
/// </summary>
public sealed class IngestTelemetryConverterOverrideTests
{
    private readonly Mock<IDeviceRepository> _deviceRepo = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<IStateCacheService> _stateCache = new();
    private readonly Mock<ITelemetryStore> _store = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<ILogger<IngestTelemetryCommandHandler>> _logger = new();
    private readonly ICompositeProtocolAdapter _nativeAdapter = CompositeProtocolAdapter.CreateDefault();

    private const string NativeGpsPayload = """
    { "Device": { "ChipId": "GPS-ESP8266-99" },
      "Timestamp": "2026-10-21T10:00:00Z",
      "Details": { "accident": { "gps_data": { "latitude": 18.4861, "longitude": -69.9312, "speed": 60.0, "is_valid": true } } } }
    """;

    private IngestTelemetryCommandHandler BuildHandler(ITenantConverterResolver? resolver)
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "GPS-ESP8266-99", "AA:BB:CC", "Unit 99", "ESP8266", "Generic").Value;
        _deviceRepo.Setup(r => r.GetByChipIdGlobalAsync("GPS-ESP8266-99", It.IsAny<CancellationToken>())).ReturnsAsync(device);
        _stateCache.Setup(c => c.IsDuplicateMessageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _stateCache.Setup(c => c.TryUpdateLatestPositionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<GeoPosition>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        return new IngestTelemetryCommandHandler(
            _nativeAdapter, _deviceRepo.Object, _tenantRepo.Object, _stateCache.Object, _store.Object,
            _uow.Object, _logger.Object, converterResolver: resolver);
    }

    private static ITenantConverterResolver FakeResolver(string uplinkScript) =>
        new InlineResolver(new ProtocolConnector("tenant-script:test", "DYNAMIC", new ScriptableUplinkConverter("test", uplinkScript)));

    [Fact]
    public async Task Handle_WithTenantScript_DecodesContentViaScript_NotNative()
    {
        // El script ignora los datos GPS nativos y produce SÓLO una métrica 'foo' sin posición.
        const string script = @"
            function decode(payload, topic, metadata) {
                return { deviceId: 'IGNORED', telemetry: { foo: 7 } };
            }";
        var handler = BuildHandler(FakeResolver(script));

        var result = await handler.Handle(
            new IngestTelemetryCommand("telemetry", NativeGpsPayload, TransportProtocol: "MQTT"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // Contenido del script: métrica foo=7, sin posición → no se almacena posición.
        _store.Verify(s => s.StoreTelemetryMetricsAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.Is<Dictionary<string, double>>(m => m.ContainsKey("foo") && m["foo"] == 7 && !m.ContainsKey("speed")),
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.StorePositionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.IsAny<GeoPosition>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenScriptThrows_FallsBackToNativeDecode()
    {
        // Script que lanza SIEMPRE: la ingesta NO debe caerse; conserva el decode nativo (con posición).
        const string throwing = @"function decode(payload, topic, metadata) { throw new Error('boom'); }";
        var handler = BuildHandler(FakeResolver(throwing));

        var result = await handler.Handle(
            new IngestTelemetryCommand("telemetry", NativeGpsPayload, TransportProtocol: "MQTT"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("un script roto no debe tumbar la ingesta (fail-safe)");
        _store.Verify(s => s.StorePositionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.Is<GeoPosition>(p => p.Latitude == 18.4861), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once,
            "al fallar el script se usa el decode nativo, que trae la posición GPS");
    }

    [Fact]
    public async Task Handle_WhenScriptRecursesWithoutBound_FallsBackToNativeDecode()
    {
        // decode() recursivo sin fondo: con LimitRecursion del sandbox, Jint lanza
        // RecursionDepthOverflowException (capturable) en vez de un StackOverflow no capturable. El
        // convertidor la traduce a un fallo controlado y la ingesta NO se cae: conserva el decode
        // nativo (con la posición GPS). Valida el fail-safe de la ruta caliente de ingesta.
        const string recursive = @"function decode(payload, topic, metadata) { return decode(payload, topic, metadata); }";
        var handler = BuildHandler(FakeResolver(recursive));

        var result = await handler.Handle(
            new IngestTelemetryCommand("telemetry", NativeGpsPayload, TransportProtocol: "MQTT"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("una recursión sin fondo no debe tumbar la ingesta (fail-safe)");
        _store.Verify(s => s.StorePositionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.Is<GeoPosition>(p => p.Latitude == 18.4861), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once,
            "al fallar el script se usa el decode nativo, que trae la posición GPS");
    }

    [Fact]
    public async Task Handle_WithNoResolver_UsesNativeDecode()
    {
        var handler = BuildHandler(resolver: null);

        var result = await handler.Handle(
            new IngestTelemetryCommand("telemetry", NativeGpsPayload, TransportProtocol: "MQTT"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _store.Verify(s => s.StorePositionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.Is<GeoPosition>(p => p.Latitude == 18.4861), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class InlineResolver : ITenantConverterResolver
    {
        private readonly IProtocolConnector _connector;
        public InlineResolver(IProtocolConnector connector) => _connector = connector;
        public Task<IProtocolConnector?> ResolveUplinkConnectorAsync(Guid tenantId, string profileKey, CancellationToken ct) =>
            Task.FromResult<IProtocolConnector?>(_connector);
    }
}
