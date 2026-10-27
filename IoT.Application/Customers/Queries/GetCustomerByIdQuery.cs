using IoT.Application.Common.Interfaces;
using IoT.Application.Customers.Dtos;
using IoT.Domain.Common;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Customers.Queries;

/// <summary>
/// Devuelve un nodo del subárbol visible por su Id (ADR-013 E8.4b). Un nodo fuera del subárbol de la
/// sesión (o de otro tenant) no es visible por la RLS/EF → NotFound.
/// </summary>
public sealed record GetCustomerByIdQuery(Guid Id) : IRequest<Result<CustomerDto>>;

public sealed class GetCustomerByIdQueryHandler : IRequestHandler<GetCustomerByIdQuery, Result<CustomerDto>>
{
    private readonly ICustomerRepository _customerRepository;
    private readonly ITenantContext _tenantContext;

    public GetCustomerByIdQueryHandler(ICustomerRepository customerRepository, ITenantContext tenantContext)
    {
        _customerRepository = customerRepository;
        _tenantContext = tenantContext;
    }

    public async Task<Result<CustomerDto>> Handle(GetCustomerByIdQuery request, CancellationToken cancellationToken)
    {
        var customer = await _customerRepository.GetByIdScopedAsync(request.Id, cancellationToken);

        // Defensa en profundidad simétrica con CreateCustomer (la línea dura es la RLS/EF): si la
        // sesión está acotada y el Path del nodo no desciende de app.customer_path, se trata como NO
        // visible = mismo NotFound. Para raíz/sistema/token antiguo no hay recorte (NO DISRUPTIVO).
        if (customer is null || !_tenantContext.IsPathWithinSessionSubtree(customer.Path))
        {
            return Result<CustomerDto>.Failure(new Error(
                "Customer.NotFound",
                $"El customer '{request.Id}' no existe o está fuera del subárbol visible."));
        }

        return Result<CustomerDto>.Success(CustomerDto.From(customer));
    }
}
