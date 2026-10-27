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

        // Un device por nodo de T1 + uno en T2.
        AddDevice(admin, T1, "CHIP-ROOT1", "AA:00:00:00:00:01", root1.Id);
        AddDevice(admin, T1, "CHIP-A", "AA:00:00:00:00:02", custA.Id);
        AddDevice(admin, T1, "CHIP-A1", "AA:00:00:00:00:03", custA1.Id);
        AddDevice(admin, T1, "CHIP-B", "AA:00:00:00:00:04", custB.Id);
        AddDevice(admin, T2, "CHIP-T2", "BB:00:00:00:00:01", root2.Id);

        // Un user por nodo de T1 + uno en T2.
        AddUser(admin, T1, "u_root1", "u_root1@t1.com", root1.Id);
        AddUser(admin, T1, "u_a", "u_a@t1.com", custA.Id);
        AddUser(admin, T1, "u_a1", "u_a1@t1.com", custA1.Id);
        AddUser(admin, T1, "u_b", "u_b@t1.com", custB.Id);
        AddUser(admin, T2, "u_t2", "u_t2@t2.com", root2.Id);

        await admin.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private static void AddDevice(ApplicationDbContext ctx, Guid tenantId, string chip, string mac, Guid customerId)
    {
        var device = Device.Create(tenantId, chip, mac, chip, "Model", "Mfr").Value;
        device.AssignToCustomer(customerId);
        ctx.Devices.Add(device);
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

    // ---- Defensa en profundidad: el filtro EF por subárbol (SIN IgnoreQueryFilters) -----------

    [Fact]
    public async Task EfQueryFilter_WithCustomerPathA_FiltersSubtree_AndRootSeesAll()
    {
        // path = A: el filtro EF debe recortar al subárbol (A + A1).
        await using (var ctxA = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenantA))
        {
            tenantA.TenantId = T1;
            tenantA.CustomerPath = _pathA;

            var chips = await ctxA.Devices.Select(d => d.ChipId).ToListAsync();
            chips.Should().BeEquivalentTo(new[] { "CHIP-A", "CHIP-A1" },
                "el filtro EF por subárbol no debe SOBRE-devolver (hermano/raíz fuera)");
        }

        // path raíz: el filtro EF NO debe bloquear (NO DISRUPTIVO) → ve todo T1.
        await using (var ctxRoot = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenantRoot))
        {
            tenantRoot.TenantId = T1;
            tenantRoot.CustomerPath = _pathRoot1;
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
        public bool IsSystemOperation { get; set; }
    }
}
