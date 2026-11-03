using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Commands;

public sealed record DeleteDeviceCommand(
    Guid TenantId,
    Guid DeviceId,
    Guid? PerformedByUserId = null,
    string? PerformedByUsername = null) : IRequest<Result>;

public sealed class DeleteDeviceCommandHandler : IRequestHandler<DeleteDeviceCommand, Result>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditLogRepository? _auditLogRepository;
    private readonly IMqttSessionRevoker? _sessionRevoker;

    public DeleteDeviceCommandHandler(
        IDeviceRepository deviceRepository,
        IUnitOfWork unitOfWork,
        IAuditLogRepository? auditLogRepository = null,
        IMqttSessionRevoker? sessionRevoker = null)
    {
        _deviceRepository = deviceRepository;
        _unitOfWork = unitOfWork;
        _auditLogRepository = auditLogRepository;
        _sessionRevoker = sessionRevoker;
    }

    public async Task<Result> Handle(DeleteDeviceCommand request, CancellationToken cancellationToken)
    {
        var device = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (device is null || device.TenantId != request.TenantId)
        {
            return Result.Failure(
                new Error("Device.NotFound", "No se encontró el dispositivo solicitado o no pertenece a su cuenta."));
        }

        string chipId = device.ChipId;
        string deviceName = device.Name;
        Guid tenantId = device.TenantId;

        _deviceRepository.Delete(device);

        if (_auditLogRepository is not null)
        {
            await _auditLogRepository.AddAsync(new AuditLog(
                Guid.NewGuid(),
                tenantId,
                request.PerformedByUserId,
                string.IsNullOrWhiteSpace(request.PerformedByUsername) ? "usuario" : request.PerformedByUsername!,
                "DeviceDeleted",
                $"Dispositivo '{deviceName}' ({request.DeviceId}) desvinculado de la cuenta.",
                ipAddress: null,
                userAgent: null), cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (_sessionRevoker is not null)
        {
            await _sessionRevoker.RevokeSessionAsync(
                chipId,
                "el dispositivo fue desvinculado de la cuenta",
                cancellationToken);
        }

        return Result.Success();
    }
}

