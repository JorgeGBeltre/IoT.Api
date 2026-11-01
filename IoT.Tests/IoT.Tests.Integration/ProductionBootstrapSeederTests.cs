using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// B2 (alta de tenant+admin de producción). Criterio: en Production, una base de datos LIMPIA
/// debe quedar con UNA organización y UN administrador —tomados de <c>Bootstrap:Tenants</c>— con
/// los que iniciar sesión, de forma IDEMPOTENTE y SIN datos de demostración.
///
/// Levanta un PostgreSQL+PostGIS real con Testcontainers y corre el <see cref="DatabaseSeeder"/>
/// de producción tal como lo hace Program.cs: contexto ADMIN (sin interceptor de tenant,
/// superusuario que omite RLS) y entorno "Production". Los hashers/servicios se sustituyen por
/// stubs porque lo que se prueba es la ORQUESTACIÓN del seeder (crear/idempotencia), no BCrypt/SHA.
///
/// Requiere Docker. Si no hay Docker, el arranque del contenedor falla y la clase se reporta como
/// error (no como passed en falso).
/// </summary>
public sealed class ProductionBootstrapSeederTests : IAsyncLifetime
{
    private const string BootCode = "T-BOOT";
    private const string BootName = "Organizacion Bootstrap";
    private const string BootAdminUser = "bootadmin";
    private const string BootAdminEmail = "admin@boot.test";
    private const string SeedPassword = "Str0ng-Boot-Pass-2026!";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgis/postgis:16-3.4")
        .WithDatabase("iot_platform_db")
        .WithUsername("iot_admin_test")
        .WithPassword("Iot_Admin_Test_2026")
        .Build();

    private string _adminConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _adminConnectionString = _postgres.GetConnectionString();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task CleanProductionDb_CreatesOrganizationAndAdmin_ReadyToLogIn()
    {
        await using (var ctx = NewAdminContext())
        {
            await NewSeeder(ctx).SeedAsync();
        }

        await using var verify = NewAdminContext();

        var tenant = await verify.Tenants.IgnoreQueryFilters()
            .SingleOrDefaultAsync(t => t.Code == BootCode);
        tenant.Should().NotBeNull("el bootstrap debe crear la organización en una BD limpia de producción");

        var admin = await verify.Users.IgnoreQueryFilters()
            .SingleOrDefaultAsync(u => u.Username == BootAdminUser);
        admin.Should().NotBeNull("el bootstrap debe crear el administrador inicial");
        admin!.TenantId.Should().Be(tenant!.Id, "el admin pertenece a su organización");
        admin.Email.Should().Be(BootAdminEmail, "el email se normaliza en minúsculas");
        admin.RoleName.Should().Be("Admin", "el admin necesita rol Admin para administrar e iniciar sesión");
        admin.MustChangePassword.Should().BeTrue("en producción se exige cambiar la contraseña en el primer login");
        admin.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SecondRun_IsIdempotent_DoesNotDuplicateNorResetPassword()
    {
        await using (var ctx = NewAdminContext())
        {
            await NewSeeder(ctx).SeedAsync();
        }

        Guid adminIdAfterFirst;
        string hashAfterFirst;
        await using (var afterFirst = NewAdminContext())
        {
            var admin = await afterFirst.Users.IgnoreQueryFilters().SingleAsync(u => u.Username == BootAdminUser);
            adminIdAfterFirst = admin.Id;
            hashAfterFirst = admin.PasswordHash;
        }

        // Segundo arranque (reinicio del servicio): debe ser un no-op sobre lo ya creado.
        await using (var ctx2 = NewAdminContext())
        {
            await NewSeeder(ctx2).SeedAsync();
        }

        await using var verify = NewAdminContext();
        (await verify.Tenants.IgnoreQueryFilters().CountAsync(t => t.Code == BootCode))
            .Should().Be(1, "un reinicio no debe duplicar la organización");

        var admins = await verify.Users.IgnoreQueryFilters()
            .Where(u => u.Username == BootAdminUser).ToListAsync();
        admins.Should().HaveCount(1, "un reinicio no debe duplicar el administrador");
        admins[0].Id.Should().Be(adminIdAfterFirst, "debe ser la MISMA fila (no recreada)");
        admins[0].PasswordHash.Should().Be(hashAfterFirst, "un reinicio NO debe resetear la contraseña del admin");
    }

    private ApplicationDbContext NewAdminContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_adminConnectionString, npgsql => npgsql.UseNetTopologySuite())
            .Options;
        // tenantContext: null => contexto admin sin interceptor (omite RLS), como en Program.cs.
        return new ApplicationDbContext(options, tenantContext: null);
    }

    private static DatabaseSeeder NewSeeder(ApplicationDbContext ctx) =>
        new DatabaseSeeder(
            ctx,
            new StubPasswordHasher(),
            new StubApiKeyService(),
            BuildConfig(),
            new StubEnvironment("Production"),
            NullLogger<DatabaseSeeder>.Instance);

    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seed:AdminPassword"] = SeedPassword,
                ["Bootstrap:Tenants:0:Code"] = BootCode,
                ["Bootstrap:Tenants:0:Name"] = BootName,
                ["Bootstrap:Tenants:0:AdminUsername"] = BootAdminUser,
                ["Bootstrap:Tenants:0:AdminEmail"] = BootAdminEmail,
            })
            .Build();

    // --- Stubs: el foco es la orquestación del seeder, no las primitivas criptográficas reales ---

    private sealed class StubPasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => "stub-hash:" + password;
        public bool Verify(string password, string hash) => hash == "stub-hash:" + password;
        public void ValidateComplexity(string password) { }
    }

    private sealed class StubApiKeyService : IDeviceApiKeyService
    {
        public (string PlainKey, string Hash, string Fingerprint) Generate() => ("plain", "hash", "ffffffff");
        public bool Verify(string plainKey, string hash) => true;
        public string ComputeHash(string plainKey) => "stub-" + plainKey;
        public (string Hash, string Fingerprint) DeriveFromKnownKey(string plainKey) => ("stub-" + plainKey, "ffffffff");
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public StubEnvironment(string environmentName) => EnvironmentName = environmentName;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "IoT.Tests";
        public string ContentRootPath { get; set; } = System.AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
