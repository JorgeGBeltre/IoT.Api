using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Customers.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

public sealed class ReparentCustomerCommandTests
{
    private static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid T2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Handle_WhenCustomerNotFound_FailsNotFound()
    {
        var id = Guid.NewGuid();
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Customer?)null);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);

        var handler = new ReparentCustomerCommandHandler(
            customerRepo.Object,
            tenant.Object,
            uow.Object,
            NullLogger<ReparentCustomerCommandHandler>.Instance);

        var result = await handler.Handle(new ReparentCustomerCommand(id, Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.NotFound");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTryingToMoveRootCustomer_FailsCannotMoveRoot()
    {
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;
        var newParent = Customer.Create(T1, "Otro", parent: null, code: "OTRO").Value;

        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(root.Id, It.IsAny<CancellationToken>())).ReturnsAsync(root);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);

        var handler = new ReparentCustomerCommandHandler(
            customerRepo.Object,
            tenant.Object,
            uow.Object,
            NullLogger<ReparentCustomerCommandHandler>.Instance);

        var result = await handler.Handle(new ReparentCustomerCommand(root.Id, newParent.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.CannotMoveRoot");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenNewParentIsSelf_FailsCannotBeOwnParent()
    {
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;
        var node = Customer.Create(T1, "Hijo", parent: root, code: "HIJO").Value;

        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(node.Id, It.IsAny<CancellationToken>())).ReturnsAsync(node);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);

        var handler = new ReparentCustomerCommandHandler(
            customerRepo.Object,
            tenant.Object,
            uow.Object,
            NullLogger<ReparentCustomerCommandHandler>.Instance);

        var result = await handler.Handle(new ReparentCustomerCommand(node.Id, node.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.CannotBeOwnParent");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenNewParentIsCurrentParent_ReturnsSuccessWithoutChanges()
    {
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;
        var node = Customer.Create(T1, "Hijo", parent: root, code: "HIJO").Value;

        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(node.Id, It.IsAny<CancellationToken>())).ReturnsAsync(node);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);

        var handler = new ReparentCustomerCommandHandler(
            customerRepo.Object,
            tenant.Object,
            uow.Object,
            NullLogger<ReparentCustomerCommandHandler>.Instance);

        var result = await handler.Handle(new ReparentCustomerCommand(node.Id, root.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ParentCustomerId.Should().Be(root.Id);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenNewParentBelongsToDifferentTenant_FailsParentTenantMismatch()
    {
        var root1 = Customer.Create(T1, "Raiz T1", parent: null, code: "ROOT1").Value;
        var node = Customer.Create(T1, "Hijo T1", parent: root1, code: "H1").Value;
        var root2 = Customer.Create(T2, "Raiz T2", parent: null, code: "ROOT2").Value;

        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(node.Id, It.IsAny<CancellationToken>())).ReturnsAsync(node);
        customerRepo.Setup(r => r.GetByIdScopedAsync(root2.Id, It.IsAny<CancellationToken>())).ReturnsAsync(root2);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);

        var handler = new ReparentCustomerCommandHandler(
            customerRepo.Object,
            tenant.Object,
            uow.Object,
            NullLogger<ReparentCustomerCommandHandler>.Instance);

        var result = await handler.Handle(new ReparentCustomerCommand(node.Id, root2.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.ParentTenantMismatch");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenNewParentIsDescendantOfCustomer_FailsCircularHierarchy()
    {
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;
        var parentNode = Customer.Create(T1, "Padre", parent: root, code: "P").Value;
        var childNode = Customer.Create(T1, "Hijo", parent: parentNode, code: "H").Value;
        var grandChildNode = Customer.Create(T1, "Nieto", parent: childNode, code: "N").Value;

        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(parentNode.Id, It.IsAny<CancellationToken>())).ReturnsAsync(parentNode);
        customerRepo.Setup(r => r.GetByIdScopedAsync(grandChildNode.Id, It.IsAny<CancellationToken>())).ReturnsAsync(grandChildNode);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);

        var handler = new ReparentCustomerCommandHandler(
            customerRepo.Object,
            tenant.Object,
            uow.Object,
            NullLogger<ReparentCustomerCommandHandler>.Instance);

        // Intentar mover 'Padre' bajo 'Nieto' (su propio descendiente)
        var result = await handler.Handle(new ReparentCustomerCommand(parentNode.Id, grandChildNode.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.CircularHierarchy");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenValidReparenting_MovesNodeAndUpdatesAllDescendantsSubtreePaths()
    {
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;
        var branchA = Customer.Create(T1, "Rama A", parent: root, code: "A").Value;
        var branchB = Customer.Create(T1, "Rama B", parent: root, code: "B").Value;

        // Subárbol que se va a mover: Sub1 -> Sub2
        var sub1 = Customer.Create(T1, "Sub 1", parent: branchA, code: "S1").Value;
        var sub2 = Customer.Create(T1, "Sub 2", parent: sub1, code: "S2").Value;

        var initialSub1Path = sub1.Path;
        var initialSub2Path = sub2.Path;
        initialSub1Path.Should().StartWith(branchA.Path);
        initialSub2Path.Should().StartWith(sub1.Path);
        sub1.Level.Should().Be(2);
        sub2.Level.Should().Be(3);

        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(sub1.Id, It.IsAny<CancellationToken>())).ReturnsAsync(sub1);
        customerRepo.Setup(r => r.GetByIdScopedAsync(branchB.Id, It.IsAny<CancellationToken>())).ReturnsAsync(branchB);
        customerRepo.Setup(r => r.GetDescendantsAsync(T1, initialSub1Path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Customer> { sub2 });

        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);

        var handler = new ReparentCustomerCommandHandler(
            customerRepo.Object,
            tenant.Object,
            uow.Object,
            NullLogger<ReparentCustomerCommandHandler>.Instance);

        // Mover sub1 de Rama A a Rama B
        var result = await handler.Handle(new ReparentCustomerCommand(sub1.Id, branchB.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        sub1.ParentCustomerId.Should().Be(branchB.Id);
        sub1.Path.Should().StartWith(branchB.Path);
        sub1.Path.Should().NotContain(branchA.Path.Split('.').Last());
        sub1.Level.Should().Be(branchB.Level + 1);

        // Verificar que sub2 (descendiente) actualizó su Path y Level
        sub2.Path.Should().StartWith(sub1.Path);
        sub2.Level.Should().Be(sub1.Level + 1);

        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Customer_UpdateSubtreePath_CorrectlyRecalculatesPathsAndLevels()
    {
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;
        var child = Customer.Create(T1, "Hijo", parent: root, code: "H").Value;
        var grandChild = Customer.Create(T1, "Nieto", parent: child, code: "N").Value;

        var oldRootPath = child.Path;
        var newRootPath = "c00000000000000000000000000000001.c00000000000000000000000000000002";
        var deltaLevel = 1;

        var initialLevel = grandChild.Level;
        grandChild.UpdateSubtreePath(oldRootPath, newRootPath, deltaLevel);

        grandChild.Path.Should().StartWith(newRootPath);
        grandChild.Level.Should().Be(initialLevel + 1);
    }
}
