using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using IoT.Api.Common;
using IoT.Api.Controllers;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

public class McpServerTests
{
    private readonly Mock<IMcpFleetService> _fleetServiceMock = new();
    private readonly Mock<ITenantContext> _tenantContextMock = new();
    private readonly Mock<ILogger<McpController>> _loggerMock = new();
    private readonly IAuthorizationService _authorizationService;

    private readonly Guid _testTenantId = Guid.NewGuid();

    public McpServerTests()
    {
        _tenantContextMock.Setup(t => t.TenantId).Returns(_testTenantId);
        _authorizationService = BuildAuthorizationService();
    }

    /// <summary>
    /// Construye un <see cref="IAuthorizationService"/> real con la MISMA mecánica de producción
    /// (Program.cs): la política DeviceCommand admite el permiso fino devices.command o alguno de
    /// los roles de fallback. Así el test valida el gate verdadero, no una variante.
    /// </summary>
    private static IAuthorizationService BuildAuthorizationService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            var commandRoles = AuthorizationPolicies.ResolveDeviceCommandRoles(configuration);
            options.AddPolicy(
                AuthorizationPolicies.DeviceCommand,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(ctx => AuthorizationPolicies.HasPermissionOrRole(
                        ctx.User,
                        StandardPermissions.DevicesCommand,
                        commandRoles)));
        });

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal UserWithRole(string role) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "u"), new Claim(ClaimTypes.Role, role) },
            "test"));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private McpController BuildController(ClaimsPrincipal? user = null)
    {
        var controller = new McpController(
            _fleetServiceMock.Object,
            _tenantContextMock.Object,
            _authorizationService,
            _loggerMock.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user ?? Anonymous() }
        };

        return controller;
    }

    [Fact]
    public void GetServerInfo_ReturnsProtocolMetadataAndToolList()
    {
        // Arrange
        var controller = BuildController();

        // Act
        var result = controller.GetServerInfo() as OkObjectResult;

        // Assert
        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(200);

        var json = JsonSerializer.Serialize(result.Value);
        json.Should().Contain("IoT-Nexus-MCP-Server");
        json.Should().Contain("2024-11-05");
        json.Should().Contain("list_fleet_status");
        json.Should().Contain("send_device_command");
    }

    [Fact]
    public async Task HandleJsonRpc_Initialize_ReturnsMcpCapabilities()
    {
        // Arrange
        var controller = BuildController();
        var request = new JsonRpcRequest
        {
            Id = 1,
            Method = "initialize"
        };

        // Act
        var result = await controller.HandleJsonRpc(request) as OkObjectResult;

        // Assert
        result.Should().NotBeNull();
        var json = JsonSerializer.Serialize(result!.Value);
        json.Should().Contain("protocolVersion");
        json.Should().Contain("serverInfo");
        json.Should().Contain("IoT-Nexus-MCP-Server");
    }

    [Fact]
    public async Task HandleJsonRpc_ToolsList_ReturnsAllRegisteredTools()
    {
        // Arrange
        var controller = BuildController();
        var request = new JsonRpcRequest
        {
            Id = 2,
            Method = "tools/list"
        };

        // Act
        var result = await controller.HandleJsonRpc(request) as OkObjectResult;

        // Assert
        result.Should().NotBeNull();
        var json = JsonSerializer.Serialize(result!.Value);
        json.Should().Contain("list_fleet_status");
        json.Should().Contain("get_device_telemetry");
        json.Should().Contain("get_device_events");
        json.Should().Contain("send_device_command");
    }

    [Fact]
    public async Task HandleJsonRpc_ToolsCall_ListFleetStatus_CallsServiceAndReturnsContent()
    {
        // Arrange
        _fleetServiceMock
            .Setup(s => s.ListFleetStatusAsync(_testTenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new { total = 10, online = 8 });

        var controller = BuildController();

        using var doc = JsonDocument.Parse("{\"name\":\"list_fleet_status\",\"arguments\":{}}");
        var request = new JsonRpcRequest
        {
            Id = 3,
            Method = "tools/call",
            Params = doc.RootElement
        };

        // Act
        var result = await controller.HandleJsonRpc(request) as OkObjectResult;

        // Assert
        result.Should().NotBeNull();
        var json = JsonSerializer.Serialize(result!.Value);
        json.Should().Contain("content");
        json.Should().Contain("total");
        json.Should().Contain("10");
    }

    [Fact]
    public async Task HandleJsonRpc_ToolsCall_ReadToolRemainsOpenToReadOnlyProfile()
    {
        // El fix del gate de comandos es QUIRÚRGICO: las herramientas de LECTURA no se tocan.
        // Un perfil de sólo consulta (Viewer) debe seguir pudiendo listar el estado de la flota.
        _fleetServiceMock
            .Setup(s => s.ListFleetStatusAsync(_testTenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new { total = 3, online = 3 });

        var controller = BuildController(UserWithRole("Viewer"));

        using var doc = JsonDocument.Parse("{\"name\":\"list_fleet_status\",\"arguments\":{}}");
        var request = new JsonRpcRequest
        {
            Id = 31,
            Method = "tools/call",
            Params = doc.RootElement
        };

        var result = await controller.HandleJsonRpc(request) as OkObjectResult;

        result.Should().NotBeNull();
        var json = JsonSerializer.Serialize(result!.Value);
        json.Should().Contain("\"isError\":false");
        json.Should().Contain("total");
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Operator")]
    public async Task HandleJsonRpc_ToolsCall_SendDeviceCommand_DispatchesForAuthorizedRole(string role)
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var commandId = Guid.NewGuid();

        _fleetServiceMock
            .Setup(s => s.SendDeviceCommandAsync(_testTenantId, deviceId, "CUT_FUEL", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Guid>.Success(commandId));

        var controller = BuildController(UserWithRole(role));

        using var doc = JsonDocument.Parse($"{{\"name\":\"send_device_command\",\"arguments\":{{\"deviceId\":\"{deviceId}\",\"commandName\":\"CUT_FUEL\"}}}}");
        var request = new JsonRpcRequest
        {
            Id = 4,
            Method = "tools/call",
            Params = doc.RootElement
        };

        // Act
        var result = await controller.HandleJsonRpc(request) as OkObjectResult;

        // Assert
        result.Should().NotBeNull();
        var json = JsonSerializer.Serialize(result!.Value);
        json.Should().Contain("content");
        json.Should().Contain("\"isError\":false");
        json.Should().Contain(commandId.ToString());
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Auditor")]
    public async Task HandleJsonRpc_ToolsCall_SendDeviceCommand_DeniedForReadOnlyProfile_DoesNotDispatch(string role)
    {
        // Un perfil de sólo lectura NO debe poder accionar un dispositivo por MCP: el gate
        // imperativo debe denegar ANTES de tocar el servicio de flota.
        var controller = BuildController(UserWithRole(role));

        var deviceId = Guid.NewGuid();
        using var doc = JsonDocument.Parse($"{{\"name\":\"send_device_command\",\"arguments\":{{\"deviceId\":\"{deviceId}\",\"commandName\":\"CUT_FUEL\"}}}}");
        var request = new JsonRpcRequest
        {
            Id = 5,
            Method = "tools/call",
            Params = doc.RootElement
        };

        var result = await controller.HandleJsonRpc(request) as OkObjectResult;

        result.Should().NotBeNull();
        var json = JsonSerializer.Serialize(result!.Value);
        json.Should().Contain("\"isError\":true");
        json.Should().Contain("No autorizado");

        // El comando físico NUNCA se despacha para un perfil no autorizado.
        _fleetServiceMock.Verify(
            s => s.SendDeviceCommandAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleJsonRpc_ToolsCall_SendDeviceCommand_DeniedForAnonymousPrincipal_DoesNotDispatch()
    {
        // Sin identidad autenticada tampoco se acciona (defensa en profundidad bajo el [Authorize]).
        var controller = BuildController(Anonymous());

        var deviceId = Guid.NewGuid();
        using var doc = JsonDocument.Parse($"{{\"name\":\"send_device_command\",\"arguments\":{{\"deviceId\":\"{deviceId}\",\"commandName\":\"CUT_FUEL\"}}}}");
        var request = new JsonRpcRequest
        {
            Id = 6,
            Method = "tools/call",
            Params = doc.RootElement
        };

        var result = await controller.HandleJsonRpc(request) as OkObjectResult;

        result.Should().NotBeNull();
        var json = JsonSerializer.Serialize(result!.Value);
        json.Should().Contain("\"isError\":true");

        _fleetServiceMock.Verify(
            s => s.SendDeviceCommandAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleJsonRpc_UnknownMethod_ReturnsMethodNotFound()
    {
        // Arrange
        var controller = BuildController();
        var request = new JsonRpcRequest
        {
            Id = 99,
            Method = "invalid_method"
        };

        // Act
        var result = await controller.HandleJsonRpc(request) as OkObjectResult;

        // Assert
        result.Should().NotBeNull();
        var json = JsonSerializer.Serialize(result!.Value);
        json.Should().Contain("-32601");
        json.Should().Contain("not found");
    }
}
