using IoT.Application.Commands.Commands;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using MediatR;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Services;

public sealed class McpFleetService : IMcpFleetService
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly ITelemetryStore _telemetryStore;
    private readonly IDeviceAttributeRepository _attributeRepository;
    private readonly IEventQueryService _eventQueryService;
    private readonly IMediator _mediator;
    private readonly ILogger<McpFleetService> _logger;

    public McpFleetService(
        IDeviceRepository deviceRepository,
        ITelemetryStore telemetryStore,
        IDeviceAttributeRepository attributeRepository,
        IEventQueryService eventQueryService,
        IMediator mediator,
        ILogger<McpFleetService> logger)
    {
        _deviceRepository = deviceRepository;
        _telemetryStore = telemetryStore;
        _attributeRepository = attributeRepository;
        _eventQueryService = eventQueryService;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task<object> ListFleetStatusAsync(Guid tenantId, string? statusFilter = null, CancellationToken cancellationToken = default)
    {
        DeviceOperationalStatus? opFilter = null;
        if (!string.IsNullOrWhiteSpace(statusFilter) && Enum.TryParse<DeviceOperationalStatus>(statusFilter, true, out var parsedStatus))
        {
            opFilter = parsedStatus;
        }

        var devices = await _deviceRepository.GetByTenantAsync(tenantId, opFilter, cancellationToken);

        int totalCount = devices.Count;
        int activeCount = devices.Count(d => d.AdminStatus == DeviceAdminStatus.Active);
        int onlineCount = devices.Count(d => d.OperationalStatus == DeviceOperationalStatus.Online);
        int offlineCount = devices.Count(d => d.OperationalStatus == DeviceOperationalStatus.Offline);

        var deviceSummaries = devices.Take(100).Select(d => new
        {
            id = d.Id,
            chipId = d.ChipId,
            name = d.Name,
            model = d.Model,
            operationalStatus = d.OperationalStatus.ToString(),
            adminStatus = d.AdminStatus.ToString(),
            lastCommunicationAt = d.LastCommunicationAt,
            lastTelemetryAt = d.LastTelemetryAt
        }).ToList();

        return new
        {
            summary = new
            {
                totalDevices = totalCount,
                activeDevices = activeCount,
                onlineDevices = onlineCount,
                offlineDevices = offlineCount,
                reportedAtUtc = DateTime.UtcNow
            },
            devices = deviceSummaries
        };
    }

    public async Task<object?> GetDeviceTelemetryAsync(Guid tenantId, string deviceIdOrChipId, CancellationToken cancellationToken = default)
    {
        Device? device = null;
        if (Guid.TryParse(deviceIdOrChipId, out var deviceGuid))
        {
            device = await _deviceRepository.GetByIdAsync(deviceGuid, cancellationToken);
            if (device != null && device.TenantId != tenantId)
            {
                device = null;
            }
        }

        if (device == null)
        {
            device = await _deviceRepository.GetByChipIdAsync(tenantId, deviceIdOrChipId, cancellationToken);
        }

        if (device == null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var positions = await _telemetryStore.GetPositionsAsync(tenantId, device.Id, now.AddDays(-7), now, limit: 1, cancellationToken: cancellationToken);
        var telemetry = await _telemetryStore.GetTelemetryAsync(tenantId, device.Id, now.AddDays(-7), now, limit: 1, cancellationToken: cancellationToken);
        var attributes = await _attributeRepository.ListByDeviceAsync(device.Id, cancellationToken);

        var latestPos = positions.FirstOrDefault();
        var latestTel = telemetry.FirstOrDefault();

        return new
        {
            device = new
            {
                id = device.Id,
                chipId = device.ChipId,
                name = device.Name,
                model = device.Model,
                manufacturer = device.Manufacturer,
                firmwareVersion = device.FirmwareVersion,
                operationalStatus = device.OperationalStatus.ToString(),
                adminStatus = device.AdminStatus.ToString(),
                lastCommunicationAt = device.LastCommunicationAt,
                lastTelemetryAt = device.LastTelemetryAt
            },
            latestPosition = latestPos == null ? null : new
            {
                latitude = latestPos.Latitude,
                longitude = latestPos.Longitude,
                speedKph = latestPos.SpeedKph,
                heading = latestPos.Heading,
                altitudeM = latestPos.AltitudeM,
                recordedAt = latestPos.RecordedAt
            },
            latestTelemetry = latestTel == null ? null : new
            {
                recordedAt = latestTel.RecordedAt,
                metrics = latestTel.Metrics
            },
            attributes = attributes
                .Where(a => !(a.Scope == Domain.Enums.AttributeScope.Server &&
                              (a.Key.Equals("device_key", StringComparison.OrdinalIgnoreCase) ||
                               a.Key.Equals("bind_token", StringComparison.OrdinalIgnoreCase) ||
                               a.Key.Equals("ota_hmac_key", StringComparison.OrdinalIgnoreCase))))
                .Select(a => new
                {
                    scope = a.Scope.ToString(),
                    key = a.Key,
                    value = a.Value,
                    lastUpdatedAt = a.LastUpdatedAt
                }).ToList()
        };
    }

    public async Task<object> GetDeviceEventsAsync(Guid tenantId, Guid? deviceId = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        int safeLimit = Math.Clamp(limit, 1, 100);
        var result = await _eventQueryService.GetEventsAsync(
            tenantId,
            deviceId: deviceId,
            limit: safeLimit,
            cancellationToken: cancellationToken);

        return result;
    }

    public async Task<Result<Guid>> SendDeviceCommandAsync(Guid tenantId, Guid deviceId, string commandName, string? payloadJson = null, CancellationToken cancellationToken = default)
    {
        var device = await _deviceRepository.GetByIdAsync(deviceId, cancellationToken);
        if (device == null || device.TenantId != tenantId)
        {
            return Result<Guid>.Failure(new Error("DeviceNotFound", "El dispositivo no existe o no pertenece a tu organización."));
        }

        string clientRequestId = Guid.NewGuid().ToString("N");
        var command = new SendCommandToDeviceCommand(
            TenantId: tenantId,
            DeviceId: deviceId,
            CommandName: commandName,
            PayloadJson: payloadJson,
            RequestedBy: "MCP-AI-Assistant",
            ClientRequestId: clientRequestId);

        return await _mediator.Send(command, cancellationToken);
    }
}
