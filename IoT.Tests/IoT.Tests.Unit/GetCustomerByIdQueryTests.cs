using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Customers.Queries;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using Moq;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

/// <summary>
/// Lectura de un nodo por Id (ADR-013 E8.4b). Simétrico con CreateCustomer: además de la RLS/EF, la
/// defensa en profundidad en código descarta un nodo cargado cuyo Path no desciende del subárbol de
/// la sesión (NotFound). Sin sesión acotada (raíz / token antiguo) no hay recorte (NO DISRUPTIVO).
/// </summary>
public sealed class GetCustomerByIdQueryTests
{
    private static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Handle_WhenNotVisible_FailsNotFound()
    {
        var id = Guid.NewGuid();
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Customer?)null);
        var tenant = new Mock<ITenantContext>();

        var handler = new GetCustomerByIdQueryHandler(customerRepo.Object, tenant.Object);
        var result = await handler.Handle(new GetCustomerByIdQuery(id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.NotFound");
    }

    [Fact]
    public async Task Handle_WhenLoadedNodeOutsideSessionSubtree_FailsNotFound()
    {
        // La RLS/EF no filtraron pero la sesión está acotada a OTRO subárbol (hermano): la defensa en
        // profundidad en código trata al nodo como NO visible = NotFound.
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;
        var sibling = Customer.Create(T1, "B", parent: root, code: "B").Value;

        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(sibling.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sibling);
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.CustomerPath).Returns(root.Path + ".cffffffffffffffffffffffffffffffff"); // subárbol de A

        var handler = new GetCustomerByIdQueryHandler(customerRepo.Object, tenant.Object);
        var result = await handler.Handle(new GetCustomerByIdQuery(sibling.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.NotFound", "el nodo cargado no desciende del subárbol de la sesión");
    }

    [Fact]
    public async Task Handle_WhenNodeWithinSessionSubtree_ReturnsIt()
    {
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;
        var node = Customer.Create(T1, "A", parent: root, code: "A").Value;

        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(node.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(node);
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.CustomerPath).Returns(node.Path); // sesión acotada al propio nodo

        var handler = new GetCustomerByIdQueryHandler(customerRepo.Object, tenant.Object);
        var result = await handler.Handle(new GetCustomerByIdQuery(node.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be("A");
    }

    [Fact]
    public async Task Handle_WhenSessionNotBounded_DoesNotRestrict()
    {
        // Raíz / token antiguo: CustomerPath null → no se recorta por subárbol (NO DISRUPTIVO).
        var node = Customer.Create(T1, "Cualquiera", parent: null, code: "ANY").Value;
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetByIdScopedAsync(node.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(node);
        var tenant = new Mock<ITenantContext>(); // CustomerPath null

        var handler = new GetCustomerByIdQueryHandler(customerRepo.Object, tenant.Object);
        var result = await handler.Handle(new GetCustomerByIdQuery(node.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
