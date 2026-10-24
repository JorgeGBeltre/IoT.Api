using IoT.Api.Common;
using IoT.Application.Commands.Commands;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Microsoft.AspNetCore.Authorization;

namespace IoT.Api.Controllers;

[Authorize]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/devices/{deviceId:guid}/commands")]
public sealed class CommandsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITenantContext _tenantContext;

    public CommandsController(IMediator mediator, ITenantContext tenantContext)
    {
        _mediator = mediator;
        _tenantContext = tenantContext;
    }

    public sealed record SendDeviceCommandRequest(
        string CommandName,
        string? PayloadJson = null,
        string? RequestedBy = null,
        string? ClientRequestId = null,
        bool AllowOfflineQueueing = false);

    public sealed record CommandAckRequest(
        string Status,
        string? ResultPayload = null,
        string? ErrorMessage = null);

    // Accionamiento físico de un dispositivo: exige rol explícito, no sólo sesión válida.
    [Authorize(Policy = AuthorizationPolicies.DeviceCommand)]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DispatchCommand(
        Guid deviceId,
        [FromBody] SendDeviceCommandRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var authenticatedUser = User.Identity?.Name ?? User.FindFirst("unique_name")?.Value ?? "Operator";
        string? effectiveRequestId = !string.IsNullOrWhiteSpace(idempotencyKey) ? idempotencyKey : request.ClientRequestId;

        var command = new SendCommandToDeviceCommand(
            _tenantContext.TenantId,
            deviceId,
            request.CommandName,
            request.PayloadJson,
            authenticatedUser,
            effectiveRequestId,
            request.AllowOfflineQueueing);

        var result = await _mediator.Send(command, cancellationToken);
        if (result.IsFailure)
        {
            if (result.Error.Code == "Device.NotFound")
                return NotFound(new { error = result.Error.Message });

            if (result.Error.Code == "Device.Suspended" || result.Error.Code == "Device.Offline" || result.Error.Code == "Device.InErrorState")
                return Conflict(new { error = result.Error.Message, code = result.Error.Code });

            return BadRequest(new { error = result.Error.Message, code = result.Error.Code });
        }

        return Accepted(new { commandId = result.Value, status = "Sent" });
    }

    // Confirmar un comando cambia su estado y su auditoría: también exige rol.
    [Authorize(Policy = AuthorizationPolicies.DeviceCommand)]
    [HttpPost("{commandId:guid}/ack")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcknowledgeCommand(
        Guid deviceId,
        Guid commandId,
        [FromBody] CommandAckRequest request,
        CancellationToken cancellationToken)
    {
        var ackCommand = new AcknowledgeDeviceCommand(
            CommandId: commandId,
            ClientRequestId: null,
            Status: request.Status,
            ResultPayload: request.ResultPayload,
            ErrorMessage: request.ErrorMessage,
            TenantId: _tenantContext.TenantId);

        var result = await _mediator.Send(ackCommand, cancellationToken);
        if (result.IsFailure)
        {
            return NotFound(new { error = result.Error.Message });
        }

        return Ok(new { commandId = result.Value, status = request.Status });
    }
}
