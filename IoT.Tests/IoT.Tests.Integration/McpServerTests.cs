using System.Text.Json;
using FluentAssertions;
using IoT.Api.Controllers;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

public class McpServerTests
{
    private readonly Mock<IMcpFleetService> _fleetServiceMock = new();
    private readonly Mock<ITenantContext> _tenantContextMock = new();
    private readonly Mock<ILogger<McpController>> _loggerMock = new();

    private readonly Guid _testTenantId = Guid.NewGuid();

    public McpServerTests()
    {
        _tenantContextMock.Setup(t => t.TenantId).Returns(_testTenantId);
    }

    [Fact]
    public void GetServerInfo_ReturnsProtocolMetadataAndToolList()
    {
        // Arrange
        var controller = new McpController(_fleetServiceMock.Object, _tenantContextMock.Object, _loggerMock.Object);

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
        var controller = new McpController(_fleetServiceMock.Object, _tenantContextMock.Object, _loggerMock.Object);
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
        var controller = new McpController(_fleetServiceMock.Object, _tenantContextMock.Object, _loggerMock.Object);
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

        var controller = new McpController(_fleetServiceMock.Object, _tenantContextMock.Object, _loggerMock.Object);

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
    public async Task HandleJsonRpc_ToolsCall_SendDeviceCommand_DispatchesCommand()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var commandId = Guid.NewGuid();

        _fleetServiceMock
            .Setup(s => s.SendDeviceCommandAsync(_testTenantId, deviceId, "CUT_FUEL", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Guid>.Success(commandId));

        var controller = new McpController(_fleetServiceMock.Object, _tenantContextMock.Object, _loggerMock.Object);

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

    [Fact]
    public async Task HandleJsonRpc_UnknownMethod_ReturnsMethodNotFound()
    {
        // Arrange
        var controller = new McpController(_fleetServiceMock.Object, _tenantContextMock.Object, _loggerMock.Object);
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
