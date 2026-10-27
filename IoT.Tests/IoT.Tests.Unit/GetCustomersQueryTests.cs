using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Customers.Queries;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using Moq;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

/// <summary>
/// Listado del subárbol visible (ADR-013 E8.4b/E8.4c). Defensa en profundidad en código simétrica con
/// GetCustomerById: además del recorte por subárbol de la RLS/EF, el handler descarta los nodos cuyo
/// Path no desciende del subárbol de la sesión. Sin sesión acotada (raíz / token antiguo) no recorta.
/// </summary>
public sealed class GetCustomersQueryTests
{
    private static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static (Customer root, Customer a, Customer a1, Customer b) BuildTopology()
    {
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;
        var a = Customer.Create(T1, "A", parent: root, code: "A").Value;
        var a1 = Customer.Create(T1, "A1", parent: a, code: "A1").Value;
        var b = Customer.Create(T1, "B", parent: root, code: "B").Value; // hermano de A
        return (root, a, a1, b);
    }

    [Fact]
    public async Task Handle_WhenSessionBounded_ExcludesSiblingsOutsideSubtree()
    {
        var (_, a, a1, b) = BuildTopology();
        var customerRepo = new Mock<ICustomerRepository>();
        // La RLS debería recortar, pero aquí devolvemos también el hermano B para probar el filtro de código.
        customerRepo.Setup(r => r.GetVisibleAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { a, a1, b });
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.CustomerPath).Returns(a.Path); // sesión acotada a A

        var handler = new GetCustomersQueryHandler(customerRepo.Object, tenant.Object);
        var result = await handler.Handle(new GetCustomersQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(c => c.Code).Should().BeEquivalentTo(
            new[] { "A", "A1" },
            "el subárbol visible de A no incluye al hermano B aunque la RLS lo dejara pasar");
    }

    [Fact]
    public async Task Handle_WhenSessionNotBounded_DoesNotRestrict()
    {
        var (root, a, a1, b) = BuildTopology();
        var customerRepo = new Mock<ICustomerRepository>();
        customerRepo.Setup(r => r.GetVisibleAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { root, a, a1, b });
        var tenant = new Mock<ITenantContext>(); // CustomerPath null → raíz / token antiguo

        var handler = new GetCustomersQueryHandler(customerRepo.Object, tenant.Object);
        var result = await handler.Handle(new GetCustomersQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(c => c.Code).Should().BeEquivalentTo(
            new[] { "ROOT", "A", "A1", "B" },
            "sin sesión acotada no hay recorte por subárbol (NO DISRUPTIVO)");
    }
}
