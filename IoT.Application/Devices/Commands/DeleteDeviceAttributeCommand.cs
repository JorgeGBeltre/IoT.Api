using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Commands;

/// <summary>
/// Borra un atributo fijado por el operador (<see cref="AttributeScope.Shared"/> o
/// <see cref="AttributeScope.Server"/>) de un dispositivo (P1b, opcional). Los atributos
/// <see cref="AttributeScope.Client"/> los reporta el equipo, así que NO se borran por API
/// (<c>Attributes.ClientReadOnly</c>). El dispositivo se carga SCOPED (RLS + EF): uno fuera del
/// subárbol/tenant de la sesión se trata como <c>Device.NotFound</c>; la RLS acota además el borrado.
/// </summary>
public sealed record DeleteDeviceAttributeCommand(
    Guid DeviceId,
    AttributeScope Scope,
    string Key) : IRequest<Result>;

public sealed class DeleteDeviceAttributeCommandHandler : IRequestHandler<DeleteDeviceAttributeCommand, Result>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDeviceAttributeRepository _attributeRepository;
    private readonly ITenantContext _tenantContext;
    private readonly IUnitOfWork _unitOfWork;

    public DeleteDeviceAttributeCommandHandler(
        IDeviceRepository deviceRepository,
        IDeviceAttributeRepository attributeRepository,
        ITenantContext tenantContext,
        IUnitOfWork unitOfWork)
    {
        _deviceRepository = deviceRepository;
        _attributeRepository = attributeRepository;
        _tenantContext = tenantContext;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> Handle(DeleteDeviceAttributeCommand request, CancellationToken cancellationToken)
    {
        if (request.Scope == AttributeScope.Client)
        {
            return Result.Failure(new Error(
                "Attributes.ClientReadOnly",
                "Los atributos de ámbito 'Client' los reporta el dispositivo y no pueden borrarse desde la API."));
        }

        if (string.IsNullOrWhiteSpace(request.Key))
        {
            return Result.Failure(new Error("Attributes.InvalidKey", "La clave del atributo es obligatoria."));
        }

        var device = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (device is null || device.TenantId != _tenantContext.TenantId)
        {
            return Result.Failure(new Error(
                "Device.NotFound", $"El dispositivo '{request.DeviceId}' no existe o está fuera del subárbol visible."));
        }

        var deleted = await _attributeRepository.DeleteAsync(request.DeviceId, request.Scope, request.Key, cancellationToken);
        if (!deleted)
        {
            return Result.Failure(new Error(
                "Attributes.NotFound", $"No existe un atributo '{request.Key}' de ámbito '{request.Scope}' en el dispositivo."));
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
