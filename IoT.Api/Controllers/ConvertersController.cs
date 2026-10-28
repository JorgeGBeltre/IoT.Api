using IoT.Api.Common;
using IoT.Application.Common.Interfaces;
using IoT.Application.Protocols;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Controllers;

/// <summary>
/// Administración de convertidores dinámicos por tenant (adopción estilo ThingsBoard): scripts Jint
/// que decodifican la trama de un sensor/pasarela de terceros a la telemetría canónica, sin recompilar
/// el backend. Carga (PUT), lectura (GET) y listado, acotados SIEMPRE al tenant de la sesión.
///
/// La carga está gateada por rol administrador del tenant (<see cref="AuthorizationPolicies.TenantAdmin"/>)
/// y VALIDA el script en el sandbox Jint antes de persistir: JavaScript inválido o que exceda el sandbox
/// se rechaza con 400 sin guardar nada. El script nunca se ejecuta fuera del sandbox.
/// </summary>
[Authorize]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/protocol-converters")]
public sealed class ConvertersController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITenantContext _tenantContext;

    public ConvertersController(IMediator mediator, ITenantContext tenantContext)
    {
        _mediator = mediator;
        _tenantContext = tenantContext;
    }

    public sealed record UpsertConverterScriptRequest(
        string UplinkScript,
        string? DownlinkScript,
        bool IsEnabled,
        string? SamplePayload);

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var result = await _mediator.Send(new ListConverterScriptsQuery(_tenantContext.TenantId), ct);
        return result.IsFailure
            ? BadRequest(new { error = result.Error.Message, code = result.Error.Code })
            : Ok(result.Value);
    }

    [HttpGet("{profileKey}")]
    [Authorize(Policy = AuthorizationPolicies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string profileKey, CancellationToken ct)
    {
        var result = await _mediator.Send(new GetConverterScriptQuery(_tenantContext.TenantId, profileKey), ct);
        if (result.IsFailure)
        {
            return result.Error.Code == "ConverterScript.NotFound"
                ? NotFound(new { error = result.Error.Message, code = result.Error.Code })
                : BadRequest(new { error = result.Error.Message, code = result.Error.Code });
        }
        return Ok(result.Value);
    }

    [HttpPut("{profileKey}")]
    [Authorize(Policy = AuthorizationPolicies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upsert(string profileKey, [FromBody] UpsertConverterScriptRequest request, CancellationToken ct)
    {
        var cmd = new UpsertConverterScriptCommand(
            _tenantContext.TenantId,
            profileKey,
            request.UplinkScript,
            request.DownlinkScript,
            request.IsEnabled,
            request.SamplePayload);

        var result = await _mediator.Send(cmd, ct);
        return result.IsFailure
            ? BadRequest(new { error = result.Error.Message, code = result.Error.Code })
            : Ok(result.Value);
    }
}
