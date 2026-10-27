using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Application.Devices.Queries;
using IoT.Domain.Entities;
using IoT.Infrastructure.Persistence;
using IoT.Infrastructure.Persistence.Interceptors;
using IoT.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Criterio de aceptación del incremento Device→Customer en el DTO de dispositivo (con la RLS real:
/// rol NOBYPASSRLS <c>iot_app</c>, enforce=true): el GET de DETALLE (<see cref="GetDeviceByIdQuery"/>)
/// y el de LISTA (<see cref="GetDevicesQuery"/>) exponen <c>CustomerId</c> y <c>CustomerName</c> del
/// nodo dueño, resueltos RESPETANDO los filtros (RLS por subárbol + EF por tenant, SIN
/// IgnoreQueryFilters). Verifica además que un visor ACOTADO ve el nombre del customer de SU device
/// (en su subárbol) sin exponer datos de otro subárbol ni de otro tenant.
///
/// Topología T1: Raíz → A → A1, y B (hermano de A), con un device en A, A1 y B; T2 independiente con
/// su raíz y un device. Reutiliza el patrón de harness de <see cref="SubtreeIsolationTests"/> y
/// <see cref="CustomerHierarchyOperationsTests"/>.
///
/// Requiere Docker. Sin Docker, el arranque del contenedor falla y la clase se reporta como error.
/// </summary>
public sealed class DeviceDetailCustomerDtoTests : IAsyncLifetime
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

    private string _pathA = string.Empty, _pathA1 = string.Empty, _pathRoot2 = string.Empty;
    private Guid _idRoot1, _idA, _idA1, _idB, _idRoot2;
    private Guid _devA, _devA1, _devB, _devT2;

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

        _pathA = custA.Path; _pathA1 = custA1.Path; _pathRoot2 = root2.Path;
        _idRoot1 = root1.Id; _idA = custA.Id; _idA1 = custA1.Id; _idB = custB.Id; _idRoot2 = root2.Id;

        _devA = AddDevice(admin, T1, "CHIP-A", "AA:00:00:00:00:02", custA.Id);
        _devA1 = AddDevice(admin, T1, "CHIP-A1", "AA:00:00:00:00:03", custA1.Id);
        _devB = AddDevice(admin, T1, "CHIP-B", "AA:00:00:00:00:04", custB.Id);
        _devT2 = AddDevice(admin, T2, "CHIP-T2", "BB:00:00:00:00:01", root2.Id);

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

    // ---- (1) tras asignar un device a un customer del subárbol, el GET de DETALLE devuelve
    //          CustomerId y CustomerName correctos (y reflejan la reasignación). -------------------

    [Fact]
    public async Task Detail_AfterAssign_ReturnsCustomerIdAndName()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerId = _idA;
        tenant.CustomerPath = _pathA;
        tenant.CustomerSubtreeIds = new[] { _idA, _idA1 }; // lo que resolvería el middleware para A

        var customerRepo = new CustomerRepository(ctx, tenant);
        var deviceRepo = new DeviceRepository(ctx);
        var detailHandler = new GetDeviceByIdQueryHandler(deviceRepo, customerRepo, new Mock<IStateCacheService>().Object);
        var assignHandler = new AssignDeviceToCustomerCommandHandler(deviceRepo, customerRepo, tenant, ctx);

        // Estado inicial: _devA cuelga de A → el detalle muestra el nombre "A".
        var before = await detailHandler.Handle(new GetDeviceByIdQuery(T1, _devA), CancellationToken.None);
        before.IsSuccess.Should().BeTrue();
        before.Value.CustomerId.Should().Be(_idA);
        before.Value.CustomerName.Should().Be("A", "el detalle expone el nombre del nodo dueño actual");

        // Reasignar _devA a A1 (dentro del subárbol).
        var assign = await assignHandler.Handle(new AssignDeviceToCustomerCommand(_devA, _idA1), CancellationToken.None);
        assign.IsSuccess.Should().BeTrue("A1 desciende de A: la reasignación cae dentro del subárbol");

        // Tras asignar, el detalle refleja el nuevo nodo dueño (ya no "Sin asignar").
        var after = await detailHandler.Handle(new GetDeviceByIdQuery(T1, _devA), CancellationToken.None);
        after.IsSuccess.Should().BeTrue();
        after.Value.CustomerId.Should().Be(_idA1, "el DTO expone el CustomerId reasignado");
        after.Value.CustomerName.Should().Be("A1", "el DTO expone el nombre del customer reasignado");
    }

    // ---- (2) un visor ACOTADO ve el nombre del customer de SU device (en su subárbol) sin exponer
    //          datos de otro subárbol (hermano B / ancestro A) ni de otro tenant (T2). --------------

    [Fact]
    public async Task ScopedViewer_SeesOwnDeviceCustomerName_NotOtherSubtreeOrTenant()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerId = _idA1;
        tenant.CustomerPath = _pathA1;        // acotado a la HOJA A1
        tenant.CustomerSubtreeIds = new[] { _idA1 };

        var customerRepo = new CustomerRepository(ctx, tenant);
        var deviceRepo = new DeviceRepository(ctx);
        var detailHandler = new GetDeviceByIdQueryHandler(deviceRepo, customerRepo, new Mock<IStateCacheService>().Object);
        var listHandler = new GetDevicesQueryHandler(deviceRepo, customerRepo, new Mock<IStateCacheService>().Object);

        // Ve el nombre del customer de SU propio device (A1 está en su subárbol).
        var ownDetail = await detailHandler.Handle(new GetDeviceByIdQuery(T1, _devA1), CancellationToken.None);
        ownDetail.IsSuccess.Should().BeTrue();
        ownDetail.Value.CustomerId.Should().Be(_idA1);
        ownDetail.Value.CustomerName.Should().Be("A1");

        // El device del ancestro A (fuera del subárbol de A1) y el del hermano B son INVISIBLES:
        // el detalle no los devuelve (NotFound), así que su nombre de customer no se puede filtrar.
        (await detailHandler.Handle(new GetDeviceByIdQuery(T1, _devA), CancellationToken.None))
            .IsFailure.Should().BeTrue("el device del ancestro A está fuera del subárbol de A1");
        (await detailHandler.Handle(new GetDeviceByIdQuery(T1, _devB), CancellationToken.None))
            .IsFailure.Should().BeTrue("el device del hermano B está fuera del subárbol de A1");

        // Cross-tenant: el device de T2 nunca es visible desde T1.
        (await detailHandler.Handle(new GetDeviceByIdQuery(T1, _devT2), CancellationToken.None))
            .IsFailure.Should().BeTrue("un device de otro tenant nunca es visible");

        // La propia consulta de nombre (GetByIdScopedAsync) tampoco alcanza customers fuera del
        // subárbol: ni el ancestro A, ni el hermano B, ni la raíz de T2 → null (no hay fuga de nombre).
        (await customerRepo.GetByIdScopedAsync(_idA, CancellationToken.None)).Should().BeNull("A es ancestro, fuera del subárbol de A1");
        (await customerRepo.GetByIdScopedAsync(_idB, CancellationToken.None)).Should().BeNull("B es hermano, fuera del subárbol de A1");
        (await customerRepo.GetByIdScopedAsync(_idRoot2, CancellationToken.None)).Should().BeNull("el raíz de T2 es de otro tenant");

        // La LISTA del visor acotado sólo contiene SU device con su nombre; nunca B, A ni la raíz.
        var list = await listHandler.Handle(new GetDevicesQuery(T1), CancellationToken.None);
        list.IsSuccess.Should().BeTrue();
        list.Value.Select(x => x.ChipId).Should().BeEquivalentTo(new[] { "CHIP-A1" });
        list.Value.Single().CustomerName.Should().Be("A1");
        list.Value.Should().NotContain(x => x.CustomerName == "B" || x.CustomerName == "A" || x.CustomerName == "Raiz T1",
            "la lista no expone nombres de customers fuera del subárbol del visor");
    }

    // ---- (3) cross-tenant en la LISTA: el visor de T2 sólo ve su device con su nombre. -----------

    [Fact]
    public async Task List_CrossTenant_OnlySeesOwnTenantDeviceAndName()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T2;
        tenant.CustomerId = _idRoot2;
        tenant.CustomerPath = null; // raíz de T2: sin recorte por subárbol dentro de su tenant

        var customerRepo = new CustomerRepository(ctx, tenant);
        var deviceRepo = new DeviceRepository(ctx);
        var listHandler = new GetDevicesQueryHandler(deviceRepo, customerRepo, new Mock<IStateCacheService>().Object);

        var list = await listHandler.Handle(new GetDevicesQuery(T2), CancellationToken.None);
        list.IsSuccess.Should().BeTrue();
        list.Value.Select(x => x.ChipId).Should().BeEquivalentTo(new[] { "CHIP-T2" },
            "el visor de T2 nunca ve dispositivos de T1");
        list.Value.Single().CustomerName.Should().Be("Raiz T2");
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
