using System.Security.Claims;
using FluentAssertions;
using IoT.Api.Hubs;
using IoT.Domain.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Regresión de B-01 (analisis.md §11.9 / §22): un usuario autenticado de un tenant no debe
/// poder unirse al grupo SignalR <c>device_{id}</c> de un dispositivo de otro tenant.
/// La consulta real del repositorio (IgnoreQueryFilters + tenant explícito) se cubre en la
/// suite de PostgreSQL real; aquí se verifica el contrato del hub.
/// </summary>
public class FleetHubTenantIsolationTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private static (FleetHub Hub, Mock<IGroupManager> Groups) CreateHub(Mock<IDeviceRepository> repo, Guid? userTenant)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "operador") };
        if (userTenant.HasValue)
        {
            claims.Add(new Claim("tenant_id", userTenant.Value.ToString()));
        }

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")));
        context.Setup(c => c.ConnectionId).Returns("conn-1");
        context.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);

        var groups = new Mock<IGroupManager>();
        var hub = new FleetHub(repo.Object, NullLogger<FleetHub>.Instance)
        {
            Context = context.Object,
            Groups = groups.Object
        };
        return (hub, groups);
    }

    [Fact]
    public async Task JoinDeviceLive_DeviceOfAnotherTenant_IsRejected_AndNotAddedToGroup()
    {
        var deviceOfTenantB = Guid.NewGuid();
        var repo = new Mock<IDeviceRepository>();
        repo.Setup(r => r.BelongsToTenantAsync(deviceOfTenantB, TenantB, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        repo.Setup(r => r.BelongsToTenantAsync(deviceOfTenantB, TenantA, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var (hub, groups) = CreateHub(repo, TenantA);

        var act = () => hub.JoinDeviceLive(deviceOfTenantB.ToString());

        await act.Should().ThrowAsync<HubException>().WithMessage("*No tienes autorización*");
        groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JoinDeviceLive_OwnDevice_JoinsNormalizedGroup()
    {
        var ownDevice = Guid.NewGuid();
        var repo = new Mock<IDeviceRepository>();
        repo.Setup(r => r.BelongsToTenantAsync(ownDevice, TenantA, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var (hub, groups) = CreateHub(repo, TenantA);

        // Mayúsculas a propósito: el grupo debe coincidir con el formato que usa el emisor.
        await hub.JoinDeviceLive(ownDevice.ToString().ToUpperInvariant());

        groups.Verify(g => g.AddToGroupAsync("conn-1", $"device_{ownDevice}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("no-es-un-guid")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task JoinDeviceLive_InvalidOrUnknownDeviceId_IsRejected(string deviceId)
    {
        var repo = new Mock<IDeviceRepository>();
        repo.Setup(r => r.BelongsToTenantAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var (hub, groups) = CreateHub(repo, TenantA);

        var act = () => hub.JoinDeviceLive(deviceId);

        await act.Should().ThrowAsync<HubException>();
        groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JoinDeviceLive_WithoutTenantClaim_IsRejected_WithoutQueryingRepository()
    {
        var repo = new Mock<IDeviceRepository>(MockBehavior.Strict);
        var (hub, groups) = CreateHub(repo, userTenant: null);

        var act = () => hub.JoinDeviceLive(Guid.NewGuid().ToString());

        await act.Should().ThrowAsync<HubException>();
        groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JoinTenantFleet_OwnTenantInUppercase_JoinsNormalizedGroup()
    {
        var (hub, groups) = CreateHub(new Mock<IDeviceRepository>(), TenantA);

        await hub.JoinTenantFleet(TenantA.ToString().ToUpperInvariant());

        groups.Verify(g => g.AddToGroupAsync("conn-1", $"tenant_{TenantA}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinTenantFleet_OtherTenant_IsRejected()
    {
        var (hub, groups) = CreateHub(new Mock<IDeviceRepository>(), TenantA);

        var act = () => hub.JoinTenantFleet(TenantB.ToString());

        await act.Should().ThrowAsync<HubException>();
        groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
