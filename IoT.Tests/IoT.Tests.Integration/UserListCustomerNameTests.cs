using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Infrastructure.Persistence;
using IoT.Infrastructure.Persistence.Interceptors;
using IoT.Infrastructure.Repositories;
using IoT.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// E8.6b: el listado de usuarios (GET /api/v1/users) expone el <c>CustomerName</c> del nodo al que
/// pertenece cada cuenta. Verifica la MISMA composición que hace <c>UsersController.GetAll</c> con la
/// RLS real (rol NOBYPASSRLS <c>iot_app</c>, enforce=true): <c>IUserRepository.GetByTenantAsync</c> +
/// diccionario de <c>ICustomerRepository.GetVisibleAsync</c>, SIN IgnoreQueryFilters.
///
/// Criterio: un admin acotado al subárbol de A ve a los usuarios de A y A1 con el NOMBRE de su cliente
/// resuelto, y NO ve (ni filtra como nombre) al usuario del hermano B. Topología T1: Raíz → A → A1, y
/// B (hermano de A).
///
/// Requiere Docker.
/// </summary>
public sealed class UserListCustomerNameTests : IAsyncLifetime
{
    private static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string AppRolePassword = "Iot_App_Test_Role_2026";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgis/postgis:16-3.4")
        .WithDatabase("iot_platform_db")
        .WithUsername("iot_admin_test")
        .WithPassword("Iot_Admin_Test_2026")
        .Build();

    private string _runtimeConnectionString = string.Empty;
    private string _pathA = string.Empty;
    private Guid _idA, _idA1, _idB;

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

        var root1 = Customer.Create(T1, "Raiz T1", parent: null, code: "ROOT1").Value;
        var custA = Customer.Create(T1, "A", parent: root1, code: "A").Value;
        var custA1 = Customer.Create(T1, "A1", parent: custA, code: "A1").Value;
        var custB = Customer.Create(T1, "B", parent: root1, code: "B").Value;
        admin.Customers.AddRange(root1, custA, custA1, custB);

        _pathA = custA.Path;
        _idA = custA.Id; _idA1 = custA1.Id; _idB = custB.Id;

        var hasher = new BCryptPasswordHasher();
        var userA = User.Create(T1, "user_a", "a@t1.com", hasher.Hash("Temp_A_2026!"), "User A", "Admin", mustChangePassword: true).Value;
        userA.AssignToCustomer(_idA);
        var userA1 = User.Create(T1, "user_a1", "a1@t1.com", hasher.Hash("Temp_A1_2026!"), "User A1", "Operator", mustChangePassword: true).Value;
        userA1.AssignToCustomer(_idA1);
        var userB = User.Create(T1, "user_b", "b@t1.com", hasher.Hash("Temp_B_2026!"), "User B", "Viewer", mustChangePassword: true).Value;
        userB.AssignToCustomer(_idB);
        admin.Users.AddRange(userA, userA1, userB);

        await admin.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task BoundedToA_ListsSubtreeUsersWithCustomerName_AndDoesNotLeakSibling()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerId = _idA;
        tenant.CustomerPath = _pathA;
        // El middleware de producción resuelve el subárbol una vez por request y lo deja aquí; alimenta
        // el filtro global de EF para ICustomerScoped (User/Device). Subárbol de A = { A, A1 }.
        tenant.CustomerSubtreeIds = new[] { _idA, _idA1 };

        var userRepo = new UserRepository(ctx);
        var customerRepo = new CustomerRepository(ctx, tenant);

        // Réplica EXACTA de UsersController.GetAll: lista de usuarios del tenant + diccionario de
        // nombres de los customers VISIBLES (subárbol de la sesión), resuelto en memoria.
        var users = await userRepo.GetByTenantAsync(T1, CancellationToken.None);
        var customerNames = (await customerRepo.GetVisibleAsync(CancellationToken.None))
            .ToDictionary(c => c.Id, c => c.Name);

        string? NameOf(User u) =>
            u.CustomerId is Guid cid && customerNames.TryGetValue(cid, out var name) ? name : null;

        // Sólo el subárbol de A: user_a (A) y user_a1 (A1). El hermano B queda oculto por la RLS.
        users.Select(u => u.Username).Should().BeEquivalentTo(new[] { "user_a", "user_a1" },
            "la RLS acota el listado al subárbol de la sesión; el usuario del hermano B no es visible");

        var a = users.Single(u => u.Username == "user_a");
        var a1 = users.Single(u => u.Username == "user_a1");

        NameOf(a).Should().Be("A", "el nombre del cliente del usuario del nodo A se resuelve a 'A'");
        NameOf(a1).Should().Be("A1", "el nombre del cliente del usuario del nodo A1 se resuelve a 'A1'");

        // El diccionario sólo trae el subárbol visible: no incluye el nombre del hermano B.
        customerNames.Values.Should().NotContain("B", "el nombre del cliente del hermano B no debe filtrarse al listado");
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
