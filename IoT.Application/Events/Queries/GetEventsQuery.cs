using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using MediatR;

namespace IoT.Application.Events.Queries;

public sealed record GetEventsQuery(
    Guid TenantId,
    Guid? DeviceId = null,
    string? Severity = null,
    string? EventType = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Limit = 100
) : IRequest<Result<IReadOnlyList<EventItemDto>>>;

public sealed class GetEventsQueryHandler : IRequestHandler<GetEventsQuery, Result<IReadOnlyList<EventItemDto>>>
{
    private readonly IEventQueryService _eventQueryService;

    public GetEventsQueryHandler(IEventQueryService eventQueryService)
    {
        _eventQueryService = eventQueryService;
    }

    public async Task<Result<IReadOnlyList<EventItemDto>>> Handle(
        GetEventsQuery request,
        CancellationToken cancellationToken)
    {
        if (request.TenantId == Guid.Empty)
        {
            return Result<IReadOnlyList<EventItemDto>>.Failure(
                new Error("Events.InvalidTenant", "TenantId is required."));
        }

        var clampedLimit = Math.Clamp(request.Limit, 1, 1000);

        var events = await _eventQueryService.GetEventsAsync(
            request.TenantId,
            request.DeviceId,
            request.Severity,
            request.EventType,
            request.FromUtc,
            request.ToUtc,
            clampedLimit,
            cancellationToken);

        return Result<IReadOnlyList<EventItemDto>>.Success(events);
    }
}
