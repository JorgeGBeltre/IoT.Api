using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Infrastructure.Persistence;
using IoT.Infrastructure.Persistence.Entities;
using IoT.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Criterio de aceptación de ADR-013 (E8.3): la Row-Level Security de PostgreSQL aísla por
/// SUBÁRBOL de la jerarquía Customer, encima del aislamiento por tenant de ADR-003, cuando la
/// aplicación se conecta con el rol NOBYPASSRLS (iot_app) y la GUC <c>app.customer_path</c> está
/// fijada por <see cref="TenantSessionInterceptor"/>.
///
/// Topología: tenant T1 con customers Raíz → A → A1 y B (hermano de A), con un device y un user en
/// CADA nodo; y un tenant T2 independiente. Se verifica:
///  (a) path = A      → ve A y A1 (descendiente), NO B (hermano);
///  (b) path = Raíz / path vacío → ve TODO T1 (NO DISRUPTIVO: el raíz y los tokens antiguos ven todo);
///  (c) cross-tenant  → NUNCA ve T2 con ningún path;
///  (d) los customers de B son invisibles con path = A.
///
/// Las aserciones con <c>IgnoreQueryFilters()</c> prueban que el aislamiento lo da la RLS (no el
/// filtro de EF). Un bloque adicional SIN IgnoreQueryFilters ejercita el filtro EF por subárbol
/// (defensa en profundidad) y confirma que no SOBRE-devuelve ni bloquea el caso raíz.
///
/// Requiere Docker. Sin Docker, el arranque del contenedor falla y la clase se reporta como error.
/// </summary>
public sealed class SubtreeIsolationTests : IAsyncLifetime
{
    private static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid T2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string AppRolePassword = "Iot_App_Test_Role_2026";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgis/postgis:16-3.4")
        .WithDatabase("iot_platform_db")
        .WithUsername("iot_admin_test")
        .WithPassword("Iot_Admin_Test_2026")
        .Build();

    private string _adminConnectionString = string.Empty;
    private string _runtimeConnectionString = string.Empty;

    // Paths ltree resueltos tras el sembrado (derivados de los Ids).
    private string _pathRoot1 = string.Empty;
    private string _pathA = string.Empty;

