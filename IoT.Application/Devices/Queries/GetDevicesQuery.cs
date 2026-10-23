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
    private readonly IStateCacheService _stateCache;

    public GetDevicesQueryHandler(IDeviceRepository deviceRepository, IStateCacheService stateCache)
    {
        _deviceRepository = deviceRepository;
        _stateCache = stateCache;
    }

    public async Task<Result<IReadOnlyList<DeviceDto>>> Handle(GetDevicesQuery request, CancellationToken cancellationToken)
    {
        var devices = await _deviceRepository.GetByTenantAsync(request.TenantId, request.Status, cancellationToken);

        var list = new List<DeviceDto>(devices.Count);
        foreach (var d in devices)
        {
            var cachedPos = await _stateCache.GetLatestPositionAsync(d.TenantId, d.Id, cancellationToken);

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
                cachedPos));
        }

        return Result<IReadOnlyList<DeviceDto>>.Success(list);
    }
}
