using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Infrastructure.Persistence;
using IoT.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Criterio de aceptación de ADR-003: la Row-Level Security de PostgreSQL aísla por tenant a
/// nivel de BASE DE DATOS cuando la aplicación se conecta con el rol NOBYPASSRLS (iot_app),
/// INDEPENDIENTEMENTE del filtro global de EF Core (que se desactiva aquí con IgnoreQueryFilters
/// para demostrar que el aislamiento lo da RLS, no el filtro .NET).
///
/// Levanta un PostgreSQL+PostGIS real con Testcontainers y reutiliza la maquinaria de producción:
/// migra como admin, provisiona el rol iot_app con <see cref="RlsRoleBootstrapper"/>, siembra datos
/// cross-tenant como admin (superusuario, bypassa RLS), y luego consulta como iot_app a través del
/// <see cref="TenantSessionInterceptor"/> con Platform:Rls:Enforce=true.
///
/// Requiere Docker. Si no hay Docker disponible, el arranque del contenedor falla y la clase entera
/// se reporta como error (no como passed en falso).
/// </summary>
public sealed class RlsCrossTenantIsolationTests : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string AppRolePassword = "Iot_App_Test_Role_2026";

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

        // La conexión de runtime usa iot_app (NOBYPASSRLS) sobre el mismo host/puerto/base.
        _runtimeConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Username = "iot_app",
            Password = AppRolePassword
        }.ConnectionString;

        // 1. Migrar + provisionar el rol, tal como lo hace Program.cs (contexto admin sin interceptor).
        await using var admin = BuildContext(_adminConnectionString, withInterceptor: false, enforce: false, out _);
        await admin.Database.MigrateAsync();
        await RlsRoleBootstrapper.EnsureAppRoleAsync(
            admin, _runtimeConnectionString, _adminConnectionString, NullLogger.Instance);

        // 2. Sembrar datos cross-tenant como admin (superusuario → bypassa RLS para la siembra).
        admin.Tenants.Add(Tenant.CreateWithId(TenantA, "Tenant A", "T-A").Value);
        admin.Tenants.Add(Tenant.CreateWithId(TenantB, "Tenant B", "T-B").Value);
        admin.Devices.Add(Device.Create(TenantA, "CHIP-A1", "AA:AA:AA:AA:AA:01", "Dev A1", "Model", "Mfr").Value);
        admin.Devices.Add(Device.Create(TenantA, "CHIP-A2", "AA:AA:AA:AA:AA:02", "Dev A2", "Model", "Mfr").Value);
        admin.Devices.Add(Device.Create(TenantB, "CHIP-B1", "BB:BB:BB:BB:BB:01", "Dev B1", "Model", "Mfr").Value);
        await admin.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task AppRole_Exists_AndDoesNotBypassRls()
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rolbypassrls FROM pg_roles WHERE rolname = 'iot_app';";
        var bypass = await cmd.ExecuteScalarAsync();

        bypass.Should().NotBeNull("el rol iot_app debe existir tras el bootstrap");
        ((bool)bypass!).Should().BeFalse("iot_app NUNCA debe omitir RLS (NOBYPASSRLS)");
    }

    [Fact]
    public async Task IotApp_WithTenantContext_SeesOnlyOwnTenant_EvenIgnoringEfQueryFilters()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);

        // Contexto de Tenant A: aun IGNORANDO el filtro de EF, sólo deben verse las 2 filas de A.
        tenant.TenantId = TenantA;
        var aCount = await ctx.Devices.IgnoreQueryFilters().CountAsync();
        aCount.Should().Be(2, "RLS debe limitar a iot_app a las filas del tenant A");

        // Contexto de Tenant B: sólo 1 fila.
        tenant.TenantId = TenantB;
        var bCount = await ctx.Devices.IgnoreQueryFilters().CountAsync();
        bCount.Should().Be(1, "RLS debe limitar a iot_app a las filas del tenant B");

        // Desde A, intentar leer un dispositivo de B por su ChipId (ignorando el filtro EF) → nada.
        tenant.TenantId = TenantA;
        var leaked = await ctx.Devices.IgnoreQueryFilters()
            .Where(d => d.ChipId == "CHIP-B1")
            .FirstOrDefaultAsync();
        leaked.Should().BeNull("un dispositivo de otro tenant no debe ser visible bajo RLS");
    }

    [Fact]
    public async Task IotApp_WithTenantContext_CannotWriteIntoAnotherTenant_WithCheck()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);

        // Sesión del tenant A intentando insertar una fila del tenant B: la cláusula WITH CHECK
        // de la política RLS debe rechazar la escritura cross-tenant.
        tenant.TenantId = TenantA;
        ctx.Devices.Add(Device.Create(TenantB, "CHIP-B-INTRUSO", "BB:BB:BB:BB:BB:99", "Intruso", "Model", "Mfr").Value);

        var act = async () => await ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("WITH CHECK de RLS debe bloquear la escritura cross-tenant");
    }

    [Fact]
    public async Task IotApp_WithoutTenant_AndNotSystem_UnderEnforce_IsBlocked()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);

        // Sin TenantId y sin operación de sistema, bajo enforce el interceptor debe RECHAZAR la
        // apertura de conexión (ADR-003 D5): nunca se abre una conexión de plano de datos sin tenant.
        tenant.TenantId = Guid.Empty;
        tenant.IsSystemOperation = false;

        var act = async () => await ctx.Devices.IgnoreQueryFilters().CountAsync();
        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        ex.ToString().Should().Contain("Row-Level Security", "el interceptor debe rechazar la conexión sin tenant bajo enforce");
    }

    [Fact]
    public async Task IotApp_SystemOperation_SeesAllTenants()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);

        // Operación de sistema explícita (cross-tenant autorizada, ADR-003 D6): ve todo.
        tenant.TenantId = Guid.Empty;
        tenant.IsSystemOperation = true;
        var total = await ctx.Devices.IgnoreQueryFilters().CountAsync();
        total.Should().Be(3, "una operación de sistema ve las filas de todos los tenants");
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

    /// <summary>Contexto de tenant mutable para poder cambiar de tenant entre consultas en el test.</summary>
    private sealed class MutableTenantContext : ITenantContext
    {
        public Guid TenantId { get; set; } = Guid.Empty;
        public Guid? CustomerId { get; set; }
        public string? CustomerPath { get; set; }
        public bool IsSystemOperation { get; set; }
    }
}