    // Ids de los nodos (para construir los conjuntos de subárbol que consume el filtro EF, igual
    // que hace el middleware en producción) y de los dispositivos (para sembrar sus datos calientes).
    private Guid _idRoot1, _idA, _idA1, _idB;
    private Guid _devRoot1, _devA, _devA1, _devB, _devT2;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _adminConnectionString = _postgres.GetConnectionString();
        _runtimeConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Username = "iot_app",
            Password = AppRolePassword
        }.ConnectionString;

        // 1. Migrar + provisionar iot_app (igual que Program.cs); contexto admin sin interceptor.
        await using var admin = BuildContext(_adminConnectionString, withInterceptor: false, enforce: false, out _);
        await admin.Database.MigrateAsync();
        await RlsRoleBootstrapper.EnsureAppRoleAsync(
            admin, _runtimeConnectionString, _adminConnectionString, NullLogger.Instance);

        // 2. Sembrar la jerarquía como admin (superusuario → bypassa RLS).
        admin.Tenants.Add(Tenant.CreateWithId(T1, "Tenant 1", "T-1").Value);
        admin.Tenants.Add(Tenant.CreateWithId(T2, "Tenant 2", "T-2").Value);

        // T1: Raíz → A → A1, y B hermano de A.
        var root1 = Customer.Create(T1, "Raiz T1", parent: null, code: "ROOT1").Value;
        var custA = Customer.Create(T1, "A", parent: root1, code: "A").Value;
        var custA1 = Customer.Create(T1, "A1", parent: custA, code: "A1").Value;
        var custB = Customer.Create(T1, "B", parent: root1, code: "B").Value;
        admin.Customers.AddRange(root1, custA, custA1, custB);

        // T2: sólo raíz.
        var root2 = Customer.Create(T2, "Raiz T2", parent: null, code: "ROOT2").Value;
        admin.Customers.Add(root2);

        _pathRoot1 = root1.Path;
        _pathA = custA.Path;
        _idRoot1 = root1.Id;
        _idA = custA.Id;
        _idA1 = custA1.Id;
        _idB = custB.Id;

        // Un device por nodo de T1 + uno en T2 (cada uno con una capability → fila device_capabilities).
        _devRoot1 = AddDevice(admin, T1, "CHIP-ROOT1", "AA:00:00:00:00:01", root1.Id);
        _devA = AddDevice(admin, T1, "CHIP-A", "AA:00:00:00:00:02", custA.Id);
        _devA1 = AddDevice(admin, T1, "CHIP-A1", "AA:00:00:00:00:03", custA1.Id);
        _devB = AddDevice(admin, T1, "CHIP-B", "AA:00:00:00:00:04", custB.Id);
        _devT2 = AddDevice(admin, T2, "CHIP-T2", "BB:00:00:00:00:01", root2.Id);

        // Un user por nodo de T1 + uno en T2.
        AddUser(admin, T1, "u_root1", "u_root1@t1.com", root1.Id);
        AddUser(admin, T1, "u_a", "u_a@t1.com", custA.Id);
        AddUser(admin, T1, "u_a1", "u_a1@t1.com", custA1.Id);
        AddUser(admin, T1, "u_b", "u_b@t1.com", custB.Id);
        AddUser(admin, T2, "u_t2", "u_t2@t2.com", root2.Id);

        // Datos calientes del dispositivo (positions + telemetry + device_commands) por cada device.
        // Es la superficie de la fuga E8.3b: antes sólo tenían aislamiento por tenant, sin recorte
        // por subárbol vía el customer dueño.
        AddDeviceData(admin, T1, _devRoot1);
        AddDeviceData(admin, T1, _devA);
        AddDeviceData(admin, T1, _devA1);
        AddDeviceData(admin, T1, _devB);
        AddDeviceData(admin, T2, _devT2);

        await admin.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private static Guid AddDevice(ApplicationDbContext ctx, Guid tenantId, string chip, string mac, Guid customerId)
    {
        var device = Device.Create(tenantId, chip, mac, chip, "Model", "Mfr").Value;
        device.AssignToCustomer(customerId);
        device.AssignCapability(CapabilityType.Gps); // → una fila device_capabilities por device
        ctx.Devices.Add(device);
        return device.Id;
    }

    private static void AddDeviceData(ApplicationDbContext ctx, Guid tenantId, Guid deviceId)
    {
        var now = DateTime.UtcNow;
        ctx.Positions.Add(new PositionEntity
        {
            TenantId = tenantId,
            DeviceId = deviceId,
            RecordedAt = now,
            ReceivedAt = now,
            Location = new Point(-69.9, 18.5) { SRID = 4326 },
            Latitude = 18.5,
            Longitude = -69.9,
            IsValid = true,
            Version = 1,
            Source = "real"
        });
        ctx.TelemetryRecords.Add(new TelemetryRecordEntity
        {
            TenantId = tenantId,
            DeviceId = deviceId,
            RecordedAt = now,
            ReceivedAt = now,
            MetricsJson = "{}",
            Source = "real"
        });
        ctx.DeviceCommands.Add(DeviceCommand.Create(tenantId, deviceId, "ping", null, "test").Value);
    }

    private static void AddUser(ApplicationDbContext ctx, Guid tenantId, string username, string email, Guid customerId)
    {
        var user = User.Create(tenantId, username, email, "hash_placeholder", username).Value;
        user.AssignToCustomer(customerId);
        ctx.Users.Add(user);
    }

    // ---- (a) path = A ve A y A1, no B; (d) customers de B invisibles con path = A -------------

    [Fact]
    public async Task Rls_WithCustomerPathA_SeesSubtreeOfA_NotSibling_EvenIgnoringEfFilters()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerPath = _pathA;

        // customers: A y A1 (subárbol de A). NO B ni la raíz.
        var customerCodes = await ctx.Customers.IgnoreQueryFilters()
            .Select(c => c.Code).ToListAsync();
        customerCodes.Should().BeEquivalentTo(new[] { "A", "A1" },
            "path=A sólo ve su propio nodo y sus descendientes (no la raíz ni el hermano B)");

        // devices: los de A y A1.
        var deviceChips = await ctx.Devices.IgnoreQueryFilters()
            .Select(d => d.ChipId).ToListAsync();
        deviceChips.Should().BeEquivalentTo(new[] { "CHIP-A", "CHIP-A1" },
            "sólo los dispositivos del subárbol de A son visibles");

        // users: los de A y A1.
        var usernames = await ctx.Users.IgnoreQueryFilters()
            .Select(u => u.Username).ToListAsync();
        usernames.Should().BeEquivalentTo(new[] { "u_a", "u_a1" },
            "sólo los usuarios del subárbol de A son visibles");

        // (d) El customer B es invisible con path = A.
        var bVisible = await ctx.Customers.IgnoreQueryFilters()
            .AnyAsync(c => c.Code == "B");
        bVisible.Should().BeFalse("el hermano B queda fuera del subárbol de A");
    }

    // ---- (b) path = Raíz ve TODO T1 (NO DISRUPTIVO) -------------------------------------------

    [Fact]
    public async Task Rls_WithRootPath_SeesEntireTenant_NonDisruptive()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerPath = _pathRoot1;

        (await ctx.Customers.IgnoreQueryFilters().CountAsync()).Should().Be(4, "el raíz ve los 4 nodos de T1");
        (await ctx.Devices.IgnoreQueryFilters().CountAsync()).Should().Be(4, "el raíz ve los 4 dispositivos de T1");
        (await ctx.Users.IgnoreQueryFilters().CountAsync()).Should().Be(4, "el raíz ve los 4 usuarios de T1");
    }

    // ---- (b') path vacío = token antiguo / sin claim ve TODO T1 (NO DISRUPTIVO) ---------------

    [Fact]
    public async Task Rls_WithEmptyCustomerPath_SeesEntireTenant_LikeLegacyToken()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerPath = null; // sin claim de customer (token anterior a E8.2)

        (await ctx.Customers.IgnoreQueryFilters().CountAsync()).Should().Be(4, "un token sin customer_path no restringe el subárbol");
        (await ctx.Devices.IgnoreQueryFilters().CountAsync()).Should().Be(4, "un token antiguo sigue viendo todo su tenant");
        (await ctx.Users.IgnoreQueryFilters().CountAsync()).Should().Be(4, "un token antiguo sigue viendo todo su tenant");
    }

    // ---- (c) cross-tenant: NUNCA ve T2, con cualquier path ------------------------------------

    [Fact]
    public async Task Rls_NeverSeesOtherTenant_WithAnyCustomerPath()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);

        // path = A
        tenant.TenantId = T1;
        tenant.CustomerPath = _pathA;
        (await ctx.Devices.IgnoreQueryFilters().CountAsync(d => d.ChipId == "CHIP-T2")).Should().Be(0);
        (await ctx.Customers.IgnoreQueryFilters().CountAsync(c => c.TenantId == T2)).Should().Be(0);

        // path = raíz de T1
        tenant.CustomerPath = _pathRoot1;
        (await ctx.Devices.IgnoreQueryFilters().CountAsync(d => d.TenantId == T2)).Should().Be(0);
        (await ctx.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId == T2)).Should().Be(0);

        // path vacío (token antiguo): sigue sin cruzar de tenant.
        tenant.CustomerPath = null;
        (await ctx.Customers.IgnoreQueryFilters().CountAsync(c => c.TenantId == T2)).Should().Be(0);
    }

    // ---- E8.3b: datos calientes del dispositivo acotados al subárbol vía el customer DUEÑO ------
    // positions / telemetry_records / device_commands / device_capabilities NO tienen CustomerId
    // propio (sólo TenantId, o lo derivan vía devices), así que el filtro EF por subárbol no las
    // toca: su único recorte por subárbol es la RLS de PostgreSQL. Estas pruebas usan
    // IgnoreQueryFilters para aislar que es la RLS (no EF) quien cierra la fuga entre hermanos.

    [Fact]
    public async Task Rls_DeviceData_WithCustomerPathA_SeesSubtree_NotSibling()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerPath = _pathA;

        // Sólo los datos de los dispositivos del subárbol de A (A + A1): 2 filas de cada tabla.
        (await ctx.Positions.IgnoreQueryFilters().CountAsync()).Should().Be(2,
            "path=A sólo ve las positions de sus devices (A y A1), no las del hermano B");
        (await ctx.TelemetryRecords.IgnoreQueryFilters().CountAsync()).Should().Be(2,
            "path=A sólo ve la telemetría de A y A1");
        (await ctx.DeviceCommands.IgnoreQueryFilters().CountAsync()).Should().Be(2,
            "path=A sólo ve los comandos de A y A1");
        (await ctx.DeviceCapabilities.IgnoreQueryFilters().CountAsync()).Should().Be(2,
            "path=A sólo ve las capabilities de A y A1");

        // El hermano B queda fuera del subárbol de A: 0 filas de B en cada tabla (la fuga cerrada).
        (await ctx.Positions.IgnoreQueryFilters().CountAsync(p => p.DeviceId == _devB)).Should().Be(0,
            "la posición/GPS del dispositivo del hermano B NO debe ser visible desde A (fuga E8.3b)");
        (await ctx.TelemetryRecords.IgnoreQueryFilters().CountAsync(t => t.DeviceId == _devB)).Should().Be(0);
        (await ctx.DeviceCommands.IgnoreQueryFilters().CountAsync(c => c.DeviceId == _devB)).Should().Be(0);
        (await ctx.DeviceCapabilities.IgnoreQueryFilters().CountAsync(c => c.DeviceId == _devB)).Should().Be(0);
    }

    [Fact]
    public async Task Rls_DeviceData_WithRootOrEmptyPath_SeesEntireTenant_NonDisruptive()
    {
        // path = raíz: ve los datos de los 4 dispositivos de T1.
        await using (var ctxRoot = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenantRoot))
        {
            tenantRoot.TenantId = T1;
            tenantRoot.CustomerPath = _pathRoot1;
            (await ctxRoot.Positions.IgnoreQueryFilters().CountAsync()).Should().Be(4, "el raíz ve las positions de todo T1");
            (await ctxRoot.TelemetryRecords.IgnoreQueryFilters().CountAsync()).Should().Be(4);
            (await ctxRoot.DeviceCommands.IgnoreQueryFilters().CountAsync()).Should().Be(4);
            (await ctxRoot.DeviceCapabilities.IgnoreQueryFilters().CountAsync()).Should().Be(4);
        }

        // path vacío (token antiguo / sin claim): sigue viendo todo T1 (NO DISRUPTIVO).
        await using (var ctxLegacy = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenantLegacy))
        {
            tenantLegacy.TenantId = T1;
            tenantLegacy.CustomerPath = null;
            (await ctxLegacy.Positions.IgnoreQueryFilters().CountAsync()).Should().Be(4, "un token sin path no recorta los datos del dispositivo");
            (await ctxLegacy.TelemetryRecords.IgnoreQueryFilters().CountAsync()).Should().Be(4);
            (await ctxLegacy.DeviceCommands.IgnoreQueryFilters().CountAsync()).Should().Be(4);
            (await ctxLegacy.DeviceCapabilities.IgnoreQueryFilters().CountAsync()).Should().Be(4);
        }
    }

    [Fact]
    public async Task Rls_DeviceData_NeverSeesOtherTenant_WithAnyCustomerPath()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;

        foreach (var path in new[] { _pathA, _pathRoot1, null })
        {
            tenant.CustomerPath = path;
            (await ctx.Positions.IgnoreQueryFilters().CountAsync(p => p.DeviceId == _devT2)).Should().Be(0,
                "nunca se ven las positions del dispositivo de T2, con ningún path");
            (await ctx.TelemetryRecords.IgnoreQueryFilters().CountAsync(t => t.DeviceId == _devT2)).Should().Be(0);
            (await ctx.DeviceCommands.IgnoreQueryFilters().CountAsync(c => c.DeviceId == _devT2)).Should().Be(0);
            (await ctx.DeviceCapabilities.IgnoreQueryFilters().CountAsync(c => c.DeviceId == _devT2)).Should().Be(0);
        }
    }

    // ---- Defensa en profundidad: el filtro EF por subárbol (SIN IgnoreQueryFilters) -----------

    [Fact]
    public async Task EfQueryFilter_WithCustomerPathA_FiltersSubtree_AndRootSeesAll()
    {
        // path = A: el filtro EF debe recortar al subárbol (A + A1).
        await using (var ctxA = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenantA))
        {
            tenantA.TenantId = T1;
            tenantA.CustomerPath = _pathA;
            // El conjunto de descendientes lo resuelve el middleware en producción (async, una vez
            // por request). Aquí se fija explícitamente el subárbol de A = {A, A1}.
            tenantA.CustomerSubtreeIds = new[] { _idA, _idA1 };

            var chips = await ctxA.Devices.Select(d => d.ChipId).ToListAsync();
            chips.Should().BeEquivalentTo(new[] { "CHIP-A", "CHIP-A1" },
                "el filtro EF por subárbol no debe SOBRE-devolver (hermano/raíz fuera)");
        }

        // path raíz: el filtro EF NO debe bloquear (NO DISRUPTIVO) → ve todo T1.
        await using (var ctxRoot = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenantRoot))
        {
            tenantRoot.TenantId = T1;
            tenantRoot.CustomerPath = _pathRoot1;
            // Subárbol del raíz = los 4 nodos de T1 (lo que resolvería el middleware).
            tenantRoot.CustomerSubtreeIds = new[] { _idRoot1, _idA, _idA1, _idB };
            (await ctxRoot.Devices.CountAsync()).Should().Be(4, "el raíz no es recortado por el filtro EF");
        }

        // path vacío (token antiguo): el filtro EF NO debe bloquear → ve todo T1.
        await using (var ctxLegacy = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenantLegacy))
        {
            tenantLegacy.TenantId = T1;
            tenantLegacy.CustomerPath = null;
            (await ctxLegacy.Devices.CountAsync()).Should().Be(4, "un token sin path no es recortado por el filtro EF");
        }
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
