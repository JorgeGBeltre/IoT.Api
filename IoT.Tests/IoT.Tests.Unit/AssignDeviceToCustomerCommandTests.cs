using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using Moq;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

/// <summary>
/// Reasignación del nodo dueño de un dispositivo (ADR-013 E8.4b): rechazo si el device o el customer
/// destino no son visibles (RLS/EF), rechazo cross-tenant, y asignación efectiva en el caso válido.
/// </summary>
public sealed class AssignDeviceToCustomerCommandTests
{
    private static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid T2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Device NewDevice(Guid tenantId, string chip)
    {
        var d = Device.Create(tenantId, chip, "AA:BB:CC:DD:EE:01", "Dev", "Model", "Mfr").Value;
        d.AssignToCustomer(Guid.NewGuid());
        return d;
    }

    [Fact]
    public async Task Handle_WhenDeviceNotVisible_FailsNotFound()
    {
        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Device?)null);
        var customerRepo = new Mock<ICustomerRepository>();
        var tenant = new Mock<ITenantContext>();
        var uow = new Mock<IUnitOfWork>();

        var handler = new AssignDeviceToCustomerCommandHandler(deviceRepo.Object, customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new AssignDeviceToCustomerCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.NotFound");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTargetCustomerNotVisible_FailsCustomerNotFound()
    {
        var device = NewDevice(T1, "CHIP-1");
        var target = Guid.NewGuid();
        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(target, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Customer?)null);  // destino fuera del subárbol (p. ej. customer B)
        var tenant = new Mock<ITenantContext>();
        var uow = new Mock<IUnitOfWork>();

        var handler = new AssignDeviceToCustomerCommandHandler(deviceRepo.Object, customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new AssignDeviceToCustomerCommand(device.Id, target), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.CustomerNotFound");
        deviceRepo.Verify(r => r.Update(It.IsAny<Device>()), Times.Never);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTargetInAnotherTenant_FailsTenantMismatch()
    {
        var device = NewDevice(T1, "CHIP-1");
        var foreign = Customer.Create(T2, "Ajeno", parent: null, code: "X").Value;
        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(foreign.Id, It.IsAny<CancellationToken>())).ReturnsAsync(foreign);
        var tenant = new Mock<ITenantContext>();
        var uow = new Mock<IUnitOfWork>();

        var handler = new AssignDeviceToCustomerCommandHandler(deviceRepo.Object, customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new AssignDeviceToCustomerCommand(device.Id, foreign.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.CustomerTenantMismatch");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenValid_AssignsAndSaves()
    {
        var device = NewDevice(T1, "CHIP-1");
        var target = Customer.Create(T1, "Destino", parent: null, code: "D").Value;
        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        var tenant = new Mock<ITenantContext>();
        var uow = new Mock<IUnitOfWork>();

        var handler = new AssignDeviceToCustomerCommandHandler(deviceRepo.Object, customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new AssignDeviceToCustomerCommand(device.Id, target.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        device.CustomerId.Should().Be(target.Id);
        deviceRepo.Verify(r => r.Update(device), Times.Once);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenTargetOutsideSessionSubtree_FailsCustomerNotFound()
    {
        // La RLS/EF no filtraron pero la sesión está acotada a OTRO subárbol (hermano): la defensa en
        // profundidad en código trata al destino como NO visible = CustomerNotFound (simétrico con el
        // device origen y con GetCustomerById/CreateCustomer).
        var device = NewDevice(T1, "CHIP-1");
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;
        var sibling = Customer.Create(T1, "B", parent: root, code: "B").Value; // destino hermano del subárbol de la sesión

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(sibling.Id, It.IsAny<CancellationToken>())).ReturnsAsync(sibling);
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.CustomerPath).Returns(root.Path + ".cffffffffffffffffffffffffffffffff"); // subárbol de A, no de B
        var uow = new Mock<IUnitOfWork>();

        var handler = new AssignDeviceToCustomerCommandHandler(deviceRepo.Object, customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new AssignDeviceToCustomerCommand(device.Id, sibling.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.CustomerNotFound", "el destino no desciende del subárbol de la sesión");
        deviceRepo.Verify(r => r.Update(It.IsAny<Device>()), Times.Never);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTargetWithinSessionSubtree_AssignsAndSaves()
    {
        // Sesión acotada a A; destino A1 (desciende de A) → doble defensa OK.
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;
        var custA = Customer.Create(T1, "A", parent: root, code: "A").Value;
        var custA1 = Customer.Create(T1, "A1", parent: custA, code: "A1").Value;
        var device = NewDevice(T1, "CHIP-1");

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(custA1.Id, It.IsAny<CancellationToken>())).ReturnsAsync(custA1);
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.CustomerPath).Returns(custA.Path); // sesión acotada a A
        var uow = new Mock<IUnitOfWork>();

        var handler = new AssignDeviceToCustomerCommandHandler(deviceRepo.Object, customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new AssignDeviceToCustomerCommand(device.Id, custA1.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("A1 desciende de A: el destino cae dentro del subárbol de la sesión");
        device.CustomerId.Should().Be(custA1.Id);
        deviceRepo.Verify(r => r.Update(device), Times.Once);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenTargetEmpty_FailsInvalidCustomer()
    {
        var deviceRepo = new Mock<IDeviceRepository>();
        var customerRepo = new Mock<ICustomerRepository>();
        var tenant = new Mock<ITenantContext>();
        var uow = new Mock<IUnitOfWork>();

        var handler = new AssignDeviceToCustomerCommandHandler(deviceRepo.Object, customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new AssignDeviceToCustomerCommand(Guid.NewGuid(), Guid.Empty), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.InvalidCustomer");
        deviceRepo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
