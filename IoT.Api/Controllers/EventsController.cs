using IoT.Api.Common;
using IoT.Application.Common.Interfaces;
using IoT.Application.Events.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Controllers;

/// <summary>
/// Bitácora y stream histórico de eventos, alertas de hardware y comandos del tenant.
/// Los datos provienen de comandos confirmados/fallidos y alertas de telemetría registradas.
/// </summary>
[Authorize]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/events")]
public sealed class EventsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITenantContext _tenantContext;

    public EventsController(IMediator mediator, ITenantContext tenantContext)
    {
        _mediator = mediator;
        _tenantContext = tenantContext;
    }

    [HttpGet]
    [HttpGet("~/api/v1/devices/{deviceId:guid}/events")]
    [ProducesResponseType(typeof(List<EventItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEvents(
        [FromRoute] Guid? deviceId = null,
        [FromQuery] Guid? queryDeviceId = null,
        [FromQuery] string? severity = null,
        [FromQuery] string? eventType = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var targetDeviceId = deviceId ?? queryDeviceId;

        var query = new GetEventsQuery(
            _tenantContext.TenantId,
            targetDeviceId,
            severity,
            eventType,
            from,
            to,
            limit);

        var result = await _mediator.Send(query, cancellationToken);
        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }
}
