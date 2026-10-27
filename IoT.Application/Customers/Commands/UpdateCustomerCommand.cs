using IoT.Application.Common.Interfaces;
using IoT.Application.Customers.Dtos;
using IoT.Domain.Common;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Customers.Commands;

/// <summary>
/// Renombra y/o activa-desactiva un sub-customer (ADR-013 E8.4b). El nodo se carga RESPETANDO los
/// filtros (RLS + EF): una sesión acotada no puede modificar un nodo fuera de su subárbol (devuelve
/// NotFound). NO reubica el nodo en la jerarquía (reparenting) — eso es trabajo diferido.
/// </summary>
public sealed record UpdateCustomerCommand(
    Guid Id,
    string? Name = null,
    bool? IsActive = null) : IRequest<Result<CustomerDto>>;

public sealed class UpdateCustomerCommandHandler : IRequestHandler<UpdateCustomerCommand, Result<CustomerDto>>
{
    private readonly ICustomerRepository _customerRepository;
    private readonly ITenantContext _tenantContext;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateCustomerCommandHandler(
        ICustomerRepository customerRepository,
        ITenantContext tenantContext,
        IUnitOfWork unitOfWork)
    {
        _customerRepository = customerRepository;
        _tenantContext = tenantContext;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<CustomerDto>> Handle(UpdateCustomerCommand request, CancellationToken cancellationToken)
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

        // Guarda de raíz: el nodo raíz del tenant (sin padre) no puede desactivarse (dejaría el tenant
        // sin ancla de jerarquía). Renombrarlo sí se permite; sólo se bloquea la desactivación.
        if (request.IsActive == false && customer.ParentCustomerId is null)
        {
            return Result<CustomerDto>.Failure(new Error(
                "Customer.CannotDeactivateRoot",
                "No se puede desactivar el customer raíz del tenant."));
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            customer.Rename(request.Name);
        }

        if (request.IsActive.HasValue)
        {
            if (request.IsActive.Value) customer.Activate();
            else customer.Deactivate();
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<CustomerDto>.Success(CustomerDto.From(customer));
    }
}
