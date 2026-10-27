using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Infrastructure.Persistence;
using IoT.Infrastructure.Persistence.Interceptors;
using IoT.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Criterio de aceptación de ADR-014 (P1a): los atributos de dispositivo (<c>device_attributes</c>) son
/// un dato PROPIEDAD DEL DEVICE y quedan aislados por SUBÁRBOL (ADR-013) vía el <c>Customer</c> del
/// device dueño, encima del aislamiento por tenant (ADR-003), cuando la app se conecta con el rol
/// NOBYPASSRLS (<c>iot_app</c>) y la GUC <c>app.customer_path</c> está fijada. El recorte por subárbol
/// de esta tabla lo da SÓLO la RLS (no es <c>ICustomerScoped</c>, no tiene filtro EF por subárbol),
/// igual que <c>positions</c>/<c>telemetry_records</c>; por eso las aserciones usan
/// <c>IgnoreQueryFilters</c> para aislar que es la RLS quien cierra la fuga entre hermanos.
///
/// Topología: tenant T1 con Raíz → A → A1 y B (hermano de A), un device por nodo A/A1/B; y un tenant
/// T2 independiente con su device. Se verifica:
///  (a) una sesión acotada a A ve/lista los atributos de A y A1 (descendiente), NO los de B (hermano);
///  (b) cross-tenant: nunca ve los atributos de T2, con ningún path;
///  (c) el índice único (TenantId, DeviceId, Scope, Key) impide duplicados: el upsert reemplaza.
///
/// Requiere Docker. Sin Docker, el arranque del contenedor falla y la clase se reporta como error.
/// </summary>
public sealed class DeviceAttributeSubtreeTests : IAsyncLifetime
{
    private static readonly Guid T1 = Guid.Parse("a1111111-1111-1111-1111-111111111111");
    private static readonly Guid T2 = Guid.Parse("a2222222-2222-2222-2222-222222222222");
    private const string AppRolePassword = "Iot_App_Test_Role_2026";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgis/postgis:16-3.4")
        .WithDatabase("iot_platform_db")
        .WithUsername("iot_admin_test")
        .WithPassword("Iot_Admin_Test_2026")
        .Build();

    private string _adminConnectionString = string.Empty;
    private string _runtimeConnectionString = string.Empty;

    private string _pathRoot1 = string.Empty;
    private string _pathA = string.Empty;
    private Guid _devA, _devA1, _devB, _devT2;

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
        await RlsRoleBootstrapper.EnsureAppRoleAsync(
            admin, _runtimeConnectionString, _adminConnectionString, NullLogger.Instance);

        admin.Tenants.Add(Tenant.CreateWithId(T1, "Tenant 1", "T-1").Value);
        admin.Tenants.Add(Tenant.CreateWithId(T2, "Tenant 2", "T-2").Value);

        var root1 = Customer.Create(T1, "Raiz T1", parent: null, code: "ROOT1").Value;
        var custA = Customer.Create(T1, "A", parent: root1, code: "A").Value;
        var custA1 = Customer.Create(T1, "A1", parent: custA, code: "A1").Value;
        var custB = Customer.Create(T1, "B", parent: root1, code: "B").Value;
        admin.Customers.AddRange(root1, custA, custA1, custB);

        var root2 = Customer.Create(T2, "Raiz T2", parent: null, code: "ROOT2").Value;
        admin.Customers.Add(root2);

        _pathRoot1 = root1.Path;
        _pathA = custA.Path;

        _devA = AddDevice(admin, T1, "ATTR-CHIP-A", "AC:00:00:00:00:02", custA.Id);
        _devA1 = AddDevice(admin, T1, "ATTR-CHIP-A1", "AC:00:00:00:00:03", custA1.Id);
        _devB = AddDevice(admin, T1, "ATTR-CHIP-B", "AC:00:00:00:00:04", custB.Id);
        _devT2 = AddDevice(admin, T2, "ATTR-CHIP-T2", "BC:00:00:00:00:01", root2.Id);

