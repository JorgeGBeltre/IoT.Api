using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Fleet.Queries;

public sealed record GetFleetLivePositionsQuery(Guid TenantId) : IRequest<Result<IReadOnlyList<LivePositionSnapshot>>>;

public sealed class GetFleetLivePositionsQueryHandler : IRequestHandler<GetFleetLivePositionsQuery, Result<IReadOnlyList<LivePositionSnapshot>>>
{
    private readonly IStateCacheService _stateCache;
    private readonly IDeviceRepository? _deviceRepository;
    private readonly ITenantContext? _tenantContext;

    public GetFleetLivePositionsQueryHandler(
        IStateCacheService stateCache,
        IDeviceRepository? deviceRepository = null,
        ITenantContext? tenantContext = null)
    {
        _stateCache = stateCache;
        _deviceRepository = deviceRepository;
        _tenantContext = tenantContext;
    }

    public async Task<Result<IReadOnlyList<LivePositionSnapshot>>> Handle(GetFleetLivePositionsQuery request, CancellationToken cancellationToken)
    {
        if (request.TenantId == Guid.Empty)
            return Result<IReadOnlyList<LivePositionSnapshot>>.Failure(new Error("Fleet.InvalidTenant", "TenantId cannot be empty."));

        var snapshot = await _stateCache.GetFleetSnapshotAsync(request.TenantId, cancellationToken);
        if (_tenantContext?.CustomerSubtreeIds is not null && _deviceRepository is not null)
        {
            var visibleDevices = await _deviceRepository.GetByTenantAsync(request.TenantId, null, cancellationToken);
            var allowedIds = visibleDevices.Select(d => d.Id).ToHashSet();
            var allowedChipIds = visibleDevices
                .Select(d => d.ChipId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            snapshot = snapshot
                .Where(s => allowedIds.Contains(s.DeviceId) || allowedChipIds.Contains(s.DeviceIdentifier))
                .ToList();
        }

        return Result<IReadOnlyList<LivePositionSnapshot>>.Success(snapshot);
    }
}
