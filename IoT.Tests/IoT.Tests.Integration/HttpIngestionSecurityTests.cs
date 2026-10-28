using System.Text.Json;
using FluentAssertions;
using IoT.Api.Controllers;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Http;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

public class HttpIngestionSecurityTests
{
    private readonly Mock<IMediator> _mediatorMock = new();
    private readonly Mock<IDeviceRepository> _deviceRepoMock = new();
    private readonly Mock<IDeviceApiKeyService> _apiKeyServiceMock = new();
    private readonly Mock<ILogger<HttpIngestionController>> _loggerMock = new();
    private readonly Mock<ILogger<HttpDeviceTransport>> _transportLoggerMock = new();
    private readonly HttpDeviceTransport _httpTransport;
    private readonly IConfiguration _config;

    public HttpIngestionSecurityTests()
    {
        _httpTransport = new HttpDeviceTransport(_transportLoggerMock.Object, null);
        var configValues = new Dictionary<string, string?>
        {
            { "HttpIngestion:RequireDeviceKey", "false" }
        };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
    }

    [Fact]
    public async Task PollCommands_WhenDeviceHasApiKey_AndHeaderMissing_ReturnsUnauthorized()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "HTTP-DEV01", "00:11:22:33:44:55", "Test Device", "ESP32", "Generic").Value;
        device.SetMqttApiKey("hashed_key_xyz", "fp_123", "admin");

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("HTTP-DEV01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        var controller = new HttpIngestionController(
            _mediatorMock.Object,
            _httpTransport,
            _deviceRepoMock.Object,
            _config,
            _loggerMock.Object,
            _apiKeyServiceMock.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Act
        var result = await controller.PollCommands("HTTP-DEV01", CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task PollCommands_WhenDeviceHasApiKey_AndInvalidHeader_ReturnsUnauthorized()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "HTTP-DEV02", "00:11:22:33:44:56", "Test Device 2", "ESP32", "Generic").Value;
        device.SetMqttApiKey("hashed_key_xyz", "fp_123", "admin");

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("HTTP-DEV02", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        _apiKeyServiceMock.Setup(s => s.Verify("wrong_key", "hashed_key_xyz"))
            .Returns(false);

        var controller = new HttpIngestionController(
            _mediatorMock.Object,
            _httpTransport,
            _deviceRepoMock.Object,
            _config,
            _loggerMock.Object,
            _apiKeyServiceMock.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Device-Key"] = "wrong_key";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        var result = await controller.PollCommands("HTTP-DEV02", CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task PollCommands_WhenDeviceHasApiKey_AndValidHeader_ReturnsNoContentWhenQueueEmpty()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "HTTP-DEV03", "00:11:22:33:44:57", "Test Device 3", "ESP32", "Generic").Value;
        device.SetMqttApiKey("hashed_key_xyz", "fp_123", "admin");

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("HTTP-DEV03", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        _apiKeyServiceMock.Setup(s => s.Verify("valid_secret_key", "hashed_key_xyz"))
            .Returns(true);

        var controller = new HttpIngestionController(
            _mediatorMock.Object,
            _httpTransport,
            _deviceRepoMock.Object,
            _config,
            _loggerMock.Object,
            _apiKeyServiceMock.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Device-Key"] = "valid_secret_key";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        var result = await controller.PollCommands("HTTP-DEV03", CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task IngestTelemetry_WhenDeviceHasApiKey_AndValidKeyProvided_CallsMediatorSuccessfully()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "HTTP-DEV04", "00:11:22:33:44:58", "Test Device 4", "ESP32", "Generic").Value;
        device.SetMqttApiKey("hashed_key_xyz", "fp_123", "admin");

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("HTTP-DEV04", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        _apiKeyServiceMock.Setup(s => s.Verify("valid_secret_key", "hashed_key_xyz"))
            .Returns(true);

        _mediatorMock.Setup(m => m.Send(It.IsAny<IngestTelemetryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var controller = new HttpIngestionController(
            _mediatorMock.Object,
            _httpTransport,
            _deviceRepoMock.Object,
            _config,
            _loggerMock.Object,
            _apiKeyServiceMock.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Device-Key"] = "valid_secret_key";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        string json = """
        {
          "chipId": "HTTP-DEV04",
          "temperature": 24.5
        }
        """;
        using var doc = JsonDocument.Parse(json);

        // Act
        var result = await controller.IngestTelemetry("devices/HTTP-DEV04/telemetry", doc.RootElement, CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        _mediatorMock.Verify(m => m.Send(It.Is<IngestTelemetryCommand>(c => c.Source == "http"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IngestTelemetry_WhenDeviceHasApiKey_AndInvalidKeyProvided_ReturnsUnauthorized()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "HTTP-DEV05", "00:11:22:33:44:59", "Test Device 5", "ESP32", "Generic").Value;
        device.SetMqttApiKey("hashed_key_xyz", "fp_123", "admin");

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("HTTP-DEV05", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        _apiKeyServiceMock.Setup(s => s.Verify("bad_key", "hashed_key_xyz"))
            .Returns(false);

        var controller = new HttpIngestionController(
            _mediatorMock.Object,
            _httpTransport,
            _deviceRepoMock.Object,
            _config,
            _loggerMock.Object,
            _apiKeyServiceMock.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Device-Key"] = "bad_key";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        string json = """
        {
          "chipId": "HTTP-DEV05",
          "temperature": 24.5
        }
        """;
        using var doc = JsonDocument.Parse(json);

        // Act
        var result = await controller.IngestTelemetry("devices/HTTP-DEV05/telemetry", doc.RootElement, CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedObjectResult>();
        _mediatorMock.Verify(m => m.Send(It.IsAny<IngestTelemetryCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetDeviceAttributes_WhenDeviceNotFound_ReturnsNotFound()
    {
        // Arrange
        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("UNKNOWN", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Device?)null);

        var controller = new HttpIngestionController(
            _mediatorMock.Object,
            _httpTransport,
            _deviceRepoMock.Object,
            _config,
            _loggerMock.Object,
            _apiKeyServiceMock.Object);

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();

        // Act
        var result = await controller.GetDeviceAttributes("UNKNOWN", null, scopeFactoryMock.Object, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetDeviceAttributes_WhenDeviceHasApiKey_AndHeaderMissing_ReturnsUnauthorized()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "HTTP-DEV-ATTR", "00:11:22:33:44:55", "Test Device", "ESP32", "Generic").Value;
        device.SetMqttApiKey("hashed_key_xyz", "fp_123", "admin");

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("HTTP-DEV-ATTR", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        var controller = new HttpIngestionController(
            _mediatorMock.Object,
            _httpTransport,
            _deviceRepoMock.Object,
            _config,
            _loggerMock.Object,
            _apiKeyServiceMock.Object);

        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();

        // Act
        var result = await controller.GetDeviceAttributes("HTTP-DEV-ATTR", null, scopeFactoryMock.Object, CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task GetDeviceAttributes_WhenValid_ReturnsSharedAndClientAttributes_ExcludingServer()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "HTTP-DEV-ATTR2", "00:11:22:33:44:56", "Test Device", "ESP32", "Generic").Value;

        _deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("HTTP-DEV-ATTR2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        var controller = new HttpIngestionController(
            _mediatorMock.Object,
            _httpTransport,
            _deviceRepoMock.Object,
            _config,
            _loggerMock.Object,
            _apiKeyServiceMock.Object);

        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var attrRepoMock = new Mock<IDeviceAttributeRepository>();
        var attrs = new List<DeviceAttribute>
        {
            DeviceAttribute.Create(tenantId, device.Id, AttributeScope.Shared, "interval", "60").Value,
            DeviceAttribute.Create(tenantId, device.Id, AttributeScope.Client, "firmware", "v1.2.0").Value,
            DeviceAttribute.Create(tenantId, device.Id, AttributeScope.Server, "internal_secret", "classified").Value
        };

        attrRepoMock.Setup(r => r.ListByDeviceAsync(device.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(attrs);

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock.Setup(sp => sp.GetService(typeof(IDeviceAttributeRepository)))
            .Returns(attrRepoMock.Object);

        var serviceScopeMock = new Mock<IServiceScope>();
        serviceScopeMock.Setup(s => s.ServiceProvider).Returns(serviceProviderMock.Object);

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(sf => sf.CreateScope()).Returns(serviceScopeMock.Object);

        // Act
        var result = await controller.GetDeviceAttributes("HTTP-DEV-ATTR2", null, scopeFactoryMock.Object, CancellationToken.None);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var dict = okResult.Value.Should().BeOfType<Dictionary<string, Dictionary<string, string>>>().Subject;
        dict.Should().ContainKey("shared");
        dict["shared"].Should().ContainKey("interval").WhoseValue.Should().Be("60");
        dict.Should().ContainKey("client");
        dict["client"].Should().ContainKey("firmware").WhoseValue.Should().Be("v1.2.0");
        dict.Should().NotContainKey("server");
    }
}
