using IoT.Api.Common;
using IoT.Application.Fleet.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Microsoft.AspNetCore.Authorization;

namespace IoT.Api.Controllers;

[Authorize]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/[controller]")]
public sealed class FleetController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITenantContext _tenantContext;

    public FleetController(IMediator mediator, ITenantContext tenantContext)
    {
        _mediator = mediator;
        _tenantContext = tenantContext;
    }

    [HttpGet("snapshot")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFleetSnapshot(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetFleetLivePositionsQuery(_tenantContext.TenantId), cancellationToken);
        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error.Message });
        }

        return Ok(new
        {
            tenantId = _tenantContext.TenantId,
            timestamp = DateTime.UtcNow,
            totalUnits = result.Value.Count,
            units = result.Value
        });
    }
}
