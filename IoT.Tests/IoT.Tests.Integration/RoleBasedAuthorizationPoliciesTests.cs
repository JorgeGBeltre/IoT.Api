using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using IoT.Api.Common;
using IoT.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Pruebas de regresión para B-05: control de acceso basado en roles para operaciones
/// sensibles de gestión de dispositivos y administración del tenant.
/// </summary>
public class RoleBasedAuthorizationPoliciesTests
{
    private static IAuthorizationService BuildAuthorizationService(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            var deviceRoles = AuthorizationPolicies.ResolveDeviceManageRoles(configuration);
            options.AddPolicy(
                AuthorizationPolicies.DeviceManage,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(ctx => AuthorizationPolicies.HasAnyRole(ctx.User, deviceRoles)));

            var tenantAdminRoles = AuthorizationPolicies.ResolveTenantAdminRoles(configuration);
            options.AddPolicy(
                AuthorizationPolicies.TenantAdmin,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(ctx => AuthorizationPolicies.HasAnyRole(ctx.User, tenantAdminRoles)));

            // BrokerManage se registra con el MISMO helper que producción (Program.cs), de modo que el
            // test ejercita el registro REAL (gate sólo-rol) y no una reimplementación que pudiera diverger.
            AuthorizationPolicies.AddBrokerManagePolicy(options, configuration);
        });

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static ClaimsPrincipal UserWithRole(string? role)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "test_user"), new("tenant_id", Guid.NewGuid().ToString()) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    // =========================================================================
    // DeviceManage Policy Tests
    // =========================================================================

    [Theory]
    [InlineData("Admin")]
    [InlineData("Operator")]
    [InlineData("admin")] // Case-insensitive
    [InlineData("operator")]
    public async Task DeviceManage_AllowsAdminAndOperator(string role)
    {
        var authz = BuildAuthorizationService(EmptyConfig());
        var result = await authz.AuthorizeAsync(UserWithRole(role), null, AuthorizationPolicies.DeviceManage);
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("viewer")]
    [InlineData("Guest")]
    public async Task DeviceManage_DeniesViewerAndOthers(string role)
    {
        var authz = BuildAuthorizationService(EmptyConfig());
        var result = await authz.AuthorizeAsync(UserWithRole(role), null, AuthorizationPolicies.DeviceManage);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task DeviceManage_DeniesAnonymous()
    {
        var authz = BuildAuthorizationService(EmptyConfig());
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await authz.AuthorizeAsync(anonymous, null, AuthorizationPolicies.DeviceManage);
        result.Succeeded.Should().BeFalse();
    }

    // =========================================================================
    // TenantAdmin Policy Tests
    // =========================================================================

    [Theory]
    [InlineData("Admin")]
    [InlineData("admin")]
    public async Task TenantAdmin_AllowsAdmin(string role)
    {
        var authz = BuildAuthorizationService(EmptyConfig());
        var result = await authz.AuthorizeAsync(UserWithRole(role), null, AuthorizationPolicies.TenantAdmin);
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("Viewer")]
    [InlineData("Auditor")]
    public async Task TenantAdmin_DeniesOperatorAndViewer(string role)
    {
        var authz = BuildAuthorizationService(EmptyConfig());
        var result = await authz.AuthorizeAsync(UserWithRole(role), null, AuthorizationPolicies.TenantAdmin);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task TenantAdmin_DeniesAnonymous()
    {
        var authz = BuildAuthorizationService(EmptyConfig());
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await authz.AuthorizeAsync(anonymous, null, AuthorizationPolicies.TenantAdmin);
        result.Succeeded.Should().BeFalse();
    }

    // =========================================================================
    // BrokerManage Policy Tests (gating de infraestructura de brokers)
    // =========================================================================

    [Theory]
    [InlineData("Admin")]
    [InlineData("admin")]
    [InlineData("Superadmin")]
    [InlineData("Platform Super Admin")] // Alineado con el gate del frontend (/brokers superadmin)
    public async Task BrokerManage_AllowsAdminAndPlatformSuperByDefault(string role)
    {
        var authz = BuildAuthorizationService(EmptyConfig());
        var result = await authz.AuthorizeAsync(UserWithRole(role), null, AuthorizationPolicies.BrokerManage);
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("Viewer")]
    [InlineData("Auditor")]
    public async Task BrokerManage_DeniesLowerPrivilegeRoles(string role)
    {
        // Regresión del hueco corregido: un perfil de menor privilegio ya no puede leer host:port.
        var authz = BuildAuthorizationService(EmptyConfig());
        var result = await authz.AuthorizeAsync(UserWithRole(role), null, AuthorizationPolicies.BrokerManage);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task BrokerManage_DeniesAnonymous()
    {
        var authz = BuildAuthorizationService(EmptyConfig());
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await authz.AuthorizeAsync(anonymous, null, AuthorizationPolicies.BrokerManage);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task BrokerManage_CanBeRestrictedToPlatformSuperAdminByConfiguration()
    {
        // La perilla documentada: limitar brokers a un superadmin de plataforma sin tocar código
        // ni afectar a TenantAdmin. Con la config puesta, "Admin" deja de bastar y sólo pasa el super.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authorization:BrokerManageRoles"] = "Platform Super Admin"
            })
            .Build();
        var authz = BuildAuthorizationService(config);

        (await authz.AuthorizeAsync(UserWithRole("Admin"), null, AuthorizationPolicies.BrokerManage))
            .Succeeded.Should().BeFalse();
        (await authz.AuthorizeAsync(UserWithRole("Platform Super Admin"), null, AuthorizationPolicies.BrokerManage))
            .Succeeded.Should().BeTrue();
    }

    // =========================================================================
    // Endpoint Decoration Tests (Reflective Verification)
    // =========================================================================

    [Fact]
    public void DevicesController_SensitiveEndpoints_RequireDeviceManagePolicy()
    {
        var methods = typeof(DevicesController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        var registerMethod = methods.FirstOrDefault(m => m.Name == nameof(DevicesController.RegisterDevice));
        registerMethod.Should().NotBeNull();
        registerMethod!.GetCustomAttributes<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .Should().Contain(AuthorizationPolicies.DeviceManage);

        var getCredsMethod = methods.FirstOrDefault(m => m.Name == nameof(DevicesController.GetDeviceCredentials));
        getCredsMethod.Should().NotBeNull();
        getCredsMethod!.GetCustomAttributes<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .Should().Contain(AuthorizationPolicies.DeviceManage);

        var rotateCredsMethod = methods.FirstOrDefault(m => m.Name == nameof(DevicesController.RotateDeviceCredentials));
        rotateCredsMethod.Should().NotBeNull();
        rotateCredsMethod!.GetCustomAttributes<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .Should().Contain(AuthorizationPolicies.DeviceManage);
    }

    [Fact]
    public void TenantSettingsController_UpdateEndpoint_RequiresTenantAdminPolicy()
    {
        var updateMethod = typeof(TenantSettingsController).GetMethod(nameof(TenantSettingsController.Update));
        updateMethod.Should().NotBeNull();
        updateMethod!.GetCustomAttributes<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .Should().Contain(AuthorizationPolicies.TenantAdmin);
    }

    [Fact]
    public void BrokersController_RequiresBrokerManagePolicyAtClassLevel()
    {
        // La política está a NIVEL DE CLASE a propósito: así cubre también la LECTURA (GetAll/GetById),
        // que es donde estaba la fuga de host:port. No debe degradarse a per-método.
        typeof(BrokersController).GetCustomAttribute<AuthorizeAttribute>()!
            .Policy.Should().Be(AuthorizationPolicies.BrokerManage);
    }

    [Fact]
    public void BrokersController_ReadEndpoints_AreNotAnonymous()
    {
        // Regresión directa del defecto: GET lista y GET por id ya no son accesibles sin la política.
        var methods = typeof(BrokersController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var name in new[]
                 {
                     nameof(BrokersController.GetAll), nameof(BrokersController.GetById),
                     nameof(BrokersController.Create), nameof(BrokersController.Update), nameof(BrokersController.Delete)
                 })
        {
            var method = methods.FirstOrDefault(m => m.Name == name);
            method.Should().NotBeNull($"El controlador debe exponer {name}");
            method!.GetCustomAttributes<AllowAnonymousAttribute>()
                .Should().BeEmpty($"El método {name} no debe permitir acceso anónimo");
        }
    }

    [Fact]
    public void GatewaysController_RequiresBrokerManagePolicyAtClassLevel()
    {
        // GET /api/v1/gateways publica el host público + los puertos de los listeners (infraestructura
        // de conexión). Comparte el gate de brokers: antes era [Authorize] desnudo y cualquier usuario
        // autenticado del tenant leía host:port.
        typeof(GatewaysController).GetCustomAttribute<AuthorizeAttribute>()!
            .Policy.Should().Be(AuthorizationPolicies.BrokerManage);
    }

    [Fact]
    public void UsersController_RequiresTenantAdminPolicy()
    {
        typeof(UsersController).GetCustomAttribute<AuthorizeAttribute>()!
            .Policy.Should().Be(AuthorizationPolicies.TenantAdmin);
    }

    [Fact]
    public void AuditController_RequiresTenantAdminPolicy()
    {
        typeof(AuditController).GetCustomAttribute<AuthorizeAttribute>()!
            .Policy.Should().Be(AuthorizationPolicies.TenantAdmin);
    }
}
