using IoT.Api.Common;
using IoT.Application.Devices.Commands;
using IoT.Application.Devices.Queries;
using IoT.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Microsoft.AspNetCore.Authorization;

namespace IoT.Api.Controllers;

[Authorize]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/[controller]")]
public sealed class DevicesController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITenantContext _tenantContext;

    public DevicesController(IMediator mediator, ITenantContext tenantContext)
    {
        _mediator = mediator;
        _tenantContext = tenantContext;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDevices([FromQuery] DeviceOperationalStatus? status, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetDevicesQuery(_tenantContext.TenantId, status), cancellationToken);
        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeviceById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetDeviceByIdQuery(_tenantContext.TenantId, id), cancellationToken);
        if (result.IsFailure)
        {
            return NotFound(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }

    public sealed record RegisterDeviceRequest(
        string ChipId,
        string MacAddress,
        string Name,
        string Model,
        string Manufacturer,
        IReadOnlyList<CapabilityType>? Capabilities);

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        var command = new CreateDeviceCommand(
            _tenantContext.TenantId,
            request.ChipId,
            request.MacAddress,
            request.Name,
            request.Model,
            request.Manufacturer,
            request.Capabilities);

        var result = await _mediator.Send(command, cancellationToken);
        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error.Message });
        }

        return CreatedAtAction(nameof(GetDeviceById), new { id = result.Value }, new { id = result.Value });
    }

    /// <summary>Credenciales MQTT del dispositivo (datos de conexión + huella de la ApiKey, sin el texto plano).</summary>
    [HttpGet("{id:guid}/credentials")]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeviceCredentials(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetDeviceCredentialsQuery(_tenantContext.TenantId, id), cancellationToken);
        if (result.IsFailure)
        {
            return NotFound(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }

    public sealed record AssignDeviceToCustomerRequest(Guid TargetCustomerId);

    /// <summary>
    /// Reasigna el nodo dueño (Customer) del dispositivo dentro de la jerarquía (ADR-013 E8.4b).
    /// Exige el rol <see cref="AuthorizationPolicies.CustomerAdmin"/> por ser una operación de la
    /// jerarquía; el recorte por subárbol (device origen y customer destino) lo aplican la RLS y el
    /// filtro de EF, no la política de rol.
    /// </summary>
    [HttpPost("{id:guid}/customer")]
    [Authorize(Policy = AuthorizationPolicies.CustomerAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignToCustomer(Guid id, [FromBody] AssignDeviceToCustomerRequest request, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new AssignDeviceToCustomerCommand(id, request.TargetCustomerId),
            cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.Code is "Device.NotFound" or "Device.CustomerNotFound"
                ? NotFound(new { error = result.Error.Message })
                : BadRequest(new { error = result.Error.Message });
        }

        return NoContent();
    }

    /// <summary>
    /// Atributos del dispositivo (ADR-014, P1b) agrupados por ámbito: Client (reportados por el equipo,
    /// sólo-lectura), Shared (estado deseado que fija el operador; push diferido por protocolo) y Server
    /// (metadato de negocio). Lectura: basta sesión válida (igual que el resto de GET de device); el
    /// recorte por subárbol/tenant lo aplican la RLS + el filtro EF, no la política de rol.
    /// </summary>
    [HttpGet("{id:guid}/attributes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeviceAttributes(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetDeviceAttributesQuery(_tenantContext.TenantId, id), cancellationToken);
        if (result.IsFailure)
        {
            return NotFound(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Fija (upsert) atributos de un ámbito del dispositivo. <paramref name="scope"/> va en la ruta
    /// (<c>shared</c> o <c>server</c>); <c>client</c> se RECHAZA con 400 porque esos valores los reporta
    /// el equipo. Requiere <see cref="AuthorizationPolicies.DeviceManage"/>, coherente con el resto de
    /// mutaciones de device (registro, credenciales); el recorte por subárbol lo imponen la carga scoped
    /// del device y la RLS (<c>WITH CHECK</c>) de <c>device_attributes</c>.
    /// </summary>
    [HttpPut("{id:guid}/attributes/{scope}")]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDeviceAttributes(
        Guid id, string scope, [FromBody] Dictionary<string, string?> attributes, CancellationToken cancellationToken)
    {
        if (!TryParseScope(scope, out var parsedScope))
        {
            return BadRequest(new { error = $"Ámbito de atributo inválido: '{scope}'. Use 'shared' o 'server'." });
        }

        var result = await _mediator.Send(
            new SetDeviceAttributesCommand(id, parsedScope, attributes ?? new Dictionary<string, string?>()),
            cancellationToken);

        return result.IsSuccess ? NoContent() : MapAttributeFailure(result.Error);
    }

    /// <summary>
    /// Borra un atributo fijado por el operador (<c>shared</c>/<c>server</c>). <c>client</c> se rechaza
    /// con 400. Mismas garantías de alcance que el PUT.
    /// </summary>
    [HttpDelete("{id:guid}/attributes/{scope}/{key}")]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDeviceAttribute(
        Guid id, string scope, string key, CancellationToken cancellationToken)
    {
        if (!TryParseScope(scope, out var parsedScope))
        {
            return BadRequest(new { error = $"Ámbito de atributo inválido: '{scope}'. Use 'shared' o 'server'." });
        }

        var result = await _mediator.Send(
            new DeleteDeviceAttributeCommand(id, parsedScope, key), cancellationToken);

        return result.IsSuccess ? NoContent() : MapAttributeFailure(result.Error);
    }

    private static bool TryParseScope(string scope, out AttributeScope parsed) =>
        Enum.TryParse(scope, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);

    private IActionResult MapAttributeFailure(IoT.Domain.Common.Error error) => error.Code switch
    {
        "Device.NotFound" or "Attributes.NotFound" => NotFound(new { error = error.Message }),
        _ => BadRequest(new { error = error.Message }),
    };

    /// <summary>Genera/rota la ApiKey MQTT del dispositivo. Devuelve el texto plano UNA sola vez.</summary>
    [HttpPost("{id:guid}/credentials/rotate")]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RotateDeviceCredentials(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new RotateDeviceApiKeyCommand(_tenantContext.TenantId, id, User.Identity?.Name),
            cancellationToken);
        if (result.IsFailure)
        {
            return NotFound(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }
}
