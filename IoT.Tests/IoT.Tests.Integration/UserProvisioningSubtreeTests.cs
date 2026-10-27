using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using IoT.Application.Auth.Commands;
using IoT.Application.Common.Interfaces;
using IoT.Application.Users.Commands;
using IoT.Application.Users.Dtos;
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
/// Criterio de aceptación de E8.6a con la RLS real (rol NOBYPASSRLS <c>iot_app</c>, enforce=true): un
/// administrador acotado a un subárbol crea un usuario asignado a un nodo DENTRO de su subárbol, con
/// una contraseña temporal; el usuario queda confinado al subárbol y puede AUTENTICARSE, y su token
/// lleva <c>customer_id</c>/<c>customer_path</c> = nodo destino y <c>must_change_password</c>. Crear un
/// usuario en el hermano o en otro tenant se RECHAZA (la RLS oculta el nodo destino → el handler no lo
/// resuelve).
///
/// Topología T1: Raíz → A → A1, y B (hermano de A); T2 independiente. Reutiliza el patrón de harness de
/// <see cref="CustomerHierarchyOperationsTests"/>/<see cref="SubtreeIsolationTests"/>.
///
/// Requiere Docker.
/// </summary>
public sealed class UserProvisioningSubtreeTests : IAsyncLifetime
{
    private static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid T2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string AppRolePassword = "Iot_App_Test_Role_2026";
    private const string JwtSecret = "ThisIsAValidLongSecretKeyForJwt2026EnterprisePlatform!";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgis/postgis:16-3.4")
        .WithDatabase("iot_platform_db")
        .WithUsername("iot_admin_test")
        .WithPassword("Iot_Admin_Test_2026")
        .Build();

    private string _runtimeConnectionString = string.Empty;

    private string _pathA = string.Empty, _pathA1 = string.Empty;
    private Guid _idRoot1, _idA, _idA1, _idB, _idRoot2;

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

        _pathA = custA.Path; _pathA1 = custA1.Path;
        _idRoot1 = root1.Id; _idA = custA.Id; _idA1 = custA1.Id; _idB = custB.Id; _idRoot2 = root2.Id;

        await admin.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    // ---- (OK) admin acotado a A crea un usuario en A1 (dentro del subárbol); el usuario puede login
    //      y su token lleva customer_id/customer_path = A1 y must_change_password. -----------------

    [Fact]
    public async Task BoundedToA_CreatesUserInA1_PersistsWithCustomerIdA1_AndUserCanLoginWithSubtreeClaims()
    {
        CreateUserResult created;

        await using (var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant))
        {
            tenant.TenantId = T1;
            tenant.CustomerId = _idA;
            tenant.CustomerPath = _pathA;

            var handler = BuildCreateUserHandler(ctx, tenant);
            var result = await handler.Handle(
                new CreateUserCommand("driver_a1", "driver_a1@t1.com", "Driver A1", "Operator", _idA1),
                CancellationToken.None);

            result.IsSuccess.Should().BeTrue("A1 desciende de A: el alta cae dentro del subárbol (WITH CHECK OK)");
            created = result.Value;
            created.CustomerId.Should().Be(_idA1);
            created.MustChangePassword.Should().BeTrue();
            created.RoleName.Should().Be("Operator");
            created.TemporaryPassword.Should().NotBeNullOrWhiteSpace();
        }