        await admin.SaveChangesAsync();

        // Sembrar 2 atributos por device (client + server) con una sesión RAÍZ (path null = sin recorte
        // por subárbol, dentro del tenant): ejercita UpsertAsync y pasa el WITH CHECK por el customer
        // del device. Cada device de T1 por su sesión de T1; el de T2 por una sesión de T2.
        await SeedAttributes(T1, _pathRoot1, _devA, _devA1, _devB);
        await SeedAttributes(T2, path: null, _devT2);
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private async Task SeedAttributes(Guid tenantId, string? path, params Guid[] deviceIds)
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = tenantId;
        tenant.CustomerPath = path; // null = raíz/sin recorte por subárbol
        var repo = new DeviceAttributeRepository(ctx);

        foreach (var deviceId in deviceIds)
        {
            await repo.UpsertAsync(tenantId, deviceId, AttributeScope.Client, "battery", "95", default);
            await repo.UpsertAsync(tenantId, deviceId, AttributeScope.Server, "note", "\"seed\"", default);
        }

        await ctx.SaveChangesAsync();
    }

    private static Guid AddDevice(ApplicationDbContext ctx, Guid tenantId, string chip, string mac, Guid customerId)
    {
        var device = Device.Create(tenantId, chip, mac, chip, "Model", "Mfr").Value;
        device.AssignToCustomer(customerId);
        ctx.Devices.Add(device);
        return device.Id;
    }

    // ---- (a) path = A ve los atributos de A y A1 (descendiente), NO los del hermano B --------------

    [Fact]
    public async Task Rls_Attributes_WithCustomerPathA_SeesSubtree_NotSibling()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerPath = _pathA;

        // 2 atributos por device del subárbol de A (A + A1) = 4.
        (await ctx.DeviceAttributes.IgnoreQueryFilters().CountAsync()).Should().Be(4,
            "path=A sólo ve los atributos de sus devices (A y A1), no los del hermano B");

        // El hermano B queda fuera del subárbol de A: 0 filas suyas.
        (await ctx.DeviceAttributes.IgnoreQueryFilters().CountAsync(a => a.DeviceId == _devB)).Should().Be(0,
            "los atributos del dispositivo del hermano B NO deben ser visibles desde A");

        // El repositorio (RESPETANDO filtros) lista los del device de A y de A1, pero NO los de B.
        var repo = new DeviceAttributeRepository(ctx);
        (await repo.ListByDeviceAsync(_devA)).Should().HaveCount(2);
        (await repo.ListByDeviceAsync(_devA1)).Should().HaveCount(2);
        (await repo.ListByDeviceAsync(_devB)).Should().BeEmpty(
            "una sesión acotada a A no lista los atributos del hermano B");

        var clientAttrs = await repo.GetByDeviceAndScopeAsync(_devA, AttributeScope.Client);
        clientAttrs.Should().ContainSingle().Which.Key.Should().Be("battery");
    }

    // ---- (b) path = Raíz ve TODO T1 (NO DISRUPTIVO) ------------------------------------------------

    [Fact]
    public async Task Rls_Attributes_WithRootOrEmptyPath_SeesEntireTenant_NonDisruptive()
    {
        await using (var ctxRoot = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tRoot))
        {
            tRoot.TenantId = T1;
            tRoot.CustomerPath = _pathRoot1;
            (await ctxRoot.DeviceAttributes.IgnoreQueryFilters().CountAsync()).Should().Be(6,
                "el raíz ve los atributos de los 3 devices de T1 (A, A1, B)");
        }

        await using (var ctxLegacy = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tLegacy))
        {
            tLegacy.TenantId = T1;
            tLegacy.CustomerPath = null;
            (await ctxLegacy.DeviceAttributes.IgnoreQueryFilters().CountAsync()).Should().Be(6,
                "un token sin customer_path no recorta por subárbol (NO DISRUPTIVO)");
        }
    }

    // ---- (c) cross-tenant: nunca ve los atributos de T2, con cualquier path ------------------------

    [Fact]
    public async Task Rls_Attributes_NeverSeesOtherTenant_WithAnyCustomerPath()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;

        foreach (var path in new[] { _pathA, _pathRoot1, null })
        {
            tenant.CustomerPath = path;
            (await ctx.DeviceAttributes.IgnoreQueryFilters().CountAsync(a => a.DeviceId == _devT2)).Should().Be(0,
                "nunca se ven los atributos del dispositivo de T2, con ningún path");
            (await ctx.DeviceAttributes.IgnoreQueryFilters().CountAsync(a => a.TenantId == T2)).Should().Be(0);
        }
    }

    // ---- (d) índice único (TenantId, DeviceId, Scope, Key): el upsert reemplaza, no duplica --------

    [Fact]
    public async Task Upsert_SameScopeAndKey_ReplacesValue_NoDuplicate()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerPath = _pathA;
        var repo = new DeviceAttributeRepository(ctx);

        // La clave "battery" (Client) ya existe para devA (sembrada a 95). Un segundo upsert la actualiza.
        await repo.UpsertAsync(T1, _devA, AttributeScope.Client, "battery", "40", default);
        await ctx.SaveChangesAsync();

        var clientAttrs = await repo.GetByDeviceAndScopeAsync(_devA, AttributeScope.Client);
        clientAttrs.Should().ContainSingle("el upsert sobre la misma (scope, clave) reemplaza, no duplica");
        clientAttrs[0].Value.Should().Be("40");

        // Misma clave en OTRO scope (Server) es una fila distinta: no colisiona con el índice único.
        await repo.UpsertAsync(T1, _devA, AttributeScope.Server, "battery", "\"n/a\"", default);
        await ctx.SaveChangesAsync();
        (await repo.ListByDeviceAsync(_devA)).Should().HaveCount(3,
            "battery/Client + note/Server + battery/Server: el scope forma parte de la identidad");
    }

    [Fact]
    public async Task UniqueIndex_RejectsRawDuplicate_SameTenantDeviceScopeKey()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerPath = _pathRoot1;

        // INSERT crudo de un duplicado exacto (battery/Client en devA, ya sembrado): el índice único
        // (TenantId, DeviceId, Scope, Key) debe rechazarlo con 23505. Value es text (ADR-014 D2).
        var act = async () => await ctx.Database.ExecuteSqlRawAsync(
            @"INSERT INTO device_attributes (""Id"", ""TenantId"", ""DeviceId"", ""Scope"", ""Key"", ""Value"", ""LastUpdatedAt"")
              VALUES ({0}, {1}, {2}, 'Client', 'battery', '1', now())",
            Guid.NewGuid(), T1, _devA);

        var thrown = await act.Should().ThrowAsync<PostgresException>(
            "el índice único impide dos valores para la misma (tenant, device, scope, clave)");
        thrown.Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation, "23505 unique_violation");
    }

    // ---- (e) WITH CHECK: una sesión acotada a A NO puede ESCRIBIR sobre el device del hermano B ----

    [Fact]
    public async Task Upsert_InsertOnSiblingDevice_FromScopedSessionA_IsRejectedByWithCheck_NotPersisted()
    {
        await using (var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant))
        {
            tenant.TenantId = T1;
            tenant.CustomerPath = _pathA; // sesión acotada al subárbol de A

            var repo = new DeviceAttributeRepository(ctx);

            // Clave NUEVA (no sembrada) sobre el device del hermano B: como B no es visible desde A, el
            // upsert resuelve a INSERT; el WITH CHECK (device fuera del subárbol de la sesión) lo rechaza.
            var act = async () =>
            {
                await repo.UpsertAsync(T1, _devB, AttributeScope.Client, "intrusion", "1", default);
                await ctx.SaveChangesAsync();
            };

            var thrown = await act.Should().ThrowAsync<DbUpdateException>(
                "el WITH CHECK rechaza INSERTAR una fila cuyo device no está en el subárbol de la sesión");
            thrown.WithInnerException<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                    "42501: new row violates row-level security policy");
        }

        // NADA se persistió: se comprueba con el admin (superusuario, bypassa RLS) e IgnoreQueryFilters.
        await using var admin = BuildContext(_adminConnectionString, withInterceptor: false, enforce: false, out _);
        (await admin.DeviceAttributes.IgnoreQueryFilters()
            .CountAsync(a => a.DeviceId == _devB && a.Key == "intrusion"))
            .Should().Be(0, "la fila intrusa sobre el hermano B no debe existir en la base");
    }

    // ---- (f) WITH CHECK cross-tenant: una sesión de T1 NO puede ESCRIBIR sobre un device de T2 ------

    [Fact]
    public async Task Upsert_InsertOnOtherTenantDevice_IsRejectedByWithCheck_NotPersisted()
    {
        await using (var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant))
        {
            tenant.TenantId = T1;
            tenant.CustomerPath = _pathRoot1; // raíz de T1: el bloqueo es por tenant, no por subárbol

            var repo = new DeviceAttributeRepository(ctx);

            // Intentar escribir sobre el device de OTRO tenant (T2): el device de T2 no es alcanzable
            // desde la sesión de T1, así que el WITH CHECK rechaza la escritura.
            var act = async () =>
            {
                await repo.UpsertAsync(T1, _devT2, AttributeScope.Client, "intrusion", "1", default);
                await ctx.SaveChangesAsync();
            };

            var thrown = await act.Should().ThrowAsync<DbUpdateException>(
                "el WITH CHECK rechaza INSERTAR una fila sobre el device de otro tenant");
            thrown.WithInnerException<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                    "42501: new row violates row-level security policy");
        }

        await using var admin = BuildContext(_adminConnectionString, withInterceptor: false, enforce: false, out _);
        (await admin.DeviceAttributes.IgnoreQueryFilters()
            .CountAsync(a => a.DeviceId == _devT2 && a.Key == "intrusion"))
            .Should().Be(0, "no debe persistirse ninguna fila sobre el device de T2");
    }

    // ---- (g) Value text (ADR-014 D2): persiste la cadena vacía y escalares simples sin error --------

    [Fact]
    public async Task Upsert_WithEmptyAndScalarValues_PersistsWithoutError()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerPath = _pathA;
        var repo = new DeviceAttributeRepository(ctx);

        // Valor vacío (value null -> "" en la entidad) y escalares NO entrecomillados (no serían JSON
        // válido): con Value en text persisten sin 22P02.
        await repo.UpsertAsync(T1, _devA, AttributeScope.Server, "emptyNote", null!, default);
        await repo.UpsertAsync(T1, _devA, AttributeScope.Client, "status", "online", default);
        await repo.UpsertAsync(T1, _devA, AttributeScope.Client, "count", "42", default);
        await repo.UpsertAsync(T1, _devA, AttributeScope.Client, "enabled", "true", default);
        await ctx.SaveChangesAsync();

        var serverAttrs = await repo.GetByDeviceAndScopeAsync(_devA, AttributeScope.Server);
        serverAttrs.Should().Contain(a => a.Key == "emptyNote" && a.Value == string.Empty);

        var clientAttrs = await repo.GetByDeviceAndScopeAsync(_devA, AttributeScope.Client);
        clientAttrs.Should().Contain(a => a.Key == "status" && a.Value == "online");
        clientAttrs.Should().Contain(a => a.Key == "count" && a.Value == "42");
        clientAttrs.Should().Contain(a => a.Key == "enabled" && a.Value == "true");
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
