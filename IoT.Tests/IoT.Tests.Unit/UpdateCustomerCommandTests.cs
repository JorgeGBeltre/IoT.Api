using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Customers.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using Moq;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

/// <summary>
/// Renombrar y activar/desactivar un sub-customer (ADR-013 E8.4b). Un nodo fuera del subárbol visible
/// devuelve NotFound sin tocar la BD, ya sea porque la RLS/EF lo ocultan (GetByIdScopedAsync null) o
/// porque la defensa en profundidad en código detecta que su Path no desciende de app.customer_path.
/// El nodo raíz del tenant no puede desactivarse (guarda de raíz).
/// </summary>
public sealed class UpdateCustomerCommandTests
{
    private static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Handle_WhenNotVisible_FailsNotFound()
    {
        var id = Guid.NewGuid();
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Customer?)null);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();

        var handler = new UpdateCustomerCommandHandler(customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new UpdateCustomerCommand(id, "Nuevo", null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.NotFound");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenLoadedNodeOutsideSessionSubtree_FailsNotFound()
    {
        // La RLS/EF no filtraron (p. ej. degradada) pero la sesión está acotada a OTRO subárbol:
        // la defensa en profundidad en código trata al nodo (hermano) como NO visible = NotFound.
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;
        var sibling = Customer.Create(T1, "B", parent: root, code: "B").Value;

        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(sibling.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sibling);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.CustomerPath).Returns(root.Path + ".cffffffffffffffffffffffffffffffff"); // subárbol de A (hermano de B)

        var handler = new UpdateCustomerCommandHandler(customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new UpdateCustomerCommand(sibling.Id, "Renombrado", null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.NotFound", "el nodo cargado no desciende del subárbol de la sesión");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenDeactivatingTenantRoot_FailsCannotDeactivateRoot()
    {
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value; // sin padre = raíz
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(root.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(root);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>(); // no acotada (raíz): sin recorte por subárbol

        var handler = new UpdateCustomerCommandHandler(customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new UpdateCustomerCommand(root.Id, null, false), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.CannotDeactivateRoot");
        root.IsActive.Should().BeTrue("la guarda rechaza antes de mutar la entidad");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RenamesTenantRoot_IsAllowed()
    {
        // La guarda de raíz sólo bloquea la DESACTIVACIÓN del raíz; renombrarlo se permite.
        var root = Customer.Create(T1, "Original", parent: null, code: "ROOT").Value;
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(root.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(root);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();

        var handler = new UpdateCustomerCommandHandler(customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new UpdateCustomerCommand(root.Id, "Raiz Renombrada", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Raiz Renombrada");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RenamesAndDeactivates_NonRootNode()
    {
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;
        var customer = Customer.Create(T1, "Original", parent: root, code: "C").Value; // nodo no raíz
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(customer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(customer);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();

        var handler = new UpdateCustomerCommandHandler(customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new UpdateCustomerCommand(customer.Id, "Renombrado", false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Renombrado");
        result.Value.IsActive.Should().BeFalse();
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ReactivatesWhenIsActiveTrue()
    {
        var customer = Customer.Create(T1, "Original", parent: null, code: "C").Value;
        customer.Deactivate();
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(customer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(customer);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();

        var handler = new UpdateCustomerCommandHandler(customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new UpdateCustomerCommand(customer.Id, null, true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsActive.Should().BeTrue();
    }
}
