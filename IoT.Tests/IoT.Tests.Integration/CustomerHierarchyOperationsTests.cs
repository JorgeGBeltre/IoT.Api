using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Customers.Commands;
using IoT.Application.Customers.Queries;
using IoT.Application.Devices.Commands;
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
/// Criterio de aceptación de E8.4b con la RLS real (rol NOBYPASSRLS <c>iot_app</c>, enforce=true):
/// la CAPA DE APLICACIÓN que opera la jerarquía (crear sub-customer, listar subárbol, reasignar el
/// nodo dueño de un dispositivo) queda acotada al subárbol de la sesión por la RLS de PostgreSQL y el
/// filtro de EF, SIN lógica de recorte extra en los handlers.
///
/// Topología T1: Raíz → A → A1, y B (hermano de A), con un device en A, A1 y B; T2 independiente.
/// Se verifica para una sesión acotada a A:
///   (a) crea un hijo de A (por parent explícito y por parent por defecto = nodo de la sesión);
///   (b) lista su subárbol: ve A y descendientes, NO el hermano B ni la raíz;
///   (c) reasigna su propio device a A1;
///   (d) NO puede crear un customer colgando de B (RLS oculta B → ParentNotFound);
///   (e) NO puede asignar un device al customer B (RLS oculta B → CustomerNotFound);
///   (f) cross-tenant: no ve customers de T2.
/// Y para la raíz: escribe en cualquier nodo del tenant (NO DISRUPTIVO).
///
/// Requiere Docker. Reutiliza el patrón de harness de <see cref="SubtreeIsolationTests"/>.
/// </summary>
public sealed class CustomerHierarchyOperationsTests : IAsyncLifetime
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

    private string _runtimeConnectionString = string.Empty;

    private string _pathRoot1 = string.Empty, _pathA = string.Empty;
    private Guid _idRoot1, _idA, _idA1, _idB, _idRoot2;
    private Guid _devA, _devA1, _devB;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var adminConnectionString = _postgres.GetConnectionString();
        _runtimeConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Username = "iot_app",
            Password = AppRolePassword
        }.ConnectionString;

        await using var admin = BuildContext(adminConnectionString, withInterceptor: false, enforce: false, out _);
        await admin.Database.MigrateAsync();
        await RlsRoleBootstrapper.EnsureAppRoleAsync(
            admin, _runtimeConnectionString, adminConnectionString, NullLogger.Instance);

        admin.Tenants.Add(Tenant.CreateWithId(T1, "Tenant 1", "T-1").Value);
        admin.Tenants.Add(Tenant.CreateWithId(T2, "Tenant 2", "T-2").Value);

        var root1 = Customer.Create(T1, "Raiz T1", parent: null, code: "ROOT1").Value;
        var custA = Customer.Create(T1, "A", parent: root1, code: "A").Value;
        var custA1 = Customer.Create(T1, "A1", parent: custA, code: "A1").Value;
        var custB = Customer.Create(T1, "B", parent: root1, code: "B").Value;
        admin.Customers.AddRange(root1, custA, custA1, custB);

        var root2 = Customer.Create(T2, "Raiz T2", parent: null, code: "ROOT2").Value;
        admin.Customers.Add(root2);

        _pathRoot1 = root1.Path; _pathA = custA.Path;
        _idRoot1 = root1.Id; _idA = custA.Id; _idA1 = custA1.Id; _idB = custB.Id; _idRoot2 = root2.Id;

        _devA = AddDevice(admin, T1, "CHIP-A", "AA:00:00:00:00:02", custA.Id);
        _devA1 = AddDevice(admin, T1, "CHIP-A1", "AA:00:00:00:00:03", custA1.Id);
        _devB = AddDevice(admin, T1, "CHIP-B", "AA:00:00:00:00:04", custB.Id);

        await admin.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private static Guid AddDevice(ApplicationDbContext ctx, Guid tenantId, string chip, string mac, Guid customerId)
    {
        var device = Device.Create(tenantId, chip, mac, chip, "Model", "Mfr").Value;
        device.AssignToCustomer(customerId);
        ctx.Devices.Add(device);
        return device.Id;
    }

    // ---- (a) crear hijo de A + (b) listar subárbol (ve A, A1, hijos; no B ni raíz) + (f) cross-tenant ----

    [Fact]
    public async Task BoundedToA_CreatesChild_ListsOwnSubtree_NotSiblingOrOtherTenant()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerId = _idA;
        tenant.CustomerPath = _pathA;

        var customerRepo = new CustomerRepository(ctx, tenant);
        var createHandler = new CreateCustomerCommandHandler(customerRepo, tenant, ctx);
        var listHandler = new GetCustomersQueryHandler(customerRepo, tenant);
        var byIdHandler = new GetCustomerByIdQueryHandler(customerRepo, tenant);

        // (a.1) parent explícito = A → OK, hijo de A.
        var created = await createHandler.Handle(new CreateCustomerCommand(_idA, "A-child", "A-CHILD"), CancellationToken.None);
        created.IsSuccess.Should().BeTrue("crear un hijo de A desde una sesión acotada a A cae dentro del subárbol");
        created.Value.ParentCustomerId.Should().Be(_idA);

        // (a.2) parent por defecto = nodo de la sesión (A).
        var createdDefault = await createHandler.Handle(new CreateCustomerCommand(null, "A-child-2", "A-CHILD-2"), CancellationToken.None);
        createdDefault.IsSuccess.Should().BeTrue();
        createdDefault.Value.ParentCustomerId.Should().Be(_idA, "sin parent explícito, el padre es el nodo de la sesión");

        // (b) listar: ve A, A1 y los dos hijos nuevos; NO la raíz ni el hermano B.
        var list = await listHandler.Handle(new GetCustomersQuery(), CancellationToken.None);
        list.IsSuccess.Should().BeTrue();
        list.Value.Select(c => c.Code).Should().BeEquivalentTo(
            new[] { "A", "A1", "A-CHILD", "A-CHILD-2" },
            "el subárbol visible de A no incluye la raíz ROOT1 ni el hermano B");

        // (f) cross-tenant y hermano: GetById de B y del raíz de T2 → NotFound.
        (await byIdHandler.Handle(new GetCustomerByIdQuery(_idB), CancellationToken.None)).IsFailure.Should().BeTrue();
        (await byIdHandler.Handle(new GetCustomerByIdQuery(_idRoot2), CancellationToken.None)).IsFailure.Should().BeTrue();
    }

    // ---- (c) reasignar device propio a A1 (dentro del subárbol) --------------------------------

    [Fact]
    public async Task BoundedToA_AssignsOwnDevice_ToA1()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerId = _idA;
        tenant.CustomerPath = _pathA;
        tenant.CustomerSubtreeIds = new[] { _idA, _idA1 }; // lo que resolvería el middleware para A

        var customerRepo = new CustomerRepository(ctx, tenant);
        var deviceRepo = new DeviceRepository(ctx);
        var assignHandler = new AssignDeviceToCustomerCommandHandler(deviceRepo, customerRepo, tenant, ctx);

        var result = await assignHandler.Handle(new AssignDeviceToCustomerCommand(_devA, _idA1), CancellationToken.None);
        result.IsSuccess.Should().BeTrue("A1 desciende de A: la reasignación cae dentro del subárbol (WITH CHECK OK)");

        // Confirmar la persistencia en scope de sistema.
        tenant.IsSystemOperation = true; tenant.TenantId = Guid.Empty; tenant.CustomerPath = null; tenant.CustomerSubtreeIds = null;
        var reloaded = await ctx.Devices.IgnoreQueryFilters().FirstAsync(d => d.Id == _devA);
        reloaded.CustomerId.Should().Be(_idA1);
    }

    // ---- (d) no crear customer bajo B; (e) no asignar device a B (RLS oculta B) ----------------

    [Fact]
    public async Task BoundedToA_CannotCreateUnderSibling_NorAssignDeviceToSibling()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerId = _idA;
        tenant.CustomerPath = _pathA;
        tenant.CustomerSubtreeIds = new[] { _idA, _idA1 };

        var customerRepo = new CustomerRepository(ctx, tenant);
        var deviceRepo = new DeviceRepository(ctx);
        var createHandler = new CreateCustomerCommandHandler(customerRepo, tenant, ctx);
        var assignHandler = new AssignDeviceToCustomerCommandHandler(deviceRepo, customerRepo, tenant, ctx);

        // (d) parent = B (hermano): la RLS oculta B → el handler no lo encuentra.
        var create = await createHandler.Handle(new CreateCustomerCommand(_idB, "colgado-de-B", "ROGUE"), CancellationToken.None);
        create.IsFailure.Should().BeTrue();
        create.Error.Code.Should().Be("Customer.ParentNotFound", "B está fuera del subárbol de A y es invisible");

        // (e) asignar device propio al customer B: B invisible → CustomerNotFound.
        var assign = await assignHandler.Handle(new AssignDeviceToCustomerCommand(_devA, _idB), CancellationToken.None);
        assign.IsFailure.Should().BeTrue();
        assign.Error.Code.Should().Be("Device.CustomerNotFound", "el customer B destino está fuera del subárbol de A");

        // El device del hermano B es, además, invisible como origen (no reasignable desde A).
        var assignForeignDevice = await assignHandler.Handle(new AssignDeviceToCustomerCommand(_devB, _idA1), CancellationToken.None);
        assignForeignDevice.IsFailure.Should().BeTrue();
        assignForeignDevice.Error.Code.Should().Be("Device.NotFound", "el device del hermano B no es visible desde A");
    }

    // ---- raíz: NO DISRUPTIVO (crea bajo cualquier nodo del tenant, incluido B) -----------------

    [Fact]
    public async Task Root_CreatesUnderAnyNode_AndSeesWholeTenant()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerId = _idRoot1;
        tenant.CustomerPath = null; // raíz / token antiguo: sin recorte por subárbol

        var customerRepo = new CustomerRepository(ctx, tenant);
        var createHandler = new CreateCustomerCommandHandler(customerRepo, tenant, ctx);
        var listHandler = new GetCustomersQueryHandler(customerRepo, tenant);

        // La raíz puede colgar un nodo de B (gestiona todo el tenant).
        var created = await createHandler.Handle(new CreateCustomerCommand(_idB, "bajo-B", "UNDER-B"), CancellationToken.None);
        created.IsSuccess.Should().BeTrue("la raíz no está restringida por subárbol (NO DISRUPTIVO)");

        // La raíz ve todo el tenant (4 originales + el nuevo).
        var list = await listHandler.Handle(new GetCustomersQuery(), CancellationToken.None);
        list.Value.Select(c => c.Code).Should().BeEquivalentTo(
            new[] { "ROOT1", "A", "A1", "B", "UNDER-B" });
    }

    // ---- update: sesión acotada a A renombra/desactiva un nodo propio (A1), NO el hermano B -------

    [Fact]
    public async Task BoundedToA_UpdatesOwnNonRootNode_NotSibling()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerId = _idA;
        tenant.CustomerPath = _pathA;

        var customerRepo = new CustomerRepository(ctx, tenant);
        var updateHandler = new UpdateCustomerCommandHandler(customerRepo, tenant, ctx);
        var byIdHandler = new GetCustomerByIdQueryHandler(customerRepo, tenant);

        // Renombrar y desactivar A1 (nodo propio, no raíz) → OK.
        var ok = await updateHandler.Handle(new UpdateCustomerCommand(_idA1, "A1-renombrado", false), CancellationToken.None);
        ok.IsSuccess.Should().BeTrue("A1 está en el subárbol de A y no es raíz del tenant");
        ok.Value.Name.Should().Be("A1-renombrado");
        ok.Value.IsActive.Should().BeFalse();

        // GetById y Update del hermano B → NotFound (RLS + defensa en profundidad).
        (await byIdHandler.Handle(new GetCustomerByIdQuery(_idB), CancellationToken.None)).IsFailure.Should().BeTrue();
        var updB = await updateHandler.Handle(new UpdateCustomerCommand(_idB, "secuestrado", null), CancellationToken.None);
        updB.IsFailure.Should().BeTrue();
        updB.Error.Code.Should().Be("Customer.NotFound", "B está fuera del subárbol de A y es invisible");
    }

    // ---- guarda de raíz: ni siquiera la raíz puede desactivar el nodo raíz del tenant -------------

    [Fact]
    public async Task Root_CannotDeactivateTenantRoot_ButCanRenameIt()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerId = _idRoot1;
        tenant.CustomerPath = null; // raíz: sin recorte por subárbol

        var customerRepo = new CustomerRepository(ctx, tenant);
        var updateHandler = new UpdateCustomerCommandHandler(customerRepo, tenant, ctx);

        // Desactivar el raíz del tenant → bloqueado por la guarda.
        var deactivate = await updateHandler.Handle(new UpdateCustomerCommand(_idRoot1, null, false), CancellationToken.None);
        deactivate.IsFailure.Should().BeTrue();
        deactivate.Error.Code.Should().Be("Customer.CannotDeactivateRoot");

        // Renombrar el raíz sí se permite.
        var rename = await updateHandler.Handle(new UpdateCustomerCommand(_idRoot1, "Raiz T1 renombrada", null), CancellationToken.None);
        rename.IsSuccess.Should().BeTrue();
        rename.Value.Name.Should().Be("Raiz T1 renombrada");

        // Confirmar en scope de sistema que el raíz sigue activo.
        tenant.IsSystemOperation = true; tenant.TenantId = Guid.Empty; tenant.CustomerPath = null; tenant.CustomerSubtreeIds = null;
        var reloaded = await ctx.Customers.IgnoreQueryFilters().FirstAsync(c => c.Id == _idRoot1);
        reloaded.IsActive.Should().BeTrue();
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
