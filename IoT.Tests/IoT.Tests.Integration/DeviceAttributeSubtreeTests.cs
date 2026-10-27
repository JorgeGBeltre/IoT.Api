using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Application.Devices.Queries;
using IoT.Application.Ingestion;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Domain.ValueObjects;
using IoT.Infrastructure.Persistence;
using IoT.Infrastructure.Persistence.Interceptors;
using IoT.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using IoT.Protocols;
using IoT.Protocols.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

    private readonly List<string> _capturedLogs = new();

    private string _pathRoot1 = string.Empty;
    private string _pathA = string.Empty;
    private Guid _devA, _devA1, _devB, _devT2;
    private Guid _custAId, _custA1Id;

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
        _custAId = custA.Id;
        _custA1Id = custA1.Id;

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

    // ================================================================================================
    // P1b (ADR-014): la API de atributos (Set/Get) y la ingesta de atributos CLIENT, ejercitadas sobre
    // la base real con el rol NOBYPASSRLS (iot_app) y enforce=true. Se montan los handlers reales con
    // repositorios reales; el ITenantContext se fija como lo haría el TenantResolutionMiddleware
    // (CustomerPath + CustomerSubtreeIds) para que tanto la RLS como el filtro EF de Device acoten.
    // ================================================================================================

    [Fact]
    public async Task P1b_OperatorScopedToA_SetsServerAttributeOnDeviceA_StoredAndGetGroupsIt()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerPath = _pathA;
        tenant.CustomerSubtreeIds = new[] { _custAId, _custA1Id };

        var deviceRepo = new DeviceRepository(ctx);
        var attrRepo = new DeviceAttributeRepository(ctx);

        var setHandler = new SetDeviceAttributesCommandHandler(deviceRepo, attrRepo, tenant, ctx);
        var setResult = await setHandler.Handle(
            new SetDeviceAttributesCommand(_devA, AttributeScope.Server, new Dictionary<string, string?>
            {
                ["installDate"] = "2026-01-01",
                ["policy"] = "P-777"
            }),
            default);

        setResult.IsSuccess.Should().BeTrue("un operador acotado a A puede fijar atributos server en un device de A");

        var getHandler = new GetDeviceAttributesQueryHandler(deviceRepo, attrRepo);
        var getResult = await getHandler.Handle(new GetDeviceAttributesQuery(T1, _devA), default);

        getResult.IsSuccess.Should().BeTrue();
        var dto = getResult.Value;
        dto.Server.Select(s => s.Key).Should().Contain(new[] { "installDate", "policy", "note" /* sembrado */ });
        dto.Server.Should().Contain(s => s.Key == "installDate" && s.Value == "2026-01-01");
        dto.Client.Should().Contain(c => c.Key == "battery", "la lectura agrupa también los atributos client sembrados");
    }

    [Fact]
    public async Task P1c_SetSharedScope_PersistsDesiredState_AndDispatchesPush()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerPath = _pathA;
        tenant.CustomerSubtreeIds = new[] { _custAId, _custA1Id };

        var deviceRepo = new DeviceRepository(ctx);
        var attrRepo = new DeviceAttributeRepository(ctx);
        var pushDispatcherMock = new Mock<ISharedAttributePushDispatcher>();
        pushDispatcherMock.Setup(p => p.PushSharedAttributesAsync(
            It.IsAny<Device>(), It.IsAny<IReadOnlyDictionary<string, string?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SharedAttributePushResult(true, "mqtt", "devices/ATTR-CHIP-A/attributes"));

        var setHandler = new SetDeviceAttributesCommandHandler(deviceRepo, attrRepo, tenant, ctx, pushDispatcherMock.Object);
        var setResult = await setHandler.Handle(
            new SetDeviceAttributesCommand(_devA, AttributeScope.Shared, new Dictionary<string, string?>
            {
                ["upload_interval"] = "45",
                ["target_temp"] = "22.5"
            }),
            default);

        setResult.IsSuccess.Should().BeTrue();

        // 1. Verificamos que se persistió en la BD PostgreSQL (RLS enforce)
        var attrs = await attrRepo.GetByDeviceAndScopeAsync(_devA, AttributeScope.Shared, default);
        attrs.Should().Contain(a => a.Key == "upload_interval" && a.Value == "45");
        attrs.Should().Contain(a => a.Key == "target_temp" && a.Value == "22.5");

        // 2. Verificamos que se disparó el push hacia el equipo (ADR-015 P1c)
        pushDispatcherMock.Verify(p => p.PushSharedAttributesAsync(
            It.Is<Device>(d => d.Id == _devA),
            It.Is<IReadOnlyDictionary<string, string?>>(d => d["upload_interval"] == "45" && d["target_temp"] == "22.5"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task P1b_SetClientScope_IsRejectedAsReadOnly_OverRealContext()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerPath = _pathA;
        tenant.CustomerSubtreeIds = new[] { _custAId, _custA1Id };

        var handler = new SetDeviceAttributesCommandHandler(
            new DeviceRepository(ctx), new DeviceAttributeRepository(ctx), tenant, ctx);

        var result = await handler.Handle(
            new SetDeviceAttributesCommand(_devA, AttributeScope.Client, new Dictionary<string, string?> { ["battery"] = "10" }),
            default);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Attributes.ClientReadOnly");

        // No se tocó el valor client sembrado (sigue en 95), con el admin que bypassa RLS.
        await using var admin = BuildContext(_adminConnectionString, withInterceptor: false, enforce: false, out _);
        var battery = await admin.DeviceAttributes.IgnoreQueryFilters()
            .FirstAsync(a => a.DeviceId == _devA && a.Scope == AttributeScope.Client && a.Key == "battery");
        battery.Value.Should().Be("95");
    }

    [Fact]
    public async Task P1b_OperatorScopedToA_CannotSetAttributeOnSiblingDeviceB_NotFound_NotPersisted()
    {
        await using (var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant))
        {
            tenant.TenantId = T1;
            tenant.CustomerPath = _pathA;                      // sesión acotada al subárbol de A
            tenant.CustomerSubtreeIds = new[] { _custAId, _custA1Id };

            var handler = new SetDeviceAttributesCommandHandler(
                new DeviceRepository(ctx), new DeviceAttributeRepository(ctx), tenant, ctx);

            // El device del hermano B no es visible desde A (filtro EF por subárbol + RLS): NotFound.
            var result = await handler.Handle(
                new SetDeviceAttributesCommand(_devB, AttributeScope.Server, new Dictionary<string, string?> { ["note"] = "intruso" }),
                default);

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("Device.NotFound",
                "un operador acotado a A no ve el device del hermano B, así que no puede fijarle atributos");
        }

        // Nada se escribió sobre el device de B (admin bypassa RLS).
        await using var admin = BuildContext(_adminConnectionString, withInterceptor: false, enforce: false, out _);
        (await admin.DeviceAttributes.IgnoreQueryFilters()
            .CountAsync(a => a.DeviceId == _devB && a.Key == "intruso"))
            .Should().Be(0, "no debe persistirse ningún atributo sobre el device del hermano B");
    }

    [Fact]
    public async Task P1b_Ingestion_RealHandler_UpsertsStableClientAttributes_AndDoesNotRewriteWhenUnchanged()
    {
        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        // El handler real fija el estado de la ingesta por sí mismo (is_system para resolver el device,
        // luego tenant del device). Se parte del tenant de la sesión; sin recorte de subárbol (path null).
        tenant.TenantId = T1;
        tenant.CustomerPath = null;
        tenant.CustomerSubtreeIds = null;

        var attrRepo = new DeviceAttributeRepository(ctx);

        // Trama con un metadato ESTABLE (firmware_version=v9) y una métrica VOLÁTIL (battery): sólo el
        // metadato estable debe materializarse como atributo CLIENT (ADR-014 D7). Se ejercita el CAMINO
        // DE PRODUCCIÓN: el IngestTelemetryCommandHandler real con el IDeviceAttributeRepository real.
        NormalizedTelemetryEvent MakeEvent() => new()
        {
            DeviceIdentifier = "ATTR-CHIP-A",
            RecordedAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
            Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["firmware_version"] = "v9" },
            NumericMetrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["battery"] = 95 },
        };

        var handler = BuildRealIngestHandler(ctx, tenant, MakeEvent, attrRepo);

        // --- Primera trama por el handler real ---
        (await handler.Handle(new IngestTelemetryCommand("telemetry", "{}"), default)).IsSuccess
            .Should().BeTrue("la ingesta real procesa la trama");

        var afterFirst = await attrRepo.GetByDeviceAndScopeAsync(_devA, AttributeScope.Client, default);
        var firmware = afterFirst.Single(a => a.Key == "firmware_version");
        firmware.Value.Should().Be("v9", "el metadato estable se upserta como atributo CLIENT");
        firmware.TenantId.Should().Be(T1, "el atributo queda sellado al tenant del device resuelto");
        firmware.DeviceId.Should().Be(_devA, "el atributo queda sellado al device resuelto");
        afterFirst.Should().NotContain(a => a.Key == "rssi", "ninguna métrica volátil se materializa como atributo");
        var firmwareStamp = firmware.LastUpdatedAt;

        // --- Segunda trama IDÉNTICA: no reescribe (change-only; LastUpdatedAt estable) ---
        (await handler.Handle(new IngestTelemetryCommand("telemetry", "{}"), default)).IsSuccess.Should().BeTrue();

        var afterSecond = await attrRepo.GetByDeviceAndScopeAsync(_devA, AttributeScope.Client, default);
        afterSecond.Single(a => a.Key == "firmware_version").LastUpdatedAt.Should().Be(firmwareStamp,
            "una segunda trama idéntica no reescribe el atributo (no se toca LastUpdatedAt)");
    }

    [Fact]
    public async Task P1b_Ingestion_RealHandler_AttributeUpsertFailure_DoesNotBreakLaterCommandConfirmation()
    {
        // Se siembra (admin, bypassa RLS) un atributo CLIENT firmware_version y un comando SENT para devA.
        var sentCommand = DeviceCommand.Create(T1, _devA, "RELAY_OFF", "{\"relay\":0}", "admin").Value;
        sentCommand.MarkAsSent();
        await using (var admin = BuildContext(_adminConnectionString, withInterceptor: false, enforce: false, out _))
        {
            admin.DeviceAttributes.Add(DeviceAttribute.Create(T1, _devA, AttributeScope.Client, "firmware_version", "v0").Value);
            admin.DeviceCommands.Add(sentCommand);
            await admin.SaveChangesAsync();
        }

        await using var ctx = BuildContext(_runtimeConnectionString, withInterceptor: true, enforce: true, out var tenant);
        tenant.TenantId = T1;
        tenant.CustomerPath = null;
        tenant.CustomerSubtreeIds = null;

        // Trama con firmware_version NUEVO (fuerza un upsert en 7b) y una respuesta de comando (paso 10).
        NormalizedTelemetryEvent MakeEvent() => new()
        {
            DeviceIdentifier = "ATTR-CHIP-A",
            RecordedAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
            Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["firmware_version"] = "v9",
                ["command_response"] = "{\"relay\":0,\"status\":\"ok\"}" // ResultJson es jsonb: debe ser JSON válido
            },
            NumericMetrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        };

        // Repositorio de atributos que FALLA el SaveChanges de 7b: encola un INSERT duplicado de la clave
        // ya sembrada (viola el índice único -> 23505), dejando la entidad Added en el change-tracker.
        var failingAttrRepo = new DuplicateInjectingAttributeRepository(ctx);
        var commandRepo = new DeviceCommandRepository(ctx);

        var handler = BuildRealIngestHandler(ctx, tenant, MakeEvent, failingAttrRepo, commandRepo);

        // Act: el upsert de atributos revienta, pero es best-effort y su fallo NO debe contaminar el
        // SaveChanges POSTERIOR del handler (confirmación de comando, paso 10).
        var result = await handler.Handle(new IngestTelemetryCommand("telemetry", "{}"), default);

        result.IsSuccess.Should().BeTrue("la telemetría ya persistió; el fallo del upsert es best-effort");

        await using var verify = BuildContext(_adminConnectionString, withInterceptor: false, enforce: false, out _);
        // (1) La telemetría del paso 7 se persistió pese al fallo de 7b.
        var device = await verify.Devices.IgnoreQueryFilters().FirstAsync(d => d.Id == _devA);
        device.LastTelemetryAt.Should().NotBeNull("el paso 7 (RecordTelemetry) commit antes de 7b y no se pierde");
        // (2) La confirmación de comando del paso 10 SÍ se persistió: el fallo de 7b no contaminó su SaveChanges.
        var cmd = await verify.DeviceCommands.IgnoreQueryFilters().FirstAsync(c => c.Id == sentCommand.Id);
        cmd.Status.Should().Be(CommandStatus.Confirmed,
            "sin aislar el fallo de 7b, el SaveChanges del paso 10 reintentaría el INSERT duplicado (23505) y la "
            + "confirmación se perdería en silencio. Logs de la ingesta: {0}", string.Join(" || ", _capturedLogs));
        // (3) El atributo no se corrompió: el upsert fallido se descartó, el valor sembrado sigue intacto.
        var attrs = await verify.DeviceAttributes.IgnoreQueryFilters()
            .Where(a => a.DeviceId == _devA && a.Scope == AttributeScope.Client && a.Key == "firmware_version")
            .ToListAsync();
        attrs.Should().ContainSingle("el INSERT duplicado se descartó, no se persistió una fila extra");
        attrs[0].Value.Should().Be("v0", "el upsert best-effort fallido no alteró el valor existente");
    }

    /// <summary>
    /// Monta el <see cref="IngestTelemetryCommandHandler"/> REAL sobre la BD de Testcontainers: device y
    /// atributos reales (RLS enforce), adaptador de protocolo simulado que devuelve el evento dado, y
    /// dependencias de borde (cache de estado, store de telemetría) simuladas como no-op.
    /// </summary>
    private IngestTelemetryCommandHandler BuildRealIngestHandler(
        ApplicationDbContext ctx,
        ITenantContext tenant,
        Func<NormalizedTelemetryEvent> eventFactory,
        IDeviceAttributeRepository attrRepo,
        IDeviceCommandRepository? commandRepo = null)
    {
        var stateCache = new Mock<IStateCacheService>();
        stateCache.Setup(c => c.IsDuplicateMessageAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        stateCache.Setup(c => c.TryUpdateLatestPositionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<GeoPosition>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new IngestTelemetryCommandHandler(
            new FakeProtocolAdapter(eventFactory),
            new DeviceRepository(ctx),
            new Mock<ITenantRepository>().Object,
            stateCache.Object,
            new Mock<ITelemetryStore>().Object,
            ctx, // IUnitOfWork == ApplicationDbContext
            new CapturingLogger<IngestTelemetryCommandHandler>(_capturedLogs),
            tenantContext: tenant,
            commandRepository: commandRepo,
            attributeRepository: attrRepo);
    }

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        private readonly List<string> _sink;
        public CapturingLogger(List<string> sink) => _sink = sink;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var chain = "";
            for (var e = exception; e is not null; e = e.InnerException)
                chain += $" :: {e.GetType().Name}: {e.Message}";
            _sink.Add($"[{logLevel}] {formatter(state, exception)}{chain}");
        }

        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    /// <summary>Adaptador de protocolo simulado: devuelve una copia fresca del evento en cada decode.</summary>
    private sealed class FakeProtocolAdapter : ICompositeProtocolAdapter
    {
        private readonly Func<NormalizedTelemetryEvent> _factory;
        public FakeProtocolAdapter(Func<NormalizedTelemetryEvent> factory) => _factory = factory;
        public Result<NormalizedTelemetryEvent> Decode(string topic, string payload)
            => Result<NormalizedTelemetryEvent>.Success(_factory());
        public Result<NormalizedTelemetryEvent> Decode(TransportMessage message)
            => Result<NormalizedTelemetryEvent>.Success(_factory());
    }

    /// <summary>
    /// Repositorio de atributos cuyas LECTURAS delegan en el repositorio real, pero cuyo UPSERT encola un
    /// INSERT que viola el índice único (duplica la fila existente) para FORZAR el fallo del SaveChanges
    /// del paso 7b, dejando una entidad Added pendiente en el change-tracker. Reproduce la contaminación
    /// que el detach de <see cref="IDeviceAttributeRepository.DiscardPendingChanges"/> debe aislar.
    /// </summary>
    private sealed class DuplicateInjectingAttributeRepository : IDeviceAttributeRepository
    {
        private readonly ApplicationDbContext _ctx;
        private readonly DeviceAttributeRepository _inner;

        public DuplicateInjectingAttributeRepository(ApplicationDbContext ctx)
        {
            _ctx = ctx;
            _inner = new DeviceAttributeRepository(ctx);
        }

        public Task<DeviceAttribute> UpsertAsync(
            Guid tenantId, Guid deviceId, AttributeScope scope, string key, string value, CancellationToken cancellationToken = default)
        {
            var created = DeviceAttribute.Create(tenantId, deviceId, scope, key, value).Value;
            _ctx.DeviceAttributes.Add(created); // duplica la fila ya sembrada -> 23505 en SaveChanges
            return Task.FromResult(created);
        }

        public Task<IReadOnlyList<DeviceAttribute>> ListByDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
            => _inner.ListByDeviceAsync(deviceId, cancellationToken);

        public Task<IReadOnlyList<DeviceAttribute>> GetByDeviceAndScopeAsync(Guid deviceId, AttributeScope scope, CancellationToken cancellationToken = default)
            => _inner.GetByDeviceAndScopeAsync(deviceId, scope, cancellationToken);

        public Task<bool> DeleteAsync(Guid deviceId, AttributeScope scope, string key, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(deviceId, scope, key, cancellationToken);

        public void DiscardPendingChanges() => _inner.DiscardPendingChanges();
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
