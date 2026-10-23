using IoT.Domain.Common;
using IoT.Domain.Enums;

namespace IoT.Domain.Entities;

public sealed class DeviceCommand : Entity<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public Guid DeviceId { get; private set; }
    public string CommandName { get; private set; }
    public string PayloadJson { get; private set; }
    public CommandStatus Status { get; private set; }
    public string RequestedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? SentAt { get; private set; }
    public DateTime? DeliveredAt { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public string? ResultJson { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? ClientRequestId { get; private set; }

    private DeviceCommand(
        Guid id,
        Guid tenantId,
        Guid deviceId,
        string commandName,
        string payloadJson,
        string requestedBy,
        string? clientRequestId = null) : base(id)
    {
        TenantId = tenantId;
        DeviceId = deviceId;
        CommandName = commandName;
        PayloadJson = payloadJson;
        Status = CommandStatus.Pending;
        RequestedBy = requestedBy;
        CreatedAt = DateTime.UtcNow;
        ClientRequestId = clientRequestId;
    }

    public static Result<DeviceCommand> Create(
        Guid tenantId,
        Guid deviceId,
        string commandName,
        string? payloadJson,
        string requestedBy,
        string? clientRequestId = null)
    {
        if (tenantId == Guid.Empty)
            return Result<DeviceCommand>.Failure(new Error("DeviceCommand.InvalidTenant", "TenantId cannot be empty."));

        if (deviceId == Guid.Empty)
            return Result<DeviceCommand>.Failure(new Error("DeviceCommand.InvalidDevice", "DeviceId cannot be empty."));

        if (string.IsNullOrWhiteSpace(commandName))
            return Result<DeviceCommand>.Failure(new Error("DeviceCommand.InvalidCommandName", "Command name cannot be empty."));

        return new DeviceCommand(
            Guid.NewGuid(),
            tenantId,
            deviceId,
            commandName.Trim(),
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            string.IsNullOrWhiteSpace(requestedBy) ? "System" : requestedBy.Trim(),
            string.IsNullOrWhiteSpace(clientRequestId) ? null : clientRequestId.Trim());
    }

    public void MarkAsSent()
    {
        Status = CommandStatus.Sent;
        SentAt = DateTime.UtcNow;
    }

    public void MarkAsDelivered(string? deliveryAckJson = null)
    {
        if (Status == CommandStatus.Confirmed || Status == CommandStatus.Failed)
            return;

        Status = CommandStatus.Delivered;
        DeliveredAt = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(deliveryAckJson))
            ResultJson = deliveryAckJson;
    }

    public void Confirm(string? resultJson = null)
    {
        Status = CommandStatus.Confirmed;
        ConfirmedAt = DateTime.UtcNow;
        ResultJson = resultJson;
    }

    public void Fail(string errorMessage)
    {
        Status = CommandStatus.Failed;
        ErrorMessage = errorMessage;
        ConfirmedAt = DateTime.UtcNow;
    }

    public void Expire()
    {
        Status = CommandStatus.TimedOut;
        ErrorMessage = "Command timed out without confirmation from device.";
        ConfirmedAt = DateTime.UtcNow;
    }
}
