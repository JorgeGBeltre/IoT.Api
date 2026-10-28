using IoT.Api.Common;
using IoT.Application.Customers.Commands;
using IoT.Application.Customers.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Controllers;

/// <summary>
/// Operación de la jerarquía multinivel Customer (ADR-013 E8.4b). Todas las rutas exigen el rol
/// <see cref="AuthorizationPolicies.CustomerAdmin"/>; el RECORTE por subárbol lo aplican
/// automáticamente la RLS de PostgreSQL y el filtro de EF (la política sólo controla el rol).
/// Controlador fino: sólo media a MediatR.
/// </summary>
[Authorize(Policy = AuthorizationPolicies.CustomerAdmin)]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/customers")]
public sealed class CustomersController : ControllerBase
{
    private readonly IMediator _mediator;

    public CustomersController(IMediator mediator)
    {
        _mediator = mediator;
    }

    public sealed record CreateCustomerRequest(Guid? ParentCustomerId, string Name, string? Code);

    public sealed record UpdateCustomerRequest(string? Name, bool? IsActive);

    public sealed record ReparentCustomerRequest(Guid? NewParentCustomerId);

    /// <summary>Lista el subárbol visible de la sesión (lista plana; el cliente arma el árbol).</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCustomers(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetCustomersQuery(), cancellationToken);
        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCustomerById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetCustomerByIdQuery(id), cancellationToken);
        if (result.IsFailure)
        {
            return NotFound(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateCustomer([FromBody] CreateCustomerRequest request, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new CreateCustomerCommand(request.ParentCustomerId, request.Name, request.Code),
            cancellationToken);
        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error.Message });
        }

        return CreatedAtAction(nameof(GetCustomerById), new { id = result.Value.Id }, result.Value);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCustomer(Guid id, [FromBody] UpdateCustomerRequest request, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new UpdateCustomerCommand(id, request.Name, request.IsActive),
            cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.Code == "Customer.NotFound"
                ? NotFound(new { error = result.Error.Message })
                : BadRequest(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }

    /// <summary>Reubica un customer bajo un nuevo padre en la jerarquía y actualiza su subárbol.</summary>
    [HttpPost("{id:guid}/reparent")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReparentCustomer(Guid id, [FromBody] ReparentCustomerRequest request, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new ReparentCustomerCommand(id, request.NewParentCustomerId),
            cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.Code == "Customer.NotFound"
                ? NotFound(new { error = result.Error.Message })
                : BadRequest(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }
}
