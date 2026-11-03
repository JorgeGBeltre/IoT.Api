using System.Text.Json;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Dtos;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Commands;

public sealed record UpdateDeviceCommand(
    Guid TenantId,
    Guid DeviceId,
    string? Name = null,
    string? Model = null,
    string? Manufacturer = null,
    string? Room = null,
    Guid? PerformedByUserId = null,
    string? PerformedByUsername = null) : IRequest<Result<DeviceDto>>;

public sealed class UpdateDeviceCommandHandler : IRequestHandler<UpdateDeviceCommand, Result<DeviceDto>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly ICustomerRepository? _customerRepository;
    private readonly IDeviceAttributeRepository? _attributeRepository;
    private readonly IStateCacheService? _stateCache;
    private readonly IAuditLogRepository? _auditLogRepository;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateDeviceCommandHandler(
        IDeviceRepository deviceRepository,
        IUnitOfWork unitOfWork,
        ICustomerRepository? customerRepository = null,
        IDeviceAttributeRepository? attributeRepository = null,
        IStateCacheService? stateCache = null,
        IAuditLogRepository? auditLogRepository = null)
    {
        _deviceRepository = deviceRepository;
        _unitOfWork = unitOfWork;
        _customerRepository = customerRepository;
        _attributeRepository = attributeRepository;
        _stateCache = stateCache;
        _auditLogRepository = auditLogRepository;
    }

    public async Task<Result<DeviceDto>> Handle(UpdateDeviceCommand request, CancellationToken cancellationToken)
    {
        var device = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (device is null || device.TenantId != request.TenantId)
        {
            return Result<DeviceDto>.Failure(
                new Error("Device.NotFound", "No se encontró el dispositivo solicitado o no pertenece a su cuenta."));
        }

        if (request.Name is not null)
        {
            var renameResult = device.Rename(request.Name);
            if (renameResult.IsFailure)
            {
                return Result<DeviceDto>.Failure(renameResult.Error);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Model) || !string.IsNullOrWhiteSpace(request.Manufacturer))
        {
            device.UpdateHardwareInfo(request.Model, request.Manufacturer);
        }

        if (request.Room is not null && _attributeRepository is not null)
        {
            var normalizedRoom = request.Room.Trim();
            if (string.IsNullOrEmpty(normalizedRoom))
            {
                await _attributeRepository.DeleteAsync(device.Id, AttributeScope.Shared, "room", cancellationToken);
            }
            else
            {
                await _attributeRepository.UpsertAsync(
                    device.TenantId,
                    device.Id,
                    AttributeScope.Shared,
                    "room",
                    JsonSerializer.Serialize(normalizedRoom),
                    cancellationToken);
            }
        }

        _deviceRepository.Update(device);

        if (_auditLogRepository is not null)
        {
            await _auditLogRepository.AddAsync(new AuditLog(
                Guid.NewGuid(),
                device.TenantId,
                request.PerformedByUserId,
                string.IsNullOrWhiteSpace(request.PerformedByUsername) ? "usuario" : request.PerformedByUsername!,
                "DeviceUpdated",
                $"Configuración actualizada para el dispositivo '{device.Name}' ({device.Id}).",
                ipAddress: null,
                userAgent: null), cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var cachedPos = _stateCache is not null
            ? await _stateCache.GetLatestPositionAsync(device.TenantId, device.Id, cancellationToken)
            : null;

        string? customerName = null;
        if (_customerRepository is not null && device.CustomerId is Guid ownerCustomerId)
        {
            var owner = await _customerRepository.GetByIdScopedAsync(ownerCustomerId, cancellationToken);
            customerName = owner?.Name;
        }

        var dto = new DeviceDto(
            device.Id,
            device.TenantId,
            device.ChipId,
            device.MacAddress,
            device.Name,
            device.Model,
            device.Manufacturer,
            device.AdminStatus.ToString(),
            device.OperationalStatus.ToString(),
            device.LastCommunicationAt ?? device.LastTelemetryAt,
            device.CreatedAt,
            device.Capabilities.Select(c => c.CapabilityType.ToString()).ToList(),
            cachedPos,
            device.CustomerId,
            customerName);

        return Result<DeviceDto>.Success(dto);
    }
}

