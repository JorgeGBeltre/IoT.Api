using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using Moq;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

/// <summary>
/// Unicidad GLOBAL del ChipId/IMEI en la creación de dispositivos (cierra el DoS
/// cross-tenant del modelo de adopción: un tenant no puede registrar el IMEI de otro).
/// </summary>
public sealed class CreateDeviceCommandTests
{
    [Fact]
    public async Task Handle_WhenChipIdExistsInAnotherTenant_FailsWithChipIdTaken()
    {
        var repo = new Mock<IDeviceRepository>();
        repo.Setup(r => r.GetByChipIdAsync(It.IsAny<Guid>(), "865840040000001", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Device?)null); // no existe en ESTE tenant
        repo.Setup(r => r.ExistsByChipIdGlobalAsync("865840040000001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // pero SÍ existe en otro tenant
        var uow = new Mock<IUnitOfWork>();

        var handler = new CreateDeviceCommandHandler(repo.Object, uow.Object);
        var cmd = new CreateDeviceCommand(Guid.NewGuid(), "865840040000001", "AA:BB:CC:DD:EE:FF", "Tracker", "Generic", "Generic");

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.ChipIdTaken");
        repo.Verify(r => r.AddAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()), Times.Never);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenChipIdGloballyFree_Succeeds()
    {
        var repo = new Mock<IDeviceRepository>();
        repo.Setup(r => r.GetByChipIdAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Device?)null);
        repo.Setup(r => r.ExistsByChipIdGlobalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var uow = new Mock<IUnitOfWork>();

        var handler = new CreateDeviceCommandHandler(repo.Object, uow.Object);
        var cmd = new CreateDeviceCommand(Guid.NewGuid(), "865840040000002", "AA:BB:CC:DD:EE:01", "Tracker", "Generic", "Generic");

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        repo.Verify(r => r.AddAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()), Times.Once);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
