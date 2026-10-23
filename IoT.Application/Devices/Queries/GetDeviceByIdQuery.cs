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
    private readonly IStateCacheService _stateCache;

    public GetDeviceByIdQueryHandler(IDeviceRepository deviceRepository, IStateCacheService stateCache)
    {
        _deviceRepository = deviceRepository;
        _stateCache = stateCache;
    }

    public async Task<Result<DeviceDto>> Handle(GetDeviceByIdQuery request, CancellationToken cancellationToken)
    {
        var d = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (d is null || d.TenantId != request.TenantId)
        {
            return Result<DeviceDto>.Failure(new Error("Device.NotFound", $"Device '{request.DeviceId}' was not found."));
        }

        var cachedPos = await _stateCache.GetLatestPositionAsync(d.TenantId, d.Id, cancellationToken);

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
            cachedPos);

        return Result<DeviceDto>.Success(dto);
    }
}
