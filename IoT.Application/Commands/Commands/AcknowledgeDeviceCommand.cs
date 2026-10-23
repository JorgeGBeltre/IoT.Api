using System.Text.Json;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using MediatR;
using Microsoft.Extensions.Logging;

namespace IoT.Application.Commands.Commands;

public sealed record AcknowledgeDeviceCommand(
    Guid? CommandId,
    string? ClientRequestId,
    string Status,
    string? ResultPayload = null,
    string? ErrorMessage = null,
    string? DeviceIdentifier = null,
    Guid? TenantId = null) : IRequest<Result<Guid>>;

public sealed class AcknowledgeDeviceCommandHandler : IRequestHandler<AcknowledgeDeviceCommand, Result<Guid>>
{
    private readonly IDeviceCommandRepository _commandRepository;
    private readonly IDeviceRepository _deviceRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFleetNotificationService? _notificationService;
    private readonly ILogger<AcknowledgeDeviceCommandHandler> _logger;

    public AcknowledgeDeviceCommandHandler(
        IDeviceCommandRepository commandRepository,
        IDeviceRepository deviceRepository,
        IUnitOfWork unitOfWork,
        ILogger<AcknowledgeDeviceCommandHandler> logger,
        IFleetNotificationService? notificationService = null)
    {
        _commandRepository = commandRepository;
        _deviceRepository = deviceRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _notificationService = notificationService;
    }

    public async Task<Result<Guid>> Handle(AcknowledgeDeviceCommand request, CancellationToken cancellationToken)
    {
        DeviceCommand? command = null;

        if (request.CommandId.HasValue && request.CommandId.Value != Guid.Empty)
        {
            command = await _commandRepository.GetByIdAsync(request.CommandId.Value, cancellationToken);
        }

        if (command is null && !string.IsNullOrWhiteSpace(request.ClientRequestId) && request.TenantId.HasValue)
        {
            command = await _commandRepository.GetByClientRequestIdAsync(request.TenantId.Value, request.ClientRequestId, cancellationToken);
        }

        if (command is null)
        {
            _logger.LogWarning("Command ACK received but command not found. CommandId: {CommandId}, ClientRequestId: {ClientRequestId}",
                request.CommandId, request.ClientRequestId);
            return Result<Guid>.Failure(new Error("Command.NotFound", "The specified command could not be located for correlation."));
        }

        string normalizedStatus = (request.Status ?? string.Empty).Trim().ToLowerInvariant();

        switch (normalizedStatus)
        {
            case "delivered":
            case "accepted":
            case "received":
                command.MarkAsDelivered(request.ResultPayload);
                _logger.LogInformation("Command {CommandId} transitioned to Delivered", command.Id);
                break;

            case "confirmed":
            case "completed":
            case "executed":
            case "success":
            case "ok":
                command.Confirm(request.ResultPayload);
                _logger.LogInformation("Command {CommandId} transitioned to Confirmed", command.Id);
                break;

            case "failed":
            case "error":
            case "rejected":
                command.Fail(request.ErrorMessage ?? "Command reported failure from device execution.");
                _logger.LogWarning("Command {CommandId} transitioned to Failed: {Error}", command.Id, command.ErrorMessage);
                break;

            default:
                _logger.LogWarning("Unrecognized command ACK status '{Status}' for command {CommandId}", request.Status, command.Id);
                command.Confirm(request.ResultPayload);
                break;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (_notificationService is not null)
        {
            try
            {
                await _notificationService.NotifyCommandStatusUpdatedAsync(
                    command.TenantId,
                    command.DeviceId,
                    command.Id,
                    command.Status.ToString(),
                    command.ResultJson ?? command.ErrorMessage,
                    DateTime.UtcNow,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to broadcast CommandStatusUpdated via SignalR for command {CommandId}", command.Id);
            }
        }

        return Result<Guid>.Success(command.Id);
    }
}
