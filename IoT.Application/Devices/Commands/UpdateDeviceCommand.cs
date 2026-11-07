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
    string? PerformedByUsername = null,
    string? DisplayAs = null,
    bool? LanControlEnabled = null,
    bool? PushNoticeEnabled = null,
    bool? OfflineAlertEnabled = null,
    string? TempUnit = null) : IRequest<Result<DeviceDto>>;

public sealed class UpdateDeviceCommandHandler : IRequestHandler<UpdateDeviceCommand, Result<DeviceDto>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly ICustomerRepository? _customerRepository;
    private readonly IDeviceAttributeRepository? _attributeRepository;
    private readonly IStateCacheService? _stateCache;
    private readonly IAuditLogRepository? _auditLogRepository;
    private readonly ITelemetryStore? _telemetryStore;
    private readonly IMqttPeerEndpointRegistry? _peerEndpointRegistry;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateDeviceCommandHandler(
        IDeviceRepository deviceRepository,
        IUnitOfWork unitOfWork,
        ICustomerRepository? customerRepository = null,
        IDeviceAttributeRepository? attributeRepository = null,
        IStateCacheService? stateCache = null,
        IAuditLogRepository? auditLogRepository = null,
        ITelemetryStore? telemetryStore = null,
        IMqttPeerEndpointRegistry? peerEndpointRegistry = null)
    {
        _deviceRepository = deviceRepository;
        _unitOfWork = unitOfWork;
        _customerRepository = customerRepository;
        _attributeRepository = attributeRepository;
        _stateCache = stateCache;
        _auditLogRepository = auditLogRepository;
        _telemetryStore = telemetryStore;
        _peerEndpointRegistry = peerEndpointRegistry;
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

        if (request.DisplayAs is not null)
        {
            var trimmedDisplayAs = request.DisplayAs.Trim();
            if (!string.IsNullOrEmpty(trimmedDisplayAs) &&
                !DeviceDisplayAsCatalog.TryNormalize(trimmedDisplayAs, out _))
            {
                return Result<DeviceDto>.Failure(
                    new Error("Device.InvalidDisplayAs", "El tipo de visualización seleccionado no es válido."));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Model) || !string.IsNullOrWhiteSpace(request.Manufacturer))
        {
            device.UpdateHardwareInfo(request.Model, request.Manufacturer);
        }

        if (_attributeRepository is not null)
        {
            if (request.Room is not null)
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

            if (request.DisplayAs is not null)
            {
                var trimmedDisplayAs = request.DisplayAs.Trim();
                if (string.IsNullOrEmpty(trimmedDisplayAs))
                {
                    await _attributeRepository.DeleteAsync(device.Id, AttributeScope.Shared, "display_as", cancellationToken);
                }
                else if (DeviceDisplayAsCatalog.TryNormalize(trimmedDisplayAs, out string normDisplay))
                {
                    await _attributeRepository.UpsertAsync(
                        device.TenantId,
                        device.Id,
                        AttributeScope.Shared,
                        "display_as",
                        normDisplay,
                        cancellationToken);
                }
            }

            if (request.LanControlEnabled is bool lanControl)
            {
                await _attributeRepository.UpsertAsync(
                    device.TenantId,
                    device.Id,
                    AttributeScope.Shared,
                    "lan_control_enabled",
                    lanControl ? "true" : "false",
                    cancellationToken);
            }

            if (request.PushNoticeEnabled is bool pushNotice)
            {
                await _attributeRepository.UpsertAsync(
                    device.TenantId,
                    device.Id,
                    AttributeScope.Shared,
                    "push_notice_enabled",
                    pushNotice ? "true" : "false",
                    cancellationToken);
            }

            if (request.OfflineAlertEnabled is bool offlineAlert)
            {
                await _attributeRepository.UpsertAsync(
                    device.TenantId,
                    device.Id,
                    AttributeScope.Shared,
                    "offline_alert_enabled",
                    offlineAlert ? "true" : "false",
                    cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(request.TempUnit) &&
                DeviceAttributeProjection.TryNormalizeTempUnit(request.TempUnit, out string normTempUnit))
            {
                await _attributeRepository.UpsertAsync(
                    device.TenantId,
                    device.Id,
                    AttributeScope.Shared,
                    "temp_unit",
                    normTempUnit,
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

        IReadOnlyDictionary<string, double>? latestMetrics = null;
        if (_telemetryStore is not null)
        {
            var byDevice = await _telemetryStore.GetLatestMetricsByDevicesAsync(device.TenantId, new[] { device.Id }, cancellationToken);
            if (byDevice is not null && byDevice.TryGetValue(device.Id, out var foundMetrics))
            {
                latestMetrics = foundMetrics;
            }
        }

        string? customerName = null;
        if (_customerRepository is not null && device.CustomerId is Guid ownerCustomerId)
        {
            var owner = await _customerRepository.GetByIdScopedAsync(ownerCustomerId, cancellationToken);
            customerName = owner?.Name;
        }

        int? baseChannelCount = SwitchChannelCountResolver.Resolve(device, latestMetrics);
        var attrs = _attributeRepository is not null
            ? await _attributeRepository.ListByDeviceAsync(device.Id, cancellationToken)
            : null;
        var projected = DeviceAttributeProjection.Resolve(
            device,
            attrs,
            baseChannelCount,
            _peerEndpointRegistry?.GetPeerIpByChipId(device.ChipId));

        var dto = new DeviceDto(
            device.Id,
            device.TenantId,
            device.ChipId,
            device.MacAddress,
            device.Name,
            device.Model,
            DeviceAttributeProjection.SanitizeManufacturer(device.Manufacturer),
            device.AdminStatus.ToString(),
            device.OperationalStatus.ToString(),
            device.LastCommunicationAt ?? device.LastTelemetryAt,
            device.CreatedAt,
            device.Capabilities.Select(c => c.CapabilityType.ToString()).ToList(),
            cachedPos,
            device.CustomerId,
            customerName,
            latestMetrics,
            projected.FirmwareVersion,
            projected.ChannelCount,
            projected.WifiSsid,
            projected.LocalIp,
            projected.Room,
            projected.DisplayAs,
            projected.LanControlEnabled,
            projected.PushNoticeEnabled,
            projected.OfflineAlertEnabled,
            projected.DeviceSettings,
            projected.TempUnit,
            projected.FwName);

        return Result<DeviceDto>.Success(dto);
    }
}

