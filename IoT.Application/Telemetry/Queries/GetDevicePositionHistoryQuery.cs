using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using MediatR;

namespace IoT.Application.Telemetry.Queries;

public sealed record GetDevicePositionHistoryQuery(
    Guid TenantId,
    Guid DeviceId,
    DateTime FromUtc,
    DateTime ToUtc,
    int Limit = 500,
    string? Source = null) : IRequest<Result<IReadOnlyList<HistoricalPositionDto>>>;

public sealed class GetDevicePositionHistoryQueryHandler : IRequestHandler<GetDevicePositionHistoryQuery, Result<IReadOnlyList<HistoricalPositionDto>>>
{
    private readonly ITelemetryStore _telemetryStore;

    public GetDevicePositionHistoryQueryHandler(ITelemetryStore telemetryStore)
    {
        _telemetryStore = telemetryStore;
    }

    public async Task<Result<IReadOnlyList<HistoricalPositionDto>>> Handle(GetDevicePositionHistoryQuery request, CancellationToken cancellationToken)
    {
        var positions = await _telemetryStore.GetPositionsAsync(
            request.TenantId,
            request.DeviceId,
            request.FromUtc,
            request.ToUtc,
            request.Limit,
            request.Source,
            cancellationToken);

        return Result<IReadOnlyList<HistoricalPositionDto>>.Success(positions);
    }
}
