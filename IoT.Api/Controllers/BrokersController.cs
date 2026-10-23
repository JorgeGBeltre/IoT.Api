using IoT.Api.Common;
using IoT.Application.Brokers;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Controllers;

/// <summary>
/// Gestión de endpoints MQTT configurados por el tenant. Cada broker es un
/// endpoint individual (host:port + protocolo + autenticación) que la plataforma
/// puede consumir para ingesta de telemetría y despacho de comandos.
/// </summary>
[Authorize]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/brokers")]
public sealed class BrokersController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITenantContext _tenantContext;

    public BrokersController(IMediator mediator, ITenantContext tenantContext)
    {
        _mediator = mediator;
        _tenantContext = tenantContext;
    }

    public sealed record UpsertBrokerRequest(
        string Name,
        string Host,
        int Port,
        string Protocol,
        bool TlsEnabled,
        string AuthMethod,
        bool IsActive = true);

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var result = await _mediator.Send(new GetBrokersQuery(_tenantContext.TenantId), ct);
        return result.IsFailure
            ? BadRequest(new { error = result.Error.Message, code = result.Error.Code })
            : Ok(result.Value);
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] UpsertBrokerRequest request, CancellationToken ct)
    {
        var cmd = new CreateBrokerCommand(
            _tenantContext.TenantId, request.Name, request.Host, request.Port,
            request.Protocol, request.TlsEnabled, request.AuthMethod, request.IsActive);

        var result = await _mediator.Send(cmd, ct);
        if (result.IsFailure)
            return BadRequest(new { error = result.Error.Message, code = result.Error.Code });

        return CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var result = await _mediator.Send(new GetBrokersQuery(_tenantContext.TenantId), ct);
        if (result.IsFailure)
            return BadRequest(new { error = result.Error.Message, code = result.Error.Code });

        var found = result.Value.FirstOrDefault(b => b.Id == id);
        return found is null ? NotFound() : Ok(found);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertBrokerRequest request, CancellationToken ct)
    {
        var cmd = new UpdateBrokerCommand(
            _tenantContext.TenantId, id, request.Name, request.Host, request.Port,
            request.Protocol, request.TlsEnabled, request.AuthMethod, request.IsActive);

        var result = await _mediator.Send(cmd, ct);
        if (result.IsFailure)
        {
            if (result.Error.Code == "Broker.NotFound")
                return NotFound(new { error = result.Error.Message });
            return BadRequest(new { error = result.Error.Message, code = result.Error.Code });
        }
        return Ok(result.Value);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await _mediator.Send(new DeleteBrokerCommand(_tenantContext.TenantId, id), ct);
        if (result.IsFailure)
        {
            if (result.Error.Code == "Broker.NotFound")
                return NotFound(new { error = result.Error.Message });
            return BadRequest(new { error = result.Error.Message, code = result.Error.Code });
        }
        return NoContent();
    }
}
