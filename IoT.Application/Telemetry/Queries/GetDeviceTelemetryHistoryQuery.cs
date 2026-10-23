using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using MediatR;

namespace IoT.Application.Telemetry.Queries;

public sealed record GetDeviceTelemetryHistoryQuery(
    Guid TenantId,
    Guid DeviceId,
    DateTime FromUtc,
    DateTime ToUtc,
    int Limit = 500,
    string? Source = null) : IRequest<Result<IReadOnlyList<HistoricalTelemetryDto>>>;

public sealed class GetDeviceTelemetryHistoryQueryHandler : IRequestHandler<GetDeviceTelemetryHistoryQuery, Result<IReadOnlyList<HistoricalTelemetryDto>>>
{
    private readonly ITelemetryStore _telemetryStore;

    public GetDeviceTelemetryHistoryQueryHandler(ITelemetryStore telemetryStore)
    {
        _telemetryStore = telemetryStore;
    }

    public async Task<Result<IReadOnlyList<HistoricalTelemetryDto>>> Handle(GetDeviceTelemetryHistoryQuery request, CancellationToken cancellationToken)
    {
        var telemetry = await _telemetryStore.GetTelemetryAsync(
            request.TenantId,
            request.DeviceId,
            request.FromUtc,
            request.ToUtc,
            request.Limit,
            request.Source,
            cancellationToken);

        return Result<IReadOnlyList<HistoricalTelemetryDto>>.Success(telemetry);
    }
}
