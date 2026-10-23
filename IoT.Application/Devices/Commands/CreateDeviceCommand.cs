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
    private readonly IUnitOfWork _unitOfWork;

    public CreateDeviceCommandHandler(IDeviceRepository deviceRepository, IUnitOfWork unitOfWork)
    {
        _deviceRepository = deviceRepository;
        _unitOfWork = unitOfWork;
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

        if (request.Capabilities != null)
        {
            foreach (var cap in request.Capabilities)
            {
                device.AssignCapability(cap);
            }
        }

        await _deviceRepository.AddAsync(device, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<Guid>.Success(device.Id);
    }
}
