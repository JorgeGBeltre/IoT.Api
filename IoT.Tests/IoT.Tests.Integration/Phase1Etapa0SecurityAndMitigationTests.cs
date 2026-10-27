using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using IoT.Api.Common;
using IoT.Api.Controllers;
using IoT.Api.Hubs;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Domain.ValueObjects;
using IoT.Infrastructure.Persistence;
using IoT.Infrastructure.Persistence.Entities;
using IoT.Infrastructure.Security;
using IoT.Protocols;
using IoT.Protocols.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

public class Phase1Etapa0SecurityAndMitigationTests
{
    // =========================================================================
    // E0-01 & E0-05: Secrets and JWT Validation Tests
    // =========================================================================

    [Fact]
    public void JwtTokenGenerator_MissingSecretKey_ThrowsInvalidOperationException()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var generator = new JwtTokenGenerator(config);
        var user = User.Create(Guid.NewGuid(), "admin", "admin@test.com", "hash", "Admin User", "Admin").Value;

        var act = () => generator.GenerateToken(user, Guid.NewGuid());
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt:SecretKey*");
    }

    [Fact]
    public void JwtTokenGenerator_ShortSecretKey_ThrowsInvalidOperationException()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "too-short-key-123"
            })
            .Build();

        var generator = new JwtTokenGenerator(config);
        var user = User.Create(Guid.NewGuid(), "admin", "admin@test.com", "hash", "Admin User", "Admin").Value;

        var act = () => generator.GenerateToken(user, Guid.NewGuid());
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must be at least 32 characters*");
    }

    // =========================================================================
    // E0-02: Controllers and SignalR Authorization Verification
    // =========================================================================

    [Theory]
    [InlineData(typeof(DevicesController))]
    [InlineData(typeof(FleetController))]
    [InlineData(typeof(TelemetryController))]
    [InlineData(typeof(CommandsController))]
    public void DataPlaneControllers_MustBeDecoratedWithAuthorizeAndRequireTenantDataPlane(Type controllerType)
    {
        controllerType.GetCustomAttribute<AuthorizeAttribute>()
            .Should().NotBeNull($"Controller {controllerType.Name} must be gated with [Authorize]");

        controllerType.GetCustomAttribute<RequireTenantDataPlaneAttribute>()
            .Should().NotBeNull($"Controller {controllerType.Name} must be gated with [RequireTenantDataPlane]");
    }

    [Fact]
    public void FleetHub_MustBeDecoratedWithAuthorize()
    {
        typeof(FleetHub).GetCustomAttribute<AuthorizeAttribute>()
            .Should().NotBeNull("FleetHub must be gated with [Authorize]");
    }

    [Fact]
    public async Task RequireTenantDataPlaneFilter_AnonymousUser_Returns401()
    {
        var filter = new RequireTenantDataPlaneAttribute();
        var httpContext = new DefaultHttpContext(); // Anonymous user

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, []);

        await filter.OnAuthorizationAsync(filterContext);

        filterContext.Result.Should().BeOfType<UnauthorizedObjectResult>();
        var unauthorizedResult = (UnauthorizedObjectResult)filterContext.Result!;
        unauthorizedResult.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task RequireTenantDataPlaneFilter_AuthenticatedUserWithoutTenantClaim_Returns403()
    {
        var filter = new RequireTenantDataPlaneAttribute();
        var httpContext = new DefaultHttpContext();
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "carlos")], "TestAuth");
        httpContext.User = new ClaimsPrincipal(identity);

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, []);

        await filter.OnAuthorizationAsync(filterContext);

        filterContext.Result.Should().BeOfType<ObjectResult>();
        var objectResult = (ObjectResult)filterContext.Result!;
        objectResult.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task RequireTenantDataPlaneFilter_AuthenticatedUserWithValidTenantClaim_AllowsAccess()
    {
        var filter = new RequireTenantDataPlaneAttribute();
        var httpContext = new DefaultHttpContext();
        var tenantId = Guid.NewGuid();
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "carlos"),
            new Claim("tenant_id", tenantId.ToString())
        ], "TestAuth");
        httpContext.User = new ClaimsPrincipal(identity);

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, []);

        await filter.OnAuthorizationAsync(filterContext);

        filterContext.Result.Should().BeNull(); // Authorization allowed
    }

    [Fact]
    public async Task FleetHub_JoinTenantFleet_CrossTenantAttempt_ThrowsHubException()
    {
        var loggerMock = new Mock<ILogger<FleetHub>>();
        var hub = new FleetHub(Mock.Of<IDeviceRepository>(), loggerMock.Object);

        var userTenant = Guid.NewGuid();
        var targetTenant = Guid.NewGuid(); // Different tenant!

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "hacker_operator"),
            new Claim("tenant_id", userTenant.ToString())
        ], "TestAuth");

        var hubCallerContextMock = new Mock<HubCallerContext>();
        hubCallerContextMock.Setup(c => c.User).Returns(new ClaimsPrincipal(identity));
        hubCallerContextMock.Setup(c => c.ConnectionId).Returns("conn-123");

        hub.Context = hubCallerContextMock.Object;

        var act = async () => await hub.JoinTenantFleet(targetTenant.ToString());
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*No tienes autorización*");
    }

    // =========================================================================
    // E0-03: Tenant Resolution Middleware & Fail-Closed Isolation Tests
    // =========================================================================

    [Fact]
    public async Task TenantResolutionMiddleware_TenantSpoofingAttempt_Returns403()
    {
        var loggerMock = new Mock<ILogger<TenantResolutionMiddleware>>();
        var tenantContext = new TenantContext();

        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask, loggerMock.Object);

        var userTenant = Guid.NewGuid();
        var spoofedTenant = Guid.NewGuid();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-Id"] = spoofedTenant.ToString();

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "test_user"),
            new Claim("tenant_id", userTenant.ToString())
        ], "TestAuth");
        httpContext.User = new ClaimsPrincipal(identity);

        await middleware.InvokeAsync(httpContext, tenantContext, Mock.Of<ICustomerRepository>());

        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task TenantResolutionMiddleware_UnauthenticatedUser_DoesNotAcceptHeaderTenant()
    {
        var loggerMock = new Mock<ILogger<TenantResolutionMiddleware>>();
        var tenantContext = new TenantContext();

        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask, loggerMock.Object);

        var spoofedTenant = Guid.NewGuid();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-Id"] = spoofedTenant.ToString();
        // Unauthenticated user (no User principal)

        await middleware.InvokeAsync(httpContext, tenantContext, Mock.Of<ICustomerRepository>());

        tenantContext.TenantId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void PositionEntity_And_TelemetryRecordEntity_MustImplementITenantScoped()
    {
        typeof(ITenantScoped).IsAssignableFrom(typeof(PositionEntity))
            .Should().BeTrue("PositionEntity must implement ITenantScoped for automatic tenant filtering");

        typeof(ITenantScoped).IsAssignableFrom(typeof(TelemetryRecordEntity))
            .Should().BeTrue("TelemetryRecordEntity must implement ITenantScoped for automatic tenant filtering");
    }

    [Fact]
    public void ApplicationDbContext_ModelQueryFilters_FailClosedWithoutGuidEmptyBypass()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=test_metadata_db;Username=dummy;Password=dummy", o => o.UseNetTopologySuite())
            .Options;

        var tenantContextMock = new Mock<ITenantContext>();
        tenantContextMock.Setup(c => c.TenantId).Returns(Guid.Empty);

        using var context = new ApplicationDbContext(options, tenantContextMock.Object);

        // 1. Verify Device has query filter without Guid.Empty bypass
        var deviceEntityType = context.Model.FindEntityType(typeof(Device));
        deviceEntityType.Should().NotBeNull();
        var deviceFilter = deviceEntityType!.GetQueryFilter();
        deviceFilter.Should().NotBeNull();
        deviceFilter!.ToString().Should().NotContain("00000000-0000-0000-0000-000000000000", "Guid.Empty bypass must be removed");

        // 2. Verify PositionEntity has tenant query filter active
        var positionEntityType = context.Model.FindEntityType(typeof(PositionEntity));
        positionEntityType.Should().NotBeNull();
        var positionFilter = positionEntityType!.GetQueryFilter();
        positionFilter.Should().NotBeNull("PositionEntity must have a global tenant query filter applied");
        positionFilter!.ToString().Should().NotContain("00000000-0000-0000-0000-000000000000");

        // 3. Verify TelemetryRecordEntity has tenant query filter active
        var telemetryEntityType = context.Model.FindEntityType(typeof(TelemetryRecordEntity));
        telemetryEntityType.Should().NotBeNull();
        var telemetryFilter = telemetryEntityType!.GetQueryFilter();
        telemetryFilter.Should().NotBeNull("TelemetryRecordEntity must have a global tenant query filter applied");
        telemetryFilter!.ToString().Should().NotContain("00000000-0000-0000-0000-000000000000");
    }

    // =========================================================================
    // E0-04: Simulator Isolation and Source Telemetry Marker Tests
    // =========================================================================

    [Fact]
    public async Task IngestTelemetryCommand_SimulatedSource_PropagatesSimulatedMarker()
    {
        var protocolAdapterMock = new Mock<ICompositeProtocolAdapter>();
        var deviceRepoMock = new Mock<IDeviceRepository>();
        var tenantRepoMock = new Mock<ITenantRepository>();
        var stateCacheMock = new Mock<IStateCacheService>();
        var telemetryStoreMock = new Mock<ITelemetryStore>();
        var unitOfWorkMock = new Mock<IUnitOfWork>();
        var loggerMock = new Mock<ILogger<IngestTelemetryCommandHandler>>();

        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "SIM-GPS-01", "AA:BB:CC:DD:EE:FF", "Simulated Unit", "ESP8266", "Generic").Value;

        deviceRepoMock.Setup(r => r.GetByChipIdGlobalAsync("SIM-GPS-01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        stateCacheMock.Setup(c => c.IsDuplicateMessageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        protocolAdapterMock.Setup(p => p.Decode(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Result<NormalizedTelemetryEvent>.Success(new NormalizedTelemetryEvent
            {
                DeviceIdentifier = "SIM-GPS-01",
                Position = GeoPosition.Create(18.4861, -69.9312).Value,
                RecordedAt = DateTime.UtcNow,
                ReceivedAt = DateTime.UtcNow
            }));

        var handler = new IngestTelemetryCommandHandler(
            protocolAdapterMock.Object,
            deviceRepoMock.Object,
            tenantRepoMock.Object,
            stateCacheMock.Object,
            telemetryStoreMock.Object,
            unitOfWorkMock.Object,
            loggerMock.Object);

        var simulatedCommand = new IngestTelemetryCommand("telemetry", "{}", Source: "simulated");

        var result = await handler.Handle(simulatedCommand, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        telemetryStoreMock.Verify(s => s.StorePositionAsync(
            tenantId,
            device.Id,
            It.IsAny<DateTime>(),
            It.IsAny<DateTime>(),
            It.IsAny<GeoPosition>(),
            It.IsAny<long>(),
            "simulated",
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once, "Simulator commands must explicitly record source = 'simulated'");
    }
}
