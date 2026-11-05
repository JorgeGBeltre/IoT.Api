using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Commands;

public sealed record CreateDeviceCommand(
    Guid TenantId,
    string ChipId,
    string MacAddress,
    string Name,
    string Model,
    string Manufacturer,
    IReadOnlyList<CapabilityType>? Capabilities = null) : IRequest<Result<Guid>>;

public sealed class CreateDeviceCommandHandler : IRequestHandler<CreateDeviceCommand, Result<Guid>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly ITenantContext _tenantContext;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUnlinkedDeviceRegistry? _unlinkedDeviceRegistry;

    public CreateDeviceCommandHandler(
        IDeviceRepository deviceRepository,
        ICustomerRepository customerRepository,
        ITenantContext tenantContext,
        IUnitOfWork unitOfWork,
        IUnlinkedDeviceRegistry? unlinkedDeviceRegistry = null)
    {
        _deviceRepository = deviceRepository;
        _customerRepository = customerRepository;
        _tenantContext = tenantContext;
        _unitOfWork = unitOfWork;
        _unlinkedDeviceRegistry = unlinkedDeviceRegistry;
    }

    public async Task<Result<Guid>> Handle(CreateDeviceCommand request, CancellationToken cancellationToken)
    {
        var existing = await _deviceRepository.GetByChipIdAsync(request.TenantId, request.ChipId, cancellationToken);
        if (existing is not null)
        {
            return Result<Guid>.Failure(new Error("Device.AlreadyExists", $"Device with ChipId '{request.ChipId}' already exists in tenant."));
        }

        // Unicidad GLOBAL del ChipId/IMEI: para dispositivos sin JWT (TCP/HTTP) la única
        // identidad es el IMEI, por lo que no puede repetirse entre tenants (evita fuga y
        // DoS cross-tenant en la adopción). El índice único global en BD respalda esto.
        if (await _deviceRepository.ExistsByChipIdGlobalAsync(request.ChipId, cancellationToken))
        {
            return Result<Guid>.Failure(new Error("Device.ChipIdTaken", $"ChipId '{request.ChipId}' ya está registrado en la plataforma."));
        }

        var createResult = Device.Create(
            request.TenantId,
            request.ChipId,
            request.MacAddress,
            request.Name,
            request.Model,
            request.Manufacturer);

        if (createResult.IsFailure)
        {
            return Result<Guid>.Failure(createResult.Error);
        }

        var device = createResult.Value;

        // Nodo dueño en la jerarquía Customer (ADR-013 E8.4a): SIEMPRE se asigna un CustomerId para
        // que no queden filas con CustomerId nulo (requisito del NOT NULL y de las políticas RLS
        // fail-closed). Se usa el nodo de la sesión si está acotada a uno; si no (sesión raíz o
        // token sin customer), el Customer RAÍZ del tenant. El raíz está en el propio subárbol del
        // admin acotado al raíz, así que el alta pasa el WITH CHECK (sin regresión 42501).
        var customerId = _tenantContext.CustomerId
            ?? await _customerRepository.GetRootCustomerIdAsync(request.TenantId, cancellationToken);
        if (customerId is null || customerId.Value == Guid.Empty)
        {
            return Result<Guid>.Failure(new Error(
                "Device.NoCustomerNode",
                "El tenant no tiene un nodo Customer raíz al que asignar el dispositivo."));
        }
        device.AssignToCustomer(customerId.Value);

        if (request.Capabilities != null)
        {
            foreach (var cap in request.Capabilities)
            {
                device.AssignCapability(cap);
            }
        }

        await _deviceRepository.AddAsync(device, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        if (_unlinkedDeviceRegistry is not null)
        {
            await _unlinkedDeviceRegistry.ClearAsync(device.ChipId, cancellationToken);
        }

        return Result<Guid>.Success(device.Id);
    }
}
