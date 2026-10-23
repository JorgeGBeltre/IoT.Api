using IoT.Api.Common;
using IoT.Application.Telemetry.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Microsoft.AspNetCore.Authorization;

namespace IoT.Api.Controllers;

[Authorize]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/devices/{deviceId:guid}")]
public sealed class TelemetryController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITenantContext _tenantContext;

    public TelemetryController(IMediator mediator, ITenantContext tenantContext)
    {
        _mediator = mediator;
        _tenantContext = tenantContext;
    }

    [HttpGet("positions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistoricalPositions(
        Guid deviceId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int limit = 500,
        [FromQuery] string? source = null,
        CancellationToken cancellationToken = default)
    {
        var fromUtc = from ?? DateTime.UtcNow.AddDays(-1);
        var toUtc = to ?? DateTime.UtcNow;

        var query = new GetDevicePositionHistoryQuery(_tenantContext.TenantId, deviceId, fromUtc, toUtc, limit, source);
        var result = await _mediator.Send(query, cancellationToken);
        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error.Message });
        }

        return Ok(new
        {
            deviceId,
            from = fromUtc,
            to = toUtc,
            source,
            count = result.Value.Count,
            positions = result.Value
        });
    }

    [HttpGet("metrics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistoricalMetrics(
        Guid deviceId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int limit = 500,
        [FromQuery] string? source = null,
        CancellationToken cancellationToken = default)
    {
        var fromUtc = from ?? DateTime.UtcNow.AddDays(-1);
        var toUtc = to ?? DateTime.UtcNow;

        var query = new GetDeviceTelemetryHistoryQuery(_tenantContext.TenantId, deviceId, fromUtc, toUtc, limit, source);
        var result = await _mediator.Send(query, cancellationToken);
        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error.Message });
        }

        return Ok(new
        {
            deviceId,
            from = fromUtc,
            to = toUtc,
            source,
            count = result.Value.Count,
            metrics = result.Value
        });
    }
}
