using IoT.Application.Common.Interfaces;
using IoT.Application.Customers.Dtos;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Customers.Commands;

/// <summary>
/// Crea un sub-customer (ADR-013 E8.4b). Si <paramref name="ParentCustomerId"/> es <c>null</c>, el
/// padre es el nodo de la sesión (<see cref="ITenantContext.CustomerId"/>); si éste tampoco está
/// acotado (raíz / token antiguo), se usa el Customer raíz del tenant. El padre se carga RESPETANDO
/// los filtros (RLS + EF), así que una sesión acotada no puede colgar un nodo de un subárbol ajeno.
/// </summary>
public sealed record CreateCustomerCommand(
    Guid? ParentCustomerId,
    string Name,
    string? Code = null) : IRequest<Result<CustomerDto>>;

public sealed class CreateCustomerCommandHandler : IRequestHandler<CreateCustomerCommand, Result<CustomerDto>>
{
    private readonly ICustomerRepository _customerRepository;
    private readonly ITenantContext _tenantContext;
    private readonly IUnitOfWork _unitOfWork;

    public CreateCustomerCommandHandler(
        ICustomerRepository customerRepository,
        ITenantContext tenantContext,
        IUnitOfWork unitOfWork)
    {
        _customerRepository = customerRepository;
        _tenantContext = tenantContext;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<CustomerDto>> Handle(CreateCustomerCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId;
        if (tenantId == Guid.Empty)
        {
            return Result<CustomerDto>.Failure(new Error("Customer.NoTenant", "No hay tenant resuelto en la sesión."));
        }

        // Nodo padre: el indicado, o el de la sesión; si la sesión no está acotada (raíz / token
        // antiguo), el raíz del tenant. Se carga acotado (RLS + EF): un padre de otro subárbol o
        // tenant devuelve null y el alta se rechaza.
        var parentId = request.ParentCustomerId
            ?? _tenantContext.CustomerId
            ?? await _customerRepository.GetRootCustomerIdAsync(tenantId, cancellationToken);

        if (parentId is null || parentId.Value == Guid.Empty)
        {
            return Result<CustomerDto>.Failure(new Error(
                "Customer.NoParent",
                "No se pudo resolver el customer padre (el tenant no tiene nodo raíz)."));
        }

        var parent = await _customerRepository.GetByIdScopedAsync(parentId.Value, cancellationToken);
        if (parent is null)
        {
            return Result<CustomerDto>.Failure(new Error(
                "Customer.ParentNotFound",
                "El customer padre no existe o está fuera del subárbol visible."));
        }

        // Defensa en profundidad: el padre debe ser del tenant actual (la RLS/EF ya lo garantizan).
        if (parent.TenantId != tenantId)
        {
            return Result<CustomerDto>.Failure(new Error(
                "Customer.ParentTenantMismatch",
                "El customer padre pertenece a otro tenant."));
        }

        var createResult = Customer.Create(tenantId, request.Name, parent, request.Code);
        if (createResult.IsFailure)
        {
            return Result<CustomerDto>.Failure(createResult.Error);
        }

        // Create ya ancla al padre (deriva Path/Level de él): una sola vía de anclaje.
        var customer = createResult.Value;

        // Defensa en profundidad (la línea dura es el WITH CHECK de la RLS): el nodo nuevo DEBE caer
        // en el subárbol de la sesión. Si está acotada (CustomerPath no vacío) y el Path resultante no
        // desciende de él, se rechaza en código antes de tocar la BD. Para raíz/sistema no hay recorte.
        if (!_tenantContext.IsPathWithinSessionSubtree(customer.Path))
        {
            return Result<CustomerDto>.Failure(new Error(
                "Customer.OutsideSubtree",
                "El customer resultante caería fuera del subárbol de la sesión."));
        }

        await _customerRepository.AddAsync(customer, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<CustomerDto>.Success(CustomerDto.From(customer));
    }
}
