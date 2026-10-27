using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Infrastructure.Persistence;
using IoT.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Criterio de aceptación del BLOQUEADOR DE DESPLIEGUE de E8.4a (migración
/// <c>20261027170000_AddCustomerIdNotNull</c>): el camino de ACTUALIZACIÓN de una base ya poblada
/// (tenants/devices/users existentes con <c>CustomerId</c> NULL, SIN tabla customers) debe migrar a
/// HEAD SIN fallar, porque la migración crea el Customer raíz por tenant ANTES del backfill y del
/// <c>SET NOT NULL</c> (el seeder corre DESPUÉS de MigrateAsync y no puede cubrir este hueco).
///
/// El test:
///  (a) migra SÓLO hasta <c>AddCustomerHierarchy</c> (20261027140809) vía <see cref="IMigrator"/>
///      (estado en el que customers existe VACÍA y devices/users.CustomerId es NULLABLE);
///  (b) inserta por SQL crudo, como rol admin (superusuario → bypassa RLS), un tenant + un device +
///      un user con <c>CustomerId</c> NULL (base previa a E8.4a);
///  (c) aplica el RESTO de migraciones hasta HEAD y asevera que NO lanza, que se creó un Customer
///      raíz para ese tenant, que device y user quedaron con <c>CustomerId</c> = ese raíz, y que la
///      columna es NOT NULL a nivel de base de datos.
///
/// Requiere Docker. Sin Docker el arranque del contenedor falla y la clase se reporta como error.
/// </summary>
public sealed class CustomerIdBackfillUpgradePathTests : IAsyncLifetime
{
    private const string HierarchyMigrationId = "20261027140809_AddCustomerHierarchy";

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
    public async Task UpgradeFromPrePopulatedDb_WithNullCustomerIds_MigratesToHead_AndBackfillsRoot()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // (a) Migrar SÓLO hasta AddCustomerHierarchy (customers vacía, CustomerId aún NULLABLE).
        await using (var ctx = BuildAdminContext())
        {
            var migrator = ctx.GetService<IMigrator>();
            migrator.Migrate(HierarchyMigrationId);
        }

        // (b) Sembrar por SQL crudo (como admin) una base "antigua": tenant + device + user con
        //     CustomerId NULL (columna omitida ⇒ NULL). Columnas NOT NULL añadidas después traen
        //     DEFAULT en la BD, así que basta con las columnas base.
        await using (var conn = new NpgsqlConnection(_adminConnectionString))
        {
            await conn.OpenAsync();

            await ExecuteAsync(conn,
                @"INSERT INTO tenants (""Id"", ""Name"", ""Code"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                  VALUES (@tid, 'Upgrade Tenant', 'UPGRADE-T', true, now(), now());",
                ("tid", tenantId));

            await ExecuteAsync(conn,
                @"INSERT INTO devices (""Id"", ""TenantId"", ""ChipId"", ""MacAddress"", ""Name"", ""Model"", ""Manufacturer"", ""AdminStatus"", ""OperationalStatus"", ""MetadataJson"", ""CreatedAt"", ""UpdatedAt"")
                  VALUES (@did, @tid, 'CHIP-UPGRADE', 'AA:BB:CC:DD:EE:01', 'Upgrade Device', 'Model', 'Mfr', 0, 0, '{}', now(), now());",
                ("did", deviceId), ("tid", tenantId));

            await ExecuteAsync(conn,
                @"INSERT INTO users (""Id"", ""TenantId"", ""Username"", ""Email"", ""PasswordHash"", ""FullName"", ""IsActive"", ""MustChangePassword"", ""FailedLoginAttempts"", ""SecurityStamp"", ""RoleName"", ""CreatedAt"")
                  VALUES (@uid, @tid, 'upgrade_user', 'upgrade@t.com', 'hash', 'Upgrade User', true, false, 0, 'stamp', 'Admin', now());",
                ("uid", userId), ("tid", tenantId));

            // Sanidad: ambas filas tienen CustomerId NULL antes de migrar a HEAD.
            (await ScalarAsync<long>(conn,
                @"SELECT count(*) FROM devices WHERE ""Id"" = @did AND ""CustomerId"" IS NULL;",
                ("did", deviceId))).Should().Be(1);
        }

        // (c) Aplicar el RESTO de migraciones hasta HEAD: NO debe lanzar (antes fallaba 23502 en el
        //     SET NOT NULL por no haber raíz que backfillear).
        await using (var ctx = BuildAdminContext())
        {
            var act = async () => await ctx.Database.MigrateAsync();
            await act.Should().NotThrowAsync(
                "la migración AddCustomerIdNotNull crea el Customer raíz por tenant antes del backfill y del SET NOT NULL");
        }

        // Aserciones del resultado: raíz creado, device/user backfilleados, columna NOT NULL.
        await using (var conn = new NpgsqlConnection(_adminConnectionString))
        {
            await conn.OpenAsync();

            var rootCount = await ScalarAsync<long>(conn,
                @"SELECT count(*) FROM customers WHERE ""TenantId"" = @tid AND ""ParentCustomerId"" IS NULL;",
                ("tid", tenantId));
            rootCount.Should().Be(1, "se creó exactamente un Customer raíz para el tenant huérfano");

            var rootId = await ScalarAsync<Guid>(conn,
                @"SELECT ""Id"" FROM customers WHERE ""TenantId"" = @tid AND ""ParentCustomerId"" IS NULL LIMIT 1;",
                ("tid", tenantId));

            // El raíz imita al seeder: Code=ROOT, Level=0, Path = 'c' + Id hex sin guiones.
            var rootCode = await ScalarAsync<string>(conn,
                @"SELECT ""Code"" FROM customers WHERE ""Id"" = @cid;", ("cid", rootId));
            rootCode.Should().Be("ROOT");

            var rootLevel = await ScalarAsync<int>(conn,
                @"SELECT ""Level"" FROM customers WHERE ""Id"" = @cid;", ("cid", rootId));
            rootLevel.Should().Be(0);

            var rootPath = await ScalarAsync<string>(conn,
                @"SELECT ""Path""::text FROM customers WHERE ""Id"" = @cid;", ("cid", rootId));
            rootPath.Should().Be("c" + rootId.ToString("N"),
                "el Path es la etiqueta ltree 'c' + el mismo Id en hexadecimal sin guiones");

            var deviceCustomerId = await ScalarAsync<Guid>(conn,
                @"SELECT ""CustomerId"" FROM devices WHERE ""Id"" = @did;", ("did", deviceId));
            deviceCustomerId.Should().Be(rootId, "el device huérfano quedó asignado al Customer raíz");

            var userCustomerId = await ScalarAsync<Guid>(conn,
                @"SELECT ""CustomerId"" FROM users WHERE ""Id"" = @uid;", ("uid", userId));
            userCustomerId.Should().Be(rootId, "el user huérfano quedó asignado al Customer raíz");

            var deviceNullable = await ScalarAsync<string>(conn,
                @"SELECT is_nullable FROM information_schema.columns
                  WHERE table_name = 'devices' AND column_name = 'CustomerId';");
            deviceNullable.Should().Be("NO", "devices.CustomerId quedó NOT NULL");

            var userNullable = await ScalarAsync<string>(conn,
                @"SELECT is_nullable FROM information_schema.columns
                  WHERE table_name = 'users' AND column_name = 'CustomerId';");
            userNullable.Should().Be("NO", "users.CustomerId quedó NOT NULL");
        }
    }

    private ApplicationDbContext BuildAdminContext()
    {
        var tenant = new SystemTenantContext();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_adminConnectionString, npgsql => npgsql.UseNetTopologySuite())
            .Options;
        return new ApplicationDbContext(options, tenant);
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        if (result is T typed)
            return typed;
        return (T)Convert.ChangeType(result!, typeof(T));
    }

    /// <summary>Contexto de tenant de SISTEMA (sin interceptor en uso; el admin del contenedor es superusuario y bypassa RLS).</summary>
    private sealed class SystemTenantContext : ITenantContext
    {
        public Guid TenantId { get; set; } = Guid.Empty;
        public Guid? CustomerId { get; set; }
        public string? CustomerPath { get; set; }
        public IReadOnlyCollection<Guid>? CustomerSubtreeIds { get; set; }
        public bool IsSystemOperation { get; set; } = true;
    }
}
