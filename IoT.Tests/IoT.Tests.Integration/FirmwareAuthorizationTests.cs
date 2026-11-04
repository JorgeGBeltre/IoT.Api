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
/// Autorización de las operaciones que escriben firmware en hardware real.
///
/// Antes de esto la API no tenía NINGUNA política: todas las anotaciones [Authorize] eran
/// desnudas y la de respaldo sólo exigía estar autenticado, así que cualquier usuario del
/// tenant —incluido el rol por defecto de User.Create, que es "Operator"— podía subir un
/// binario arbitrario y flashear la flota completa.
/// </summary>
public class FirmwareAuthorizationTests
{
    private static IAuthorizationService BuildAuthorizationService(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            var firmwareRoles = AuthorizationPolicies.ResolveFirmwareManageRoles(configuration);
            options.AddPolicy(
                AuthorizationPolicies.FirmwareManage,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(ctx => AuthorizationPolicies.HasPermissionOrRole(
                        ctx.User,
                        IoT.Domain.Common.StandardPermissions.FirmwareManage,
                        firmwareRoles)));

            var commandRoles = AuthorizationPolicies.ResolveDeviceCommandRoles(configuration);
            options.AddPolicy(
                AuthorizationPolicies.DeviceCommand,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(ctx => AuthorizationPolicies.HasPermissionOrRole(
                        ctx.User,
                        IoT.Domain.Common.StandardPermissions.DevicesCommand,
                        commandRoles)));
        });

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static ClaimsPrincipal UserWithRole(string? role)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "u"), new("tenant_id", Guid.NewGuid().ToString()) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        // authenticationType no nulo => principal autenticado
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Theory]
    [InlineData("Operator")]   // el rol POR DEFECTO de User.Create
    [InlineData("Viewer")]
    [InlineData("Supervisor")]
    [InlineData("admin ")]     // con espacio al final: no debe colar
    [InlineData("")]
    public async Task ANonPrivilegedUserCannotManageFirmware(string role)
    {
        var authz = BuildAuthorizationService(EmptyConfig());

        var result = await authz.AuthorizeAsync(UserWithRole(role), null, AuthorizationPolicies.FirmwareManage);

        result.Succeeded.Should().BeFalse("el rol indicado no debe poder flashear hardware");
    }

    [Fact]
    public async Task AUserWithNoRoleClaimAtAllIsDenied()
    {
        var authz = BuildAuthorizationService(EmptyConfig());

        var result = await authz.AuthorizeAsync(UserWithRole(null), null, AuthorizationPolicies.FirmwareManage);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task AnAnonymousPrincipalIsDenied()
    {
        var authz = BuildAuthorizationService(EmptyConfig());
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity()); // sin authenticationType

        var result = await authz.AuthorizeAsync(anonymous, null, AuthorizationPolicies.FirmwareManage);

        result.Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("admin")]   // la comparación de roles no distingue caja
    public async Task TheSeededAdminRoleCanManageFirmware(string role)
    {
        var authz = BuildAuthorizationService(EmptyConfig());

        var result = await authz.AuthorizeAsync(UserWithRole(role), null, AuthorizationPolicies.FirmwareManage);

        result.Succeeded.Should().BeTrue("es el rol con el que el seeder crea los administradores");
    }

    [Fact]
    public async Task TheRoleSetCanBeWidenedByConfigurationWithoutTouchingCode()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authorization:FirmwareManageRoles:0"] = "Admin",
                ["Authorization:FirmwareManageRoles:1"] = "FieldEngineer"
            })
            .Build();

        var authz = BuildAuthorizationService(configuration);

        (await authz.AuthorizeAsync(UserWithRole("FieldEngineer"), null, AuthorizationPolicies.FirmwareManage))
            .Succeeded.Should().BeTrue();
        (await authz.AuthorizeAsync(UserWithRole("Operator"), null, AuthorizationPolicies.FirmwareManage))
            .Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData("Admin,FieldEngineer", "FieldEngineer", true)]
    [InlineData("Admin,FieldEngineer", "Operator", false)]
    public async Task TheRoleSetAlsoAcceptsACommaSeparatedValue(string configured, string role, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authorization:FirmwareManageRoles"] = configured
            })
            .Build();

        var authz = BuildAuthorizationService(configuration);

        (await authz.AuthorizeAsync(UserWithRole(role), null, AuthorizationPolicies.FirmwareManage))
            .Succeeded.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,")]
    public void AnEmptyOrBrokenConfigurationFallsBackInsteadOfLeavingThePolicyOpen(string configured)
    {
        // Una lista vacía dejaría la política SIN requisito de rol, es decir, permisiva.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authorization:FirmwareManageRoles"] = configured
            })
            .Build();

        var roles = AuthorizationPolicies.ResolveFirmwareManageRoles(configuration);

        roles.Should().NotBeEmpty();
        roles.Should().Contain("Admin");
    }

    [Fact]
    public void EveryMutatingFirmwareEndpointRequiresThePolicy()
    {
        // Guardia de regresión: si alguien añade un endpoint que escribe y se olvida de la
        // política, este test lo caza. Cubre también la ruta legacy de rollback por GET, que
        // muta y vive en el mismo método.
        var mutating = typeof(FirmwareController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpPostAttribute>().Any() ||
                        m.GetCustomAttributes<HttpPutAttribute>().Any() ||
                        m.GetCustomAttributes<HttpPatchAttribute>().Any() ||
                        m.GetCustomAttributes<HttpDeleteAttribute>().Any())
            .ToList();

        mutating.Should().NotBeEmpty("si esto queda vacío, el test no está comprobando nada");

        foreach (var method in mutating)
        {
            var policies = method.GetCustomAttributes<AuthorizeAttribute>()
                .Select(a => a.Policy)
                .ToList();

            policies.Should().Contain(AuthorizationPolicies.FirmwareManage,
                "el método " + method.Name + " escribe firmware en hardware y debe exigir la política");
        }
    }

    [Fact]
    public void TheReadOnlyEndpointsStayAvailableToAnyUserOfTheTenant()
    {
        // Consultar el inventario de versiones o el estado de una campaña no es destructivo:
        // restringirlo también dejaría al operador sin ver qué está pasando.
        foreach (var name in new[] { "GetFirmwareVersionRecords", "GetDeviceOtaStatus", "GetAvailableDeviceFirmware" })
        {
            var method = typeof(FirmwareController).GetMethod(name);
            method.Should().NotBeNull("se esperaba encontrar el endpoint de lectura " + name);
            method!.GetCustomAttributes<AuthorizeAttribute>()
                .Select(a => a.Policy)
                .Should().NotContain(AuthorizationPolicies.FirmwareManage);
        }
    }

    [Fact]
    public async Task AUserWithFineGrainedPermissionClaim_CanManageFirmware_EvenWithNonAdminRole()
    {
        var authz = BuildAuthorizationService(EmptyConfig());

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "technician_user"),
            new("tenant_id", Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "CustomTechnician"),
            new("permission", IoT.Domain.Common.StandardPermissions.FirmwareManage)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        var result = await authz.AuthorizeAsync(principal, null, AuthorizationPolicies.FirmwareManage);

        result.Succeeded.Should().BeTrue("el claim explícito 'permission: firmware.manage' otorga acceso");
    }

    [Fact]
    public async Task AUserWithFineGrainedPermissionClaim_CanExecuteDeviceCommand()
    {
        var authz = BuildAuthorizationService(EmptyConfig());

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "dispatcher_user"),
            new("tenant_id", Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "CustomDispatcher"),
            new("permission", IoT.Domain.Common.StandardPermissions.DevicesCommand)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        var result = await authz.AuthorizeAsync(principal, null, AuthorizationPolicies.DeviceCommand);

        result.Succeeded.Should().BeTrue("el claim explícito 'permission: devices.command' otorga acceso");
    }
}
