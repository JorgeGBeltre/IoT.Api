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
/// Autorizacion del envio de comandos a dispositivos. Apagar un rele por API es
/// accionamiento fisico y no deberia requerir solo una sesion valida, que era lo unico que
/// exigia el controlador.
///
/// La decision de producto esta aqui a proposito: el perfil que OPERA la flota puede mandar
/// comandos (es la rutina) pero NO puede flashear firmware (es excepcional e irreversible).
/// </summary>
public class DeviceCommandAuthorizationTests
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
                    .RequireAssertion(ctx => AuthorizationPolicies.HasAnyRole(ctx.User, firmwareRoles)));

            var commandRoles = AuthorizationPolicies.ResolveDeviceCommandRoles(configuration);
            options.AddPolicy(
                AuthorizationPolicies.DeviceCommand,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(ctx => AuthorizationPolicies.HasAnyRole(ctx.User, commandRoles)));
        });

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static ClaimsPrincipal UserWithRole(string? role)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "u"), new("tenant_id", Guid.NewGuid().ToString()) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Operator")]
    [InlineData("operator")]
    public async Task TheRolesThatRunTheFleetCanSendCommands(string role)
    {
        var authz = BuildAuthorizationService(EmptyConfig());

        var result = await authz.AuthorizeAsync(UserWithRole(role), null, AuthorizationPolicies.DeviceCommand);

        result.Succeeded.Should().BeTrue("mandar un comando es la operacion del dia a dia");
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Auditor")]
    [InlineData("")]
    public async Task AReadOnlyProfileCannotActuateADevice(string role)
    {
        var authz = BuildAuthorizationService(EmptyConfig());

        var result = await authz.AuthorizeAsync(UserWithRole(role), null, AuthorizationPolicies.DeviceCommand);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task AnOperatorCanCommandButCannotFlash()
    {
        // ESTE es el contraste que justifica tener dos politicas en vez de una.
        var authz = BuildAuthorizationService(EmptyConfig());
        var operador = UserWithRole("Operator");

        (await authz.AuthorizeAsync(operador, null, AuthorizationPolicies.DeviceCommand))
            .Succeeded.Should().BeTrue();
        (await authz.AuthorizeAsync(operador, null, AuthorizationPolicies.FirmwareManage))
            .Succeeded.Should().BeFalse("flashear es excepcional e irreversible: solo Admin");
    }

    [Fact]
    public async Task AnAnonymousPrincipalIsDenied()
    {
        var authz = BuildAuthorizationService(EmptyConfig());
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        (await authz.AuthorizeAsync(anonymous, null, AuthorizationPolicies.DeviceCommand))
            .Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyConfigurationFallsBackInsteadOfLeavingThePolicyOpen(string configured)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authorization:DeviceCommandRoles"] = configured
            })
            .Build();

        var roles = AuthorizationPolicies.ResolveDeviceCommandRoles(configuration);

        roles.Should().NotBeEmpty();
        roles.Should().Contain("Admin");
    }

    [Fact]
    public void EveryMutatingCommandEndpointRequiresThePolicy()
    {
        var mutating = typeof(CommandsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpPostAttribute>().Any() ||
                        m.GetCustomAttributes<HttpPutAttribute>().Any() ||
                        m.GetCustomAttributes<HttpPatchAttribute>().Any() ||
                        m.GetCustomAttributes<HttpDeleteAttribute>().Any())
            .ToList();

        mutating.Should().NotBeEmpty("si esto queda vacio, el test no comprueba nada");

        foreach (var method in mutating)
        {
            method.GetCustomAttributes<AuthorizeAttribute>()
                .Select(a => a.Policy)
                .Should().Contain(AuthorizationPolicies.DeviceCommand,
                    "el metodo " + method.Name + " acciona un dispositivo o cambia el estado de un comando");
        }
    }
}
