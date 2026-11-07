using FluentAssertions;
using IoT.Application.Auth.Commands;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Application.Devices.Queries;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

public sealed class UnifiedPlatformIntegrationTests
{
    private static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid T2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static JwtTokenGenerator CreateJwtGenerator()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "unit-test-secret-key-at-least-32-characters-long!",
                ["Jwt:Issuer"] = "IoT-SaaS-Platform",
                ["Jwt:Audience"] = "IoT-Frontend",
                ["Jwt:ExpirationHours"] = "2",
                ["Jwt:RefreshExpirationDays"] = "14"
            })
            .Build();
        return new JwtTokenGenerator(config);
    }

    [Fact]
    public void JwtTokenGenerator_IssuesValidRefreshToken_AndRejectsAccessTokenAsRefresh()
    {
        var jwt = CreateJwtGenerator();
        var rootCustomer = Customer.Create(T1, "Root", null, "ROOT").Value;
        var user = User.Create(T1, "ana", "ana@ejemplo.com", "HASH", "Ana Gómez", "Admin").Value;
        user.AssignToCustomer(rootCustomer.Id);

        var session = jwt.GenerateToken(user, T1, rootCustomer.Id, rootCustomer.Path, new[] { "devices:read" });

        session.RefreshToken.Should().NotBeNullOrWhiteSpace();
        session.RefreshExpiresAt.Should().BeAfter(session.ExpiresAt);

        var claims = jwt.ValidateRefreshToken(session.RefreshToken!);
        claims.Should().NotBeNull();
        claims!.UserId.Should().Be(user.Id);
        claims.TenantId.Should().Be(T1);
        claims.SecurityStamp.Should().Be(user.SecurityStamp);

        // Un token de acceso NUNCA debe validarse como token de renovación (audiencia y token_use distintos).
        jwt.ValidateRefreshToken(session.Token).Should().BeNull();
    }

    [Fact]
    public async Task RefreshTokenCommand_RotatesSession_AndRevokesWhenSecurityStampChanges()
    {
        var jwt = CreateJwtGenerator();
        var tenantEntity = Tenant.CreateWithId(T1, "Hogar Ana", "HOGAR-ANA").Value;
        var rootCustomer = Customer.Create(T1, "Hogar Ana", null, "ROOT").Value;
        var user = User.Create(T1, "ana", "ana@ejemplo.com", "HASH", "Ana Gómez", "Admin").Value;
        user.AssignToCustomer(rootCustomer.Id);

        var initialSession = jwt.GenerateToken(user, T1, rootCustomer.Id, rootCustomer.Path);

        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetByIdSystemAsync(T1, user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        var tenants = new Mock<ITenantRepository>();
        tenants.Setup(r => r.GetByIdAsync(T1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenantEntity);
        var customers = new Mock<ICustomerRepository>();
        customers.Setup(r => r.GetByIdAsync(rootCustomer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootCustomer);

        var handler = new RefreshTokenCommandHandler(
            users.Object,
            jwt,
            NullLogger<RefreshTokenCommandHandler>.Instance,
            tenantRepository: tenants.Object,
            customerRepository: customers.Object);

        var refreshed = await handler.Handle(new RefreshTokenCommand(initialSession.RefreshToken!), CancellationToken.None);
        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Value.Token.Should().NotBeNullOrWhiteSpace();
        refreshed.Value.RefreshToken.Should().NotBeNullOrWhiteSpace();
        refreshed.Value.TenantId.Should().Be(T1);

        // Si el usuario cierra sesión o cambia su clave, SecurityStamp rota y el refresh token queda revocado.
        user.RotateSecurityStamp();
        var afterStampRotation = await handler.Handle(new RefreshTokenCommand(initialSession.RefreshToken!), CancellationToken.None);
        afterStampRotation.IsFailure.Should().BeTrue();
        afterStampRotation.Error.Code.Should().Be("Auth.RefreshRevoked");
    }

    [Fact]
    public async Task RegisterAccountCommand_CreatesIsolatedTenant_RootCustomer_AndAdminSession()
    {
        var jwt = CreateJwtGenerator();
        Tenant? createdTenant = null;
        Customer? createdRoot = null;
        User? createdUser = null;

        var tenants = new Mock<ITenantRepository>();
        tenants.Setup(r => r.AddAsync(It.IsAny<Tenant>(), It.IsAny<CancellationToken>()))
            .Callback<Tenant, CancellationToken>((t, _) => createdTenant = t);

        var customers = new Mock<ICustomerRepository>();
        customers.Setup(r => r.AddAsync(It.IsAny<Customer>(), It.IsAny<CancellationToken>()))
            .Callback<Customer, CancellationToken>((c, _) => createdRoot = c);

        var users = new Mock<IUserRepository>();
        users.Setup(r => r.FindByUsernameOrEmailGlobalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        users.Setup(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Callback<User, CancellationToken>((u, _) => createdUser = u);

        var hasher = new Mock<IPasswordHasher>();
        hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns("BCRYPT-SECURE-HASH");

        var audit = new Mock<IAuditLogRepository>();
        var uow = new Mock<IUnitOfWork>();

        var handler = new RegisterAccountCommandHandler(
            tenants.Object,
            customers.Object,
            users.Object,
            hasher.Object,
            jwt,
            audit.Object,
            uow.Object,
            NullLogger<RegisterAccountCommandHandler>.Instance);

        var result = await handler.Handle(
            new RegisterAccountCommand("Carlos Pérez", "carlos@ejemplo.com", "ClaveSegura#2026", "Mi Casa Inteligente"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        createdTenant.Should().NotBeNull();
        createdRoot.Should().NotBeNull();
        createdUser.Should().NotBeNull();
        createdRoot!.TenantId.Should().Be(createdTenant!.Id);
        createdUser!.TenantId.Should().Be(createdTenant.Id);
        createdUser.CustomerId.Should().Be(createdRoot.Id);
        createdUser.RoleName.Should().Be("Admin");
        result.Value.TenantId.Should().Be(createdTenant.Id);
        result.Value.RefreshToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task UpdateAndDeleteDeviceCommands_EnforceTenantIsolation_UpdateRoom_AndRevokeMqttSession()
    {
        var device = Device.Create(T1, "ESP32-AA11BB", "AA:BB:CC:DD:EE:FF", "Interruptor Sala", "VESIS-SW1", "VESIS").Value;
        device.AssignCapability(CapabilityType.Switch);

        var devices = new Mock<IDeviceRepository>();
        devices.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        var attrs = new Mock<IDeviceAttributeRepository>();
        var uow = new Mock<IUnitOfWork>();
        var revoker = new Mock<IMqttSessionRevoker>();

        var updateHandler = new UpdateDeviceCommandHandler(
            devices.Object,
            uow.Object,
            attributeRepository: attrs.Object);

        // Otro tenant NO puede renombrar el dispositivo.
        var crossTenantUpdate = await updateHandler.Handle(
            new UpdateDeviceCommand(T2, device.Id, Name: "Intruso"),
            CancellationToken.None);
        crossTenantUpdate.IsFailure.Should().BeTrue();
        crossTenantUpdate.Error.Code.Should().Be("Device.NotFound");

        // Mismo tenant renombra y asigna ubicación/habitación.
        var validUpdate = await updateHandler.Handle(
            new UpdateDeviceCommand(T1, device.Id, Name: "Luz Principal", Room: "Dormitorio"),
            CancellationToken.None);
        validUpdate.IsSuccess.Should().BeTrue();
        validUpdate.Value.Name.Should().Be("Luz Principal");
        attrs.Verify(a => a.UpsertAsync(
            T1,
            device.Id,
            AttributeScope.Shared,
            "room",
            "\"Dormitorio\"",
            It.IsAny<CancellationToken>()), Times.Once);

        // Desvinculación elimina el dispositivo y revoca su sesión MQTT activa.
        var deleteHandler = new DeleteDeviceCommandHandler(
            devices.Object,
            uow.Object,
            sessionRevoker: revoker.Object);

        var crossTenantDelete = await deleteHandler.Handle(new DeleteDeviceCommand(T2, device.Id), CancellationToken.None);
        crossTenantDelete.IsFailure.Should().BeTrue();

        var validDelete = await deleteHandler.Handle(new DeleteDeviceCommand(T1, device.Id), CancellationToken.None);
        validDelete.IsSuccess.Should().BeTrue();
        devices.Verify(r => r.Delete(device), Times.Once);
        revoker.Verify(r => r.RevokeSessionAsync("ESP32-AA11BB", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateDevice_And_GetDeviceById_ProjectDisplayAs_Room_AlertFlags_And_HardwareSettings()
    {
        var device = Device.Create(T1, "ESP32-BB22CC", "AA:BB:CC:DD:EE:02", "Clima Sala", "Relay-WiFi-1CH", "IoT Platform").Value;

        var storedAttrs = new List<DeviceAttribute>
        {
            DeviceAttribute.Create(T1, device.Id, AttributeScope.Client, "firmware_version", "v0.0.11").Value,
            DeviceAttribute.Create(T1, device.Id, AttributeScope.Client, "startup", "stay").Value,
            DeviceAttribute.Create(T1, device.Id, AttributeScope.Client, "pulse", "on").Value,
            DeviceAttribute.Create(T1, device.Id, AttributeScope.Client, "pulseWidth", "1500").Value,
            DeviceAttribute.Create(T1, device.Id, AttributeScope.Client, "sledOnline", "off").Value,
        };

        var devices = new Mock<IDeviceRepository>();
        devices.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        var customers = new Mock<ICustomerRepository>();
        var stateCache = new Mock<IStateCacheService>();
        var attrs = new Mock<IDeviceAttributeRepository>();
        attrs.Setup(a => a.UpsertAsync(
                It.IsAny<Guid>(),
                device.Id,
                It.IsAny<AttributeScope>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid tid, Guid did, AttributeScope scope, string key, string val, CancellationToken _) =>
            {
                storedAttrs.RemoveAll(x => x.Scope == scope && string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                var created = DeviceAttribute.Create(tid, did, scope, key, val).Value;
                storedAttrs.Add(created);
                return created;
            });
        attrs.Setup(a => a.ListByDeviceAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => storedAttrs.ToList());

        var uow = new Mock<IUnitOfWork>();
        var updateHandler = new UpdateDeviceCommandHandler(
            devices.Object,
            uow.Object,
            customerRepository: customers.Object,
            attributeRepository: attrs.Object,
            stateCache: stateCache.Object);

        // Rechaza valores fuera del catálogo permitido de DisplayAs.
        var invalidDisplay = await updateHandler.Handle(
            new UpdateDeviceCommand(T1, device.Id, DisplayAs: "unsupported_appliance"),
            CancellationToken.None);
        invalidDisplay.IsFailure.Should().BeTrue();
        invalidDisplay.Error.Code.Should().Be("Device.InvalidDisplayAs");

        // Persiste DisplayAs, Room y banderas de control LAN / notificaciones / alerta offline.
        var updated = await updateHandler.Handle(
            new UpdateDeviceCommand(
                T1,
                device.Id,
                Name: "Aire Principal",
                Room: "Sala Principal",
                DisplayAs: "air_conditioner",
                LanControlEnabled: false,
                PushNoticeEnabled: true,
                OfflineAlertEnabled: false),
            CancellationToken.None);

        updated.IsSuccess.Should().BeTrue();
        updated.Value.Name.Should().Be("Aire Principal");
        updated.Value.Room.Should().Be("Sala Principal");
        updated.Value.DisplayAs.Should().Be("air_conditioner");
        updated.Value.LanControlEnabled.Should().BeFalse();
        updated.Value.PushNoticeEnabled.Should().BeTrue();
        updated.Value.OfflineAlertEnabled.Should().BeFalse();
        updated.Value.FirmwareVersion.Should().Be("v0.0.11");
        updated.Value.DeviceSettings.Should().NotBeNull();
        updated.Value.DeviceSettings!["startup"].Should().Be("stay");
        updated.Value.DeviceSettings!["pulse"].Should().Be("on");
        updated.Value.DeviceSettings!["pulseWidth"].Should().Be("1500");
        updated.Value.DeviceSettings!["sledOnline"].Should().Be("off");

        // GetDeviceByIdQuery proyecta exactamente el mismo contrato hidratado.
        var queryHandler = new GetDeviceByIdQueryHandler(
            devices.Object,
            customers.Object,
            stateCache.Object,
            attributeRepository: attrs.Object);
        var fetched = await queryHandler.Handle(new GetDeviceByIdQuery(T1, device.Id), CancellationToken.None);
        fetched.IsSuccess.Should().BeTrue();
        fetched.Value.Room.Should().Be("Sala Principal");
        fetched.Value.DisplayAs.Should().Be("air_conditioner");
        fetched.Value.LanControlEnabled.Should().BeFalse();
        fetched.Value.OfflineAlertEnabled.Should().BeFalse();
        fetched.Value.FirmwareVersion.Should().Be("v0.0.11");
    }
}

