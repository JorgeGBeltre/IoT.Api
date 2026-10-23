using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using MediatR;

namespace IoT.Application.Fleet.Queries;

public sealed record GetFleetLivePositionsQuery(Guid TenantId) : IRequest<Result<IReadOnlyList<LivePositionSnapshot>>>;

public sealed class GetFleetLivePositionsQueryHandler : IRequestHandler<GetFleetLivePositionsQuery, Result<IReadOnlyList<LivePositionSnapshot>>>
{
    private readonly IStateCacheService _stateCache;

    public GetFleetLivePositionsQueryHandler(IStateCacheService stateCache)
    {
        _stateCache = stateCache;
    }

    public async Task<Result<IReadOnlyList<LivePositionSnapshot>>> Handle(GetFleetLivePositionsQuery request, CancellationToken cancellationToken)
    {
        if (request.TenantId == Guid.Empty)
            return Result<IReadOnlyList<LivePositionSnapshot>>.Failure(new Error("Fleet.InvalidTenant", "TenantId cannot be empty."));

        var snapshot = await _stateCache.GetFleetSnapshotAsync(request.TenantId, cancellationToken);
        return Result<IReadOnlyList<LivePositionSnapshot>>.Success(snapshot);
    }
}
