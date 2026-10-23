using IoT.Api.Common;
using IoT.Application.TenantSettings;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Controllers;

/// <summary>
/// Configuración operativa del tenant actual: retención histórica, cola offline
/// de comandos, proveedor de mapas y webhook de eventos.
/// </summary>
[Authorize]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/tenants/current/settings")]
public sealed class TenantSettingsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITenantContext _tenantContext;

    public TenantSettingsController(IMediator mediator, ITenantContext tenantContext)
    {
        _mediator = mediator;
        _tenantContext = tenantContext;
    }

    public sealed record UpdateTenantSettingsRequest(
        int RetentionDays,
        bool AllowOfflineQueue,
        string MapProvider,
        string WebhookUrl);

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var result = await _mediator.Send(new GetTenantSettingsQuery(_tenantContext.TenantId), ct);
        return result.IsFailure
            ? BadRequest(new { error = result.Error.Message, code = result.Error.Code })
            : Ok(result.Value);
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update([FromBody] UpdateTenantSettingsRequest request, CancellationToken ct)
    {
        var cmd = new UpdateTenantSettingsCommand(
            _tenantContext.TenantId,
            request.RetentionDays,
            request.AllowOfflineQueue,
            request.MapProvider,
            request.WebhookUrl);

        var result = await _mediator.Send(cmd, ct);
        return result.IsFailure
            ? BadRequest(new { error = result.Error.Message, code = result.Error.Code })
            : Ok(result.Value);
    }
}
