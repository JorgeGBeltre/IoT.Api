using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Dtos;
using IoT.Domain.Common;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Queries;

public sealed record GetDeviceByIdQuery(Guid TenantId, Guid DeviceId) : IRequest<Result<DeviceDto>>;

public sealed class GetDeviceByIdQueryHandler : IRequestHandler<GetDeviceByIdQuery, Result<DeviceDto>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly IStateCacheService _stateCache;
    private readonly ITelemetryStore? _telemetryStore;
    private readonly IDeviceAttributeRepository? _attributeRepository;
    private readonly IMqttPeerEndpointRegistry? _peerEndpointRegistry;

    public GetDeviceByIdQueryHandler(
        IDeviceRepository deviceRepository,
        ICustomerRepository customerRepository,
        IStateCacheService stateCache,
        ITelemetryStore? telemetryStore = null,
        IDeviceAttributeRepository? attributeRepository = null,
        IMqttPeerEndpointRegistry? peerEndpointRegistry = null)
    {
        _deviceRepository = deviceRepository;
        _customerRepository = customerRepository;
        _stateCache = stateCache;
        _telemetryStore = telemetryStore;
        _attributeRepository = attributeRepository;
        _peerEndpointRegistry = peerEndpointRegistry;
    }

    public async Task<Result<DeviceDto>> Handle(GetDeviceByIdQuery request, CancellationToken cancellationToken)
    {
        var d = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (d is null || d.TenantId != request.TenantId)
        {
            return Result<DeviceDto>.Failure(new Error("Device.NotFound", $"Device '{request.DeviceId}' was not found."));
        }

        var cachedPos = await _stateCache.GetLatestPositionAsync(d.TenantId, d.Id, cancellationToken);
        IReadOnlyDictionary<string, double>? latestMetrics = null;
        if (_telemetryStore is not null)
        {
            var byDevice = await _telemetryStore.GetLatestMetricsByDevicesAsync(d.TenantId, new[] { d.Id }, cancellationToken);
            if (byDevice is not null && byDevice.TryGetValue(d.Id, out var foundMetrics))
            {
                latestMetrics = foundMetrics;
            }
        }

        // Nombre del nodo dueño (Customer). Se resuelve RESPETANDO los filtros (RLS + EF, SIN
        // IgnoreQueryFilters): como el device ya resultó visible, su Customer está en el subárbol de
        // la sesión y por tanto también es visible, así que el nombre se resuelve sin fuga. Si el
        // device no tiene nodo (transición E8.1) o el nodo no es visible, CustomerName queda null.
        string? customerName = null;
        if (d.CustomerId is Guid ownerCustomerId)
        {
            var owner = await _customerRepository.GetByIdScopedAsync(ownerCustomerId, cancellationToken);
            customerName = owner?.Name;
        }

        int? baseChannelCount = SwitchChannelCountResolver.Resolve(d, latestMetrics);
        var attrs = _attributeRepository is not null
            ? await _attributeRepository.ListByDeviceAsync(d.Id, cancellationToken)
            : null;
        var projected = DeviceAttributeProjection.Resolve(
            d,
            attrs,
            baseChannelCount,
            _peerEndpointRegistry?.GetPeerIpByChipId(d.ChipId));

        var dto = new DeviceDto(
            d.Id,
            d.TenantId,
            d.ChipId,
            d.MacAddress,
            d.Name,
            d.Model,
            d.Manufacturer,
            d.AdminStatus.ToString(),
            d.OperationalStatus.ToString(),
            d.LastCommunicationAt ?? d.LastTelemetryAt,
            d.CreatedAt,
            d.Capabilities.Select(c => c.CapabilityType.ToString()).ToList(),
            cachedPos,
            d.CustomerId,
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
            projected.DeviceSettings);

        return Result<DeviceDto>.Success(dto);
    }
}
