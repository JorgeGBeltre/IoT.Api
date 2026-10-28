using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Infrastructure.Persistence;
using IoT.Infrastructure.Persistence.Interceptors;
using IoT.Infrastructure.Protocols;
using IoT.Infrastructure.Repositories;
using IoT.Protocols.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Aislamiento por tenant (ADR-003) de la tabla nueva <c>protocol_converter_scripts</c> y del
/// resolutor de convertidores dinámicos, con RLS EFECTIVA: la app se conecta como <c>iot_app</c>
/// (NOBYPASSRLS) con <c>Platform:Rls:Enforce=true</c>. Prueba que:
///   - el script de un tenant NO es visible para otro, ni aun ignorando el filtro de EF (lo bloquea la RLS);
///   - el resolutor, bajo el contexto del tenant A, resuelve y DECODIFICA con el script del tenant;
///     bajo el contexto del tenant B (que no tiene script) devuelve null → se usaría el adaptador nativo;
///   - la escritura cross-tenant la bloquea el WITH CHECK de la política.
/// Requiere Docker. Sin Docker, el arranque del contenedor falla y la clase se reporta como error.
/// </summary>
public sealed class ProtocolConverterScriptRlsTests : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("a1a1a1a1-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("b2b2b2b2-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string AppRolePassword = "Iot_App_Test_Role_2026";

    private const string UplinkScript = @"
        function decode(payload, topic, metadata) {
            var d = JSON.parse(payload);
            return { deviceId: d.id, telemetry: { temperature: d.t } };
        }";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgis/postgis:16-3.4")
        .WithDatabase("iot_platform_db")
        .WithUsername("iot_admin_test")
        .WithPassword("Iot_Admin_Test_2026")
        .Build();

    private string _adminConnectionString = string.Empty;
    private string _runtimeConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _adminConnectionString = _postgres.GetConnectionString();
        _runtimeConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Username = "iot_app",
            Password = AppRolePassword
        }.ConnectionString;

        await using var admin = BuildContext(_adminConnectionString, withInterceptor: false, enforce: false, out _);
        await admin.Database.MigrateAsync();
        await RlsRoleBootstrapper.EnsureAppRoleAsync(admin, _runtimeConnectionString, _adminConnectionString, NullLogger.Instance);

        admin.Tenants.Add(Tenant.CreateWithId(TenantA, "Tenant A", "T-A").Value);
        admin.Tenants.Add(Tenant.CreateWithId(TenantB, "Tenant B", "T-B").Value);

        // Sólo el tenant A define un convertidor dinámico para el perfil 'mqtt' (sembrado como admin).
        admin.ProtocolConverterScripts.Add(
            ProtocolConverterScript.Create(TenantA, "mqtt", UplinkScript, downlinkScript: null, isEnabled: true).Value);

        await admin.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Script_OfOneTenant_IsInvisibleToAnother_UnderRls()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        var repo = new ProtocolConverterScriptRepository(ctx);

        tenant.TenantId = TenantA;
        (await repo.GetByTenantAsync(TenantA, default)).Should().HaveCount(1, "A ve su propio script");

        // Tenant B: no ve el script de A, ni por lista ni por clave, ni aun ignorando el filtro de EF (RLS).
        tenant.TenantId = TenantB;
        (await repo.GetByTenantAsync(TenantB, default)).Should().BeEmpty("B no tiene scripts");
        (await repo.GetByTenantAndProfileKeyAsync(TenantB, "mqtt", default)).Should().BeNull();

        var leaked = await ctx.ProtocolConverterScripts.IgnoreQueryFilters().CountAsync();
        leaked.Should().Be(0, "bajo RLS, el contexto del tenant B no ve ninguna fila de otro tenant");
    }

    [Fact]
    public async Task Resolver_UsesScriptForOwningTenant_AndNullForOther()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        var resolver = new TenantConverterResolver(new ProtocolConverterScriptRepository(ctx), new TenantConverterScriptCache());

        // Tenant A: el resolutor encuentra el script y el conector DECODIFICA el payload.
        tenant.TenantId = TenantA;
        var connectorA = await resolver.ResolveUplinkConnectorAsync(TenantA, "mqtt", default);
        connectorA.Should().NotBeNull();
        var decoded = connectorA!.UplinkConverter.ConvertUplink(TransportMessage.Create("t", "{\"id\":\"DEV-A\",\"t\":22.5}"));
        decoded.IsSuccess.Should().BeTrue();
        decoded.Value.DeviceIdentifier.Should().Be("DEV-A");
        decoded.Value.NumericMetrics["temperature"].Should().Be(22.5);

        // Tenant B: la RLS oculta el script de A → el resolutor devuelve null (se usaría el adaptador nativo).
        tenant.TenantId = TenantB;
        var connectorB = await resolver.ResolveUplinkConnectorAsync(TenantB, "mqtt", default);
        connectorB.Should().BeNull();
    }

    [Fact]
    public async Task Write_CrossTenant_IsBlockedByWithCheck()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);

        // Sesión del tenant A intentando insertar un script del tenant B: el WITH CHECK de la política lo bloquea.
        tenant.TenantId = TenantA;
        ctx.ProtocolConverterScripts.Add(
            ProtocolConverterScript.Create(TenantB, "intruso", UplinkScript, downlinkScript: null, isEnabled: true).Value);

        var act = async () => await ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("el WITH CHECK de RLS bloquea la escritura cross-tenant");
    }

    private static ApplicationDbContext BuildContext(
        string connectionString, bool withInterceptor, bool enforce, out MutableTenantContext tenant)
    {
        tenant = new MutableTenantContext();

        var builder = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseNetTopologySuite());

        if (withInterceptor)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Platform:Rls:Enforce"] = enforce ? "true" : "false"
                })
                .Build();

            builder.AddInterceptors(new TenantSessionInterceptor(
                tenant, config, NullLogger<TenantSessionInterceptor>.Instance));
        }

        return new ApplicationDbContext(builder.Options, tenant);
    }

    private sealed class MutableTenantContext : ITenantContext
    {
        public Guid TenantId { get; set; } = Guid.Empty;
        public Guid? CustomerId { get; set; }
        public string? CustomerPath { get; set; }
        public IReadOnlyCollection<Guid>? CustomerSubtreeIds { get; set; }
        public bool IsSystemOperation { get; set; }
    }
}
