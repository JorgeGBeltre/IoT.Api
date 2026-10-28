using FluentAssertions;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Protocols;
using IoT.Protocols.Contracts;
using IoT.Protocols.Dynamic;
using Moq;
using Xunit;

namespace IoT.Tests.Unit;

/// <summary>
/// Pruebas del resolutor de convertidores dinámicos por tenant y su caché por hash, y de la validación
/// en sandbox. Cubren: elegir script vs nativo, caché por (tenant, perfil, hash) y reconstrucción al
/// cambiar el script, caché negativa, y rechazo de JS inválido / que excede el sandbox.
/// </summary>
public sealed class TenantConverterResolverTests
{
    private const string UplinkScript = @"
        function decode(payload, topic, metadata) {
            var d = JSON.parse(payload);
            return { deviceId: d.id, telemetry: { temperature: d.t } };
        }";

    private static ProtocolConverterScript NewScript(Guid tenant, string key, string uplink, bool enabled = true) =>
        ProtocolConverterScript.Create(tenant, key, uplink, downlinkScript: null, isEnabled: enabled).Value;

    [Fact]
    public async Task Resolve_WhenEnabledScriptExists_ReturnsConnectorThatDecodesViaScript()
    {
        var tenant = Guid.NewGuid();
        var repo = new Mock<IProtocolConverterScriptRepository>();
        repo.Setup(r => r.GetByTenantAndProfileKeyAsync(tenant, "mqtt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewScript(tenant, "mqtt", UplinkScript));

        var resolver = new TenantConverterResolver(repo.Object, new TenantConverterScriptCache());

        var connector = await resolver.ResolveUplinkConnectorAsync(tenant, "MQTT", CancellationToken.None);

        connector.Should().NotBeNull();
        var result = connector!.UplinkConverter.ConvertUplink(
            TransportMessage.Create("t", "{\"id\":\"DEV-1\",\"t\":21.5}"));
        result.IsSuccess.Should().BeTrue();
        result.Value.DeviceIdentifier.Should().Be("DEV-1");
        result.Value.NumericMetrics["temperature"].Should().Be(21.5);
    }

    [Fact]
    public async Task Resolve_WhenNoScript_ReturnsNull_SoNativeAdapterIsUsed()
    {
        var tenant = Guid.NewGuid();
        var repo = new Mock<IProtocolConverterScriptRepository>();
        repo.Setup(r => r.GetByTenantAndProfileKeyAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProtocolConverterScript?)null);

        var resolver = new TenantConverterResolver(repo.Object, new TenantConverterScriptCache());

        var connector = await resolver.ResolveUplinkConnectorAsync(tenant, "MQTT", CancellationToken.None);

        connector.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_WhenScriptDisabled_ReturnsNull()
    {
        var tenant = Guid.NewGuid();
        var repo = new Mock<IProtocolConverterScriptRepository>();
        repo.Setup(r => r.GetByTenantAndProfileKeyAsync(tenant, "mqtt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewScript(tenant, "mqtt", UplinkScript, enabled: false));

        var resolver = new TenantConverterResolver(repo.Object, new TenantConverterScriptCache());

        (await resolver.ResolveUplinkConnectorAsync(tenant, "MQTT", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Resolve_CachesByHash_DoesNotHitRepositoryOrRebuildEachCall()
    {
        var tenant = Guid.NewGuid();
        var repo = new Mock<IProtocolConverterScriptRepository>();
        repo.Setup(r => r.GetByTenantAndProfileKeyAsync(tenant, "mqtt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewScript(tenant, "mqtt", UplinkScript));

        var resolver = new TenantConverterResolver(repo.Object, new TenantConverterScriptCache());

        var first = await resolver.ResolveUplinkConnectorAsync(tenant, "MQTT", CancellationToken.None);
        var second = await resolver.ResolveUplinkConnectorAsync(tenant, "MQTT", CancellationToken.None);

        first.Should().BeSameAs(second, "con el TTL fresco, el conector se reutiliza sin reconstruir");
        repo.Verify(r => r.GetByTenantAndProfileKeyAsync(tenant, "mqtt", It.IsAny<CancellationToken>()), Times.Once,
            "la segunda resolución se sirve de la caché fresca");
    }

    [Fact]
    public void Cache_StorePositive_RebuildsOnlyWhenHashChanges()
    {
        var cache = new TenantConverterScriptCache();
        var tenant = Guid.NewGuid();
        int builds = 0;
        IProtocolConnector Build() { builds++; return new ProtocolConnector("c", "DYNAMIC", new ScriptableUplinkConverter("c", UplinkScript)); }

        var hashA = ITenantConverterScriptCache.ComputeHash(UplinkScript, null);
        cache.StorePositive(tenant, "mqtt", hashA, Build);
        cache.TryGetFresh(tenant, "mqtt", out _).Should().BeTrue();
        cache.StorePositive(tenant, "mqtt", hashA, Build); // mismo hash → no reconstruye
        builds.Should().Be(1);

        var hashB = ITenantConverterScriptCache.ComputeHash(UplinkScript + "//v2", null);
        cache.StorePositive(tenant, "mqtt", hashB, Build); // hash distinto → reconstruye
        builds.Should().Be(2);
    }

    [Fact]
    public void Cache_NegativeEntry_IsFreshAndReturnsNull()
    {
        var cache = new TenantConverterScriptCache();
        var tenant = Guid.NewGuid();

        cache.StoreNegative(tenant, "mqtt");

        cache.TryGetFresh(tenant, "mqtt", out var connector).Should().BeTrue();
        connector.Should().BeNull();
    }

    // ---- Validación en sandbox ----

    [Fact]
    public void Validator_AcceptsValidUplinkScript()
    {
        ScriptConverterValidator.ValidateUplink(UplinkScript, "{\"id\":\"X\",\"t\":1}")
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validator_RejectsSyntaxError()
    {
        var result = ScriptConverterValidator.ValidateUplink("function decode( { this is not js");
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ConverterScript.InvalidScript");
    }

    [Fact]
    public void Validator_RejectsScriptWithoutDecodeFunction()
    {
        var result = ScriptConverterValidator.ValidateUplink("var x = 1;");
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ConverterScript.MissingFunction");
    }

    [Fact]
    public void Validator_RejectsScriptThatExceedsSandboxStatementLimit()
    {
        // Bucle infinito en el cuerpo superior: el límite de sentencias del sandbox corta la compilación/ejecución.
        const string runaway = "while (true) { } function decode(p,t,m){ return {}; }";
        var result = ScriptConverterValidator.ValidateUplink(runaway);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ConverterScript.SandboxExceeded");
    }

    [Fact]
    public void Validator_RejectsLoopInDecodeBody_WhenRunAgainstSamplePayload()
    {
        // Bucle infinito EN EL CUERPO de decode() (no a nivel superior): compila y la función existe,
        // pero al ejecutarla contra el payload de ejemplo excede el límite de sentencias del sandbox.
        // Antes de clasificar por TIPO de excepción, el convertidor envolvía el error y este script se
        // PERSISTÍA; ahora se RECHAZA (400) sin guardar.
        const string loopInBody = "function decode(p,t,m){ var s=0; while(true){ s++; } return {}; }";
        var result = ScriptConverterValidator.ValidateUplink(loopInBody, "{\"id\":\"X\"}");
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ConverterScript.SandboxExceeded");
    }

    [Fact]
    public void Validator_RejectsUnboundedRecursionInDecodeBody()
    {
        // Recursión sin fondo EN EL CUERPO: con LimitRecursion, Jint lanza
        // RecursionDepthOverflowException al ejecutar la muestra. Se clasifica por tipo y se rechaza.
        const string recursiveBody = "function decode(p,t,m){ return decode(p,t,m); }";
        var result = ScriptConverterValidator.ValidateUplink(recursiveBody, "{\"id\":\"X\"}");
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ConverterScript.SandboxExceeded");
    }

    [Fact]
    public void Validator_RejectsLoopInEncodeBody_WhenRunAgainstSample()
    {
        // Mismo caso para downlink: bucle en el cuerpo de encode() => rechazo sin persistir.
        const string loopInBody = "function encode(c,p,s){ while(true){ } return {}; }";
        var result = ScriptConverterValidator.ValidateDownlink(loopInBody);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ConverterScript.SandboxExceeded");
    }
}
