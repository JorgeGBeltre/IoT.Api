using IoT.Application.Common.Interfaces;
using IoT.Application.Customers.Dtos;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using MediatR;
using Microsoft.Extensions.Logging;

namespace IoT.Application.Customers.Commands;

/// <summary>
/// Reubica un customer en la jerarquía bajo un nuevo padre (reparenting) y reescribe en cascada
/// el <see cref="Customer.Path"/> y <see cref="Customer.Level"/> de todo su subárbol (ADR-013, Ítem 5).
/// Valida pertenencia al mismo tenant, visibilidad en el subárbol de la sesión, no auto-asignación
/// y prevención de ciclos en el árbol (no colgar de un descendiente propio).
/// </summary>
public sealed record ReparentCustomerCommand(
    Guid Id,
    Guid? NewParentCustomerId) : IRequest<Result<CustomerDto>>;

public sealed class ReparentCustomerCommandHandler : IRequestHandler<ReparentCustomerCommand, Result<CustomerDto>>
{
    private readonly ICustomerRepository _customerRepository;
    private readonly ITenantContext _tenantContext;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ReparentCustomerCommandHandler> _logger;

    public ReparentCustomerCommandHandler(
        ICustomerRepository customerRepository,
        ITenantContext tenantContext,
        IUnitOfWork unitOfWork,
        ILogger<ReparentCustomerCommandHandler> logger)
    {
        _customerRepository = customerRepository;
        _tenantContext = tenantContext;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<CustomerDto>> Handle(ReparentCustomerCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId;
        if (tenantId == Guid.Empty)
        {
            return Result<CustomerDto>.Failure(new Error("Customer.NoTenant", "No hay tenant resuelto en la sesión."));
        }

        // Cargar el customer objetivo respetando filtros de visibilidad (RLS + EF)
        var customer = await _customerRepository.GetByIdScopedAsync(request.Id, cancellationToken);
        if (customer is null || !_tenantContext.IsPathWithinSessionSubtree(customer.Path))
        {
            return Result<CustomerDto>.Failure(new Error(
                "Customer.NotFound",
                $"El customer '{request.Id}' no existe o está fuera del subárbol visible."));
        }

        // Regla: el nodo raíz del tenant (sin padre) es el ancla organizacional inmutable
        if (customer.ParentCustomerId is null)
        {
            return Result<CustomerDto>.Failure(new Error(
                "Customer.CannotMoveRoot",
                "No se puede reubicar el customer raíz del tenant."));
        }

        // Regla: un nodo no puede ser su propio padre
        if (request.NewParentCustomerId == customer.Id)
        {
            return Result<CustomerDto>.Failure(new Error(
                "Customer.CannotBeOwnParent",
                "Un customer no puede ser su propio padre."));
        }

        // Si se pide explícitamente el mismo padre actual, es un no-op idempotente
        if (request.NewParentCustomerId == customer.ParentCustomerId)
        {
            return Result<CustomerDto>.Success(CustomerDto.From(customer));
        }

        // Resolver nuevo padre: el indicado, o el nodo raíz del tenant si se suministra null
        var targetParentId = request.NewParentCustomerId
            ?? await _customerRepository.GetRootCustomerIdAsync(tenantId, cancellationToken);

        if (targetParentId is null)
        {
            return Result<CustomerDto>.Failure(new Error(
                "Customer.NoParent",
                "No se pudo resolver el customer padre destino (el tenant no tiene nodo raíz)."));
        }

        if (targetParentId == customer.Id)
        {
            return Result<CustomerDto>.Failure(new Error(
                "Customer.CannotBeOwnParent",
                "Un customer no puede ser su propio padre."));
        }

        if (targetParentId == customer.ParentCustomerId)
        {
            return Result<CustomerDto>.Success(CustomerDto.From(customer));
        }

        var newParent = await _customerRepository.GetByIdScopedAsync(targetParentId.Value, cancellationToken);
        if (newParent is null || !_tenantContext.IsPathWithinSessionSubtree(newParent.Path))
        {
            return Result<CustomerDto>.Failure(new Error(
                "Customer.ParentNotFound",
                "El customer padre destino no existe o está fuera del subárbol visible."));
        }

        if (newParent.TenantId != tenantId)
        {
            return Result<CustomerDto>.Failure(new Error(
                "Customer.ParentTenantMismatch",
                "El customer padre destino pertenece a otro tenant."));
        }

        // Prevención de ciclos: el nuevo padre no puede ser el mismo nodo ni un descendiente del nodo
        if (newParent.Path == customer.Path || newParent.Path.StartsWith(customer.Path + "."))
        {
            return Result<CustomerDto>.Failure(new Error(
                "Customer.CircularHierarchy",
                "No se puede reubicar un customer dentro de su propio subárbol (produciría un ciclo en la jerarquía)."));
        }

        // Guardar valores previos para el recálculo en cascada
        var oldPath = customer.Path;
        var oldLevel = customer.Level;

        // Cargar todos los descendientes en el tenant antes de mutar
        var descendants = await _customerRepository.GetDescendantsAsync(tenantId, oldPath, cancellationToken);

        // Reubicar el nodo en la jerarquía
        customer.AttachToParent(newParent);
        var newPath = customer.Path;
        var deltaLevel = customer.Level - oldLevel;

        // Actualizar en cascada todos los descendientes
        foreach (var descendant in descendants)
        {
            descendant.UpdateSubtreePath(oldPath, newPath, deltaLevel);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Customer {CustomerId} ({Name}) reubicado bajo padre {NewParentId}. Subárbol actualizado ({DescendantCount} descendientes). OldPath={OldPath}, NewPath={NewPath}",
            customer.Id,
            customer.Name,
            newParent.Id,
            descendants.Count,
            oldPath,
            newPath);

        return Result<CustomerDto>.Success(CustomerDto.From(customer));
    }
}
