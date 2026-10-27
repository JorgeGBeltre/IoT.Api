using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Customers.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using Moq;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

/// <summary>
/// Validación de la creación de sub-customers (ADR-013 E8.4b): padre por defecto = nodo del usuario,
/// resolución al raíz cuando la sesión no está acotada, y rechazo de padres fuera del subárbol/tenant.
/// </summary>
public sealed class CreateCustomerCommandTests
{
    private static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid T2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Handle_WhenParentNull_UsesSessionNode_NotRoot()
    {
        var node = Customer.Create(T1, "Nodo del usuario", parent: null, code: "NODE").Value;

        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(node.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(node);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);
        tenant.SetupGet(t => t.CustomerId).Returns(node.Id);     // sesión acotada a su nodo
        tenant.SetupGet(t => t.CustomerPath).Returns(node.Path);

        var handler = new CreateCustomerCommandHandler(customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new CreateCustomerCommand(null, "Hijo", "CH"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ParentCustomerId.Should().Be(node.Id, "el padre por defecto es el nodo de la sesión");
        result.Value.Level.Should().Be(node.Level + 1);
        customerRepo.Verify(r => r.GetRootCustomerIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
            "con nodo de sesión no debe resolver el raíz");
        customerRepo.Verify(r => r.AddAsync(It.IsAny<Customer>(), It.IsAny<CancellationToken>()), Times.Once);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenParentNull_AndSessionNotBounded_ResolvesRoot()
    {
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;

        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetRootCustomerIdAsync(T1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(root.Id);
        customerRepo.Setup(r => r.GetByIdScopedAsync(root.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(root);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);
        tenant.SetupGet(t => t.CustomerId).Returns((Guid?)null);  // raíz / token antiguo
        tenant.SetupGet(t => t.CustomerPath).Returns((string?)null);

        var handler = new CreateCustomerCommandHandler(customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new CreateCustomerCommand(null, "Hijo", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ParentCustomerId.Should().Be(root.Id);
        customerRepo.Verify(r => r.GetRootCustomerIdAsync(T1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenParentNotVisible_FailsParentNotFound()
    {
        var parentId = Guid.NewGuid();
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(parentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Customer?)null);  // RLS/EF lo ocultan (p. ej. hermano B)
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);

        var handler = new CreateCustomerCommandHandler(customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new CreateCustomerCommand(parentId, "Hijo", null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.ParentNotFound");
        customerRepo.Verify(r => r.AddAsync(It.IsAny<Customer>(), It.IsAny<CancellationToken>()), Times.Never);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenParentInAnotherTenant_FailsTenantMismatch()
    {
        var foreignParent = Customer.Create(T2, "Ajeno", parent: null, code: "X").Value;
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(foreignParent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(foreignParent);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);

        var handler = new CreateCustomerCommandHandler(customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new CreateCustomerCommand(foreignParent.Id, "Hijo", null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.ParentTenantMismatch");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenResultWouldFallOutsideSessionSubtree_FailsOutsideSubtree()
    {
        // Padre VÁLIDO (mismo tenant) pero la sesión está acotada a OTRO subárbol: el hijo caería
        // fuera de app.customer_path → defensa en profundidad lo rechaza antes de tocar la BD.
        var parent = Customer.Create(T1, "Padre fuera", parent: null, code: "P").Value;
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(parent.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);
        tenant.SetupGet(t => t.CustomerPath).Returns("cffffffffffffffffffffffffffffffff"); // subárbol distinto

        var handler = new CreateCustomerCommandHandler(customerRepo.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new CreateCustomerCommand(parent.Id, "Hijo", null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.OutsideSubtree");
        customerRepo.Verify(r => r.AddAsync(It.IsAny<Customer>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
