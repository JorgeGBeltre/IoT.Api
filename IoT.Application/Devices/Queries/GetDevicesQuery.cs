using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Dtos;
using IoT.Domain.Common;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Queries;

public sealed record GetDevicesQuery(Guid TenantId, DeviceOperationalStatus? Status = null) : IRequest<Result<IReadOnlyList<DeviceDto>>>;

public sealed class GetDevicesQueryHandler : IRequestHandler<GetDevicesQuery, Result<IReadOnlyList<DeviceDto>>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly IStateCacheService _stateCache;
    private readonly ITelemetryStore? _telemetryStore;
    private readonly IDeviceAttributeRepository? _attributeRepository;
    private readonly IMqttPeerEndpointRegistry? _peerEndpointRegistry;

    public GetDevicesQueryHandler(
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

    public async Task<Result<IReadOnlyList<DeviceDto>>> Handle(GetDevicesQuery request, CancellationToken cancellationToken)
    {
        var devices = await _deviceRepository.GetByTenantAsync(request.TenantId, request.Status, cancellationToken);

        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, double>>? metricsByDevice = null;
        if (_telemetryStore is not null && devices.Count > 0)
        {
            metricsByDevice = await _telemetryStore.GetLatestMetricsByDevicesAsync(
                request.TenantId,
                devices.Select(x => x.Id).ToArray(),
                cancellationToken);
        }

        // Nombres de los nodos dueños (Customer) en UNA sola consulta que RESPETA los filtros
        // (RLS por subárbol + EF por tenant, SIN IgnoreQueryFilters). Los customers devueltos son
        // exactamente el subárbol visible de la sesión, el mismo del que cuelgan los devices visibles,
        // así que el nombre se resuelve en memoria sin consultas por dispositivo ni fuga de subárbol.
        var customerNames = (await _customerRepository.GetVisibleAsync(cancellationToken))
            .ToDictionary(c => c.Id, c => c.Name);

        var list = new List<DeviceDto>(devices.Count);
        foreach (var d in devices)
        {
            var cachedPos = await _stateCache.GetLatestPositionAsync(d.TenantId, d.Id, cancellationToken);

            string? customerName = d.CustomerId is Guid cid && customerNames.TryGetValue(cid, out var name)
                ? name
                : null;

            var resolvedMetrics = metricsByDevice is not null && metricsByDevice.TryGetValue(d.Id, out var devMetrics) ? devMetrics : null;
            int? channelCount = SwitchChannelCountResolver.Resolve(d, resolvedMetrics);
            string? wifiSsid = null;
            string? localIp = null;

            if (_attributeRepository is not null)
            {
                var attrs = await _attributeRepository.ListByDeviceAsync(d.Id, cancellationToken);
                foreach (var attr in attrs)
                {
                    if (string.Equals(attr.Key, "wifi_ssid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(attr.Value))
                        wifiSsid = attr.Value;
                    else if (string.Equals(attr.Key, "local_ip", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(attr.Value))
                        localIp = attr.Value;
                    else if (string.Equals(attr.Key, "channel_count", StringComparison.OrdinalIgnoreCase) &&
                             int.TryParse(attr.Value, out int attrCh) && attrCh is >= 1 and <= 16)
                        channelCount = Math.Max(channelCount ?? 1, attrCh);
                }
            }

            if (string.IsNullOrWhiteSpace(localIp) && _peerEndpointRegistry is not null)
            {
                localIp = _peerEndpointRegistry.GetPeerIpByChipId(d.ChipId);
            }

            list.Add(new DeviceDto(
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
                resolvedMetrics,
                d.FirmwareVersion,
                channelCount,
                wifiSsid,
                localIp));
        }

        return Result<IReadOnlyList<DeviceDto>>.Success(list);
    }
}
