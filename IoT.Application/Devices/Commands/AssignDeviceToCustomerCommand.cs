using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Commands;

/// <summary>
/// Reasigna el nodo dueño (Customer) de un dispositivo dentro de la jerarquía (ADR-013 E8.4b). El
/// dispositivo se carga RESPETANDO los filtros (RLS + EF por subárbol): una sesión acotada no puede
/// tocar un dispositivo fuera de su subárbol. El customer destino se valida igual (RLS + EF): debe
/// existir y estar en el subárbol de la sesión. El WITH CHECK de la RLS refuerza en la BD que el
/// destino cae en el subárbol al guardar.
/// </summary>
public sealed record AssignDeviceToCustomerCommand(
    Guid DeviceId,
    Guid TargetCustomerId) : IRequest<Result>;

public sealed class AssignDeviceToCustomerCommandHandler : IRequestHandler<AssignDeviceToCustomerCommand, Result>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly ITenantContext _tenantContext;
    private readonly IUnitOfWork _unitOfWork;

    public AssignDeviceToCustomerCommandHandler(
        IDeviceRepository deviceRepository,
        ICustomerRepository customerRepository,
        ITenantContext tenantContext,
        IUnitOfWork unitOfWork)
    {
        _deviceRepository = deviceRepository;
        _customerRepository = customerRepository;
        _tenantContext = tenantContext;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> Handle(AssignDeviceToCustomerCommand request, CancellationToken cancellationToken)
    {
        if (request.TargetCustomerId == Guid.Empty)
        {
            return Result.Failure(new Error("Device.InvalidCustomer", "El customer destino es obligatorio."));
        }

        // RLS + filtro EF por subárbol: un dispositivo fuera del subárbol de la sesión no es visible.
        var device = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (device is null)
        {
            return Result.Failure(new Error("Device.NotFound", $"El dispositivo '{request.DeviceId}' no existe o está fuera del subárbol visible."));
        }

        // El customer destino debe existir y estar en el subárbol visible (RLS + EF lo acotan).
        var target = await _customerRepository.GetByIdScopedAsync(request.TargetCustomerId, cancellationToken);
        if (target is null)
        {
            return Result.Failure(new Error("Device.CustomerNotFound", $"El customer destino '{request.TargetCustomerId}' no existe o está fuera del subárbol visible."));
        }

        // Defensa en profundidad simétrica con GetCustomerById/CreateCustomer (la línea dura es la
        // RLS + su WITH CHECK): si la sesión está acotada y el Path del destino no desciende de
        // app.customer_path, el nodo se trata como NO visible = mismo CustomerNotFound. Así el destino
        // queda con la misma doble defensa que el device origen. Raíz/sistema/token antiguo: sin
        // recorte (NO DISRUPTIVO).
        if (!_tenantContext.IsPathWithinSessionSubtree(target.Path))
        {
            return Result.Failure(new Error("Device.CustomerNotFound", $"El customer destino '{request.TargetCustomerId}' no existe o está fuera del subárbol visible."));
        }

        // Defensa en profundidad: mismo tenant (RLS/EF ya lo garantizan).
        if (target.TenantId != device.TenantId)
        {
            return Result.Failure(new Error("Device.CustomerTenantMismatch", "El customer destino pertenece a otro tenant."));
        }

        device.AssignToCustomer(target.Id);
        _deviceRepository.Update(device);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
