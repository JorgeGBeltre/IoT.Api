using IoT.Application.Common.Interfaces;
using IoT.Application.Customers.Dtos;
using IoT.Domain.Common;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Customers.Queries;

/// <summary>
/// Devuelve el SUBÁRBOL visible de la sesión (ADR-013 E8.4b) como lista plana. La RLS ya acota
/// <c>customers</c> al subárbol del usuario; el frontend arma el árbol con <c>ParentCustomerId</c>.
/// </summary>
public sealed record GetCustomersQuery() : IRequest<Result<IReadOnlyList<CustomerDto>>>;

public sealed class GetCustomersQueryHandler : IRequestHandler<GetCustomersQuery, Result<IReadOnlyList<CustomerDto>>>
{
    private readonly ICustomerRepository _customerRepository;
    private readonly ITenantContext _tenantContext;

    public GetCustomersQueryHandler(ICustomerRepository customerRepository, ITenantContext tenantContext)
    {
        _customerRepository = customerRepository;
        _tenantContext = tenantContext;
    }

    public async Task<Result<IReadOnlyList<CustomerDto>>> Handle(GetCustomersQuery request, CancellationToken cancellationToken)
    {
        var customers = await _customerRepository.GetVisibleAsync(cancellationToken);

        // Defensa en profundidad simétrica con GetCustomerById (la línea dura es la RLS/EF): además
        // del recorte por subárbol de la base, se descartan en código los nodos cuyo Path no desciende
        // del subárbol de la sesión. Con CustomerPath null/vacío (raíz / sistema / token antiguo) el
        // helper devuelve true para todos → no restringe (NO DISRUPTIVO).
        IReadOnlyList<CustomerDto> list = customers
            .Where(c => _tenantContext.IsPathWithinSessionSubtree(c.Path))
            .Select(CustomerDto.From)
            .ToList();
        return Result<IReadOnlyList<CustomerDto>>.Success(list);
    }
}