        // Persistencia en scope de sistema: la fila existe con CustomerId = A1 y MustChangePassword.
        await using (var ctxSys = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tSys))
        {
            tSys.TenantId = Guid.Empty;
            tSys.IsSystemOperation = true;
            var row = await ctxSys.Users.IgnoreQueryFilters().FirstAsync(u => u.Username == "driver_a1");
            row.CustomerId.Should().Be(_idA1);
            row.MustChangePassword.Should().BeTrue();
            row.RoleName.Should().Be("Operator");
            row.PasswordHash.Should().NotBe(created.TemporaryPassword, "se guarda el hash, no el texto plano");
        }

        // El usuario creado puede AUTENTICARSE con la contraseña temporal; el token lleva el nodo A1.
        await using (var ctxLogin = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tLogin))
        {
            var login = BuildLoginHandler(ctxLogin, tLogin);
            var loginResult = await login.Handle(
                new LoginCommand("driver_a1", created.TemporaryPassword, T1), CancellationToken.None);

            loginResult.IsSuccess.Should().BeTrue("la contraseña temporal es válida para iniciar sesión");
            loginResult.Value.MustChangePassword.Should().BeTrue("debe forzarse el cambio de la clave temporal");

            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(loginResult.Value.Token);
            jwt.Claims.Should().Contain(c => c.Type == "customer_id" && c.Value == _idA1.ToString(),
                "el token acota al usuario a su nodo A1");
            jwt.Claims.Should().Contain(c => c.Type == "customer_path" && c.Value == _pathA1,
                "el customer_path firmado es el de A1 (resuelto en el servidor)");
            jwt.Claims.Should().Contain(c => c.Type == "must_change_password" && c.Value == "true");
        }
    }

    // ---- (RECHAZADO) admin acotado a A NO puede crear un usuario en el hermano B ------------------

    [Fact]
    public async Task BoundedToA_CannotCreateUserInSibling_B()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerId = _idA;
        tenant.CustomerPath = _pathA;

        var handler = BuildCreateUserHandler(ctx, tenant);
        var result = await handler.Handle(
            new CreateUserCommand("intruso_b", "intruso_b@t1.com", "Intruso B", "Operator", _idB),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("User.TargetCustomerNotFound",
            "la RLS oculta el hermano B desde A → el customer destino no se resuelve");

        // No se persiste ninguna fila (scope de sistema).
        await using var ctxSys = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tSys);
        tSys.TenantId = Guid.Empty;
        tSys.IsSystemOperation = true;
        (await ctxSys.Users.IgnoreQueryFilters().AnyAsync(u => u.Username == "intruso_b")).Should().BeFalse();
    }

    // ---- (RECHAZADO) cross-tenant: admin de T1 no puede crear un usuario en el raíz de T2 ---------

    [Fact]
    public async Task BoundedToA_CannotCreateUserCrossTenant()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerId = _idA;
        tenant.CustomerPath = _pathA;

        var handler = BuildCreateUserHandler(ctx, tenant);
        var result = await handler.Handle(
            new CreateUserCommand("cross", "cross@t2.com", "Cross Tenant", "Viewer", _idRoot2),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("User.TargetCustomerNotFound",
            "el nodo de T2 es invisible desde T1 (aislamiento por tenant + subárbol)");

        await using var ctxSys = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tSys);
        tSys.TenantId = Guid.Empty;
        tSys.IsSystemOperation = true;
        (await ctxSys.Users.IgnoreQueryFilters().AnyAsync(u => u.Username == "cross")).Should().BeFalse();
    }

    // ---- (RECHAZADO sin 500) unicidad TENANT-WIDE: admin acotado a A NO puede crear un username que ya
    //      existe en el subárbol HERMANO B (invisible por RLS). El pre-check corre en scope de sistema y
    //      ve todo el tenant → User.UsernameTaken (NO excepción/500) y NO se persiste una segunda fila. -
    [Fact]
    public async Task BoundedToA_CreatingUsernameTakenInSiblingB_ReturnsUsernameTaken_WithoutException_AndDoesNotPersistSecondRow()
    {
        const string username = "colision_x";

        // Precondición: existe un usuario con ese username en el subárbol HERMANO B. Se siembra en scope
        // de sistema (la RLS lo permite bajo app.is_system_session). Para un admin acotado a A, esta fila
        // es INVISIBLE por la RLS (customer_path de A no cubre a B).
        await using (var seed = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tSeed))
        {
            tSeed.TenantId = Guid.Empty;
            tSeed.IsSystemOperation = true;

            var userInB = User.Create(
                T1, username, "colision_b@t1.com",
                new BCryptPasswordHasher().Hash("Temp_Colision_2026!"),
                "Colision B", "Operator", mustChangePassword: true).Value;
            userInB.AssignToCustomer(_idB);
            seed.Users.Add(userInB);
            await seed.SaveChangesAsync();
        }

        // Acción: admin ACOTADO a A intenta crear el MISMO username en A1 (dentro de su subárbol). El
        // índice único (TenantId, Username) es tenant-wide: aunque B sea invisible por RLS, el pre-check
        // tenant-wide (scope de sistema) detecta la colisión y rechaza SIN excepción ni 500.
        await using (var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant))
        {
            tenant.TenantId = T1;
            tenant.CustomerId = _idA;
            tenant.CustomerPath = _pathA;

            var handler = BuildCreateUserHandler(ctx, tenant);

            var act = async () => await handler.Handle(
                new CreateUserCommand(username, "colision_a1@t1.com", "Colision A1", "Operator", _idA1),
                CancellationToken.None);

            var result = await act.Should().NotThrowAsync(
                "la colisión de unicidad tenant-wide debe resolverse como fallo de negocio, nunca como excepción/500");

            result.Subject.IsFailure.Should().BeTrue();
            result.Subject.Error.Code.Should().Be("User.UsernameTaken",
                "el username ya existe en el tenant (subárbol hermano B), según el índice único tenant-wide");
        }

        // No se persiste una segunda fila: sigue existiendo EXACTAMENTE una, la sembrada en B.
        await using (var ctxSys = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tSys))
        {
            tSys.TenantId = Guid.Empty;
            tSys.IsSystemOperation = true;

            var rows = await ctxSys.Users.IgnoreQueryFilters()
                .Where(u => u.Username == username)
                .ToListAsync();

            rows.Should().ContainSingle("no debe insertarse una segunda fila con el mismo username en el tenant");
            rows[0].CustomerId.Should().Be(_idB, "la única fila es la sembrada en el hermano B");
        }
    }

    private static CreateUserCommandHandler BuildCreateUserHandler(ApplicationDbContext ctx, ITenantContext tenant) =>
        new(
            new UserRepository(ctx),
            new CustomerRepository(ctx, tenant),
            tenant,
            new BCryptPasswordHasher(),
            new AuditLogRepository(ctx),
            ctx,
            NullLogger<CreateUserCommandHandler>.Instance);

    private static LoginCommandHandler BuildLoginHandler(ApplicationDbContext ctx, ITenantContext tenant)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SecretKey"] = JwtSecret })
            .Build();

        return new LoginCommandHandler(
            new UserRepository(ctx),
            new BCryptPasswordHasher(),
            new JwtTokenGenerator(config),
            new AuditLogRepository(ctx),
            ctx,
            NullLogger<LoginCommandHandler>.Instance,
            tenant,
            new CustomerRepository(ctx, tenant));
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
