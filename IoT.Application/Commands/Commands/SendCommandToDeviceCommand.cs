using System.Text.Json;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using MediatR;
using Microsoft.Extensions.Logging;

namespace IoT.Application.Commands.Commands;

public sealed record SendCommandToDeviceCommand(
    Guid TenantId,
    Guid DeviceId,
    string CommandName,
    string? PayloadJson = null,
    string RequestedBy = "Admin",
    string? ClientRequestId = null,
    bool AllowOfflineQueueing = true) : IRequest<Result<Guid>>;

public sealed class SendCommandToDeviceCommandHandler : IRequestHandler<SendCommandToDeviceCommand, Result<Guid>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDeviceCommandRepository _commandRepository;
    private readonly IMqttPublisher _mqttPublisher;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SendCommandToDeviceCommandHandler> _logger;
    private readonly IoT.Protocols.Routing.ITopicRoutingService? _topicRoutingService;
    private readonly ITcpCommandGateway? _tcpCommandGateway;
    private readonly IMqttClientMap? _mqttClientMap;

    public SendCommandToDeviceCommandHandler(
        IDeviceRepository deviceRepository,
        IDeviceCommandRepository commandRepository,
        IMqttPublisher mqttPublisher,
        IUnitOfWork unitOfWork,
        ILogger<SendCommandToDeviceCommandHandler> logger,
        IoT.Protocols.Routing.ITopicRoutingService? topicRoutingService = null,
        ITcpCommandGateway? tcpCommandGateway = null,
        IMqttClientMap? mqttClientMap = null)
    {
        _deviceRepository = deviceRepository;
        _commandRepository = commandRepository;
        _mqttPublisher = mqttPublisher;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _topicRoutingService = topicRoutingService;
        _tcpCommandGateway = tcpCommandGateway;
        _mqttClientMap = mqttClientMap;
    }

    public async Task<Result<Guid>> Handle(SendCommandToDeviceCommand request, CancellationToken cancellationToken)
    {
        // 1. Idempotency Check (E5-01)
        if (!string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            var existing = await _commandRepository.GetByClientRequestIdAsync(request.TenantId, request.ClientRequestId, cancellationToken);
            if (existing is not null)
            {
                _logger.LogInformation("Idempotent command dispatch detected for ClientRequestId {ClientRequestId}: returning existing command {CommandId} (Status: {Status})",
                    request.ClientRequestId, existing.Id, existing.Status);
                return Result<Guid>.Success(existing.Id);
            }
        }

        // 2. Load device and verify tenant ownership
        var device = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (device is null || device.TenantId != request.TenantId)
        {
            return Result<Guid>.Failure(new Error("Device.NotFound", $"Device '{request.DeviceId}' was not found."));
        }

        // 3. E5-02: Device AdminStatus & OperationalStatus validation
        if (device.AdminStatus != IoT.Domain.Enums.DeviceAdminStatus.Active)
        {
            return Result<Guid>.Failure(new Error("Device.Suspended",
                $"Device '{device.Id}' has administrative status '{device.AdminStatus}' and cannot receive commands."));
        }

        if (device.OperationalStatus == IoT.Domain.Enums.DeviceOperationalStatus.Error)
        {
            return Result<Guid>.Failure(new Error("Device.InErrorState",
                $"Device '{device.Id}' is currently in Error status and cannot safely execute commands."));
        }

        if (device.OperationalStatus == IoT.Domain.Enums.DeviceOperationalStatus.Offline)
        {
            if (_mqttClientMap != null)
            {
                var activeClientId = await _mqttClientMap.GetClientIdAsync(device.ChipId, cancellationToken);
                if (!string.IsNullOrWhiteSpace(activeClientId))
                {
                    _logger.LogInformation("Device {DeviceId} ({ChipId}) has active MQTT session {ClientId}; healing presence to Online.",
                        device.Id, device.ChipId, activeClientId);
                    device.RecordHeartbeat(DateTime.UtcNow);
                    _deviceRepository.Update(device);
                }
            }

            if (device.OperationalStatus == IoT.Domain.Enums.DeviceOperationalStatus.Offline && !request.AllowOfflineQueueing)
            {
                return Result<Guid>.Failure(new Error("Device.Offline",
                    $"Device '{device.Id}' is currently Offline. Enable AllowOfflineQueueing to queue for later delivery."));
            }
        }

        // 4. Create Command entity
        var commandResult = DeviceCommand.Create(
            request.TenantId,
            device.Id,
            request.CommandName,
            request.PayloadJson,
            request.RequestedBy,
            request.ClientRequestId);

        if (commandResult.IsFailure)
        {
            return Result<Guid>.Failure(commandResult.Error);
        }

        var command = commandResult.Value;
        await _commandRepository.AddAsync(command, cancellationToken);

        // 5. Check if device is connected via TCP binary gateway (ADR-007)
        if (_tcpCommandGateway != null && _tcpCommandGateway.IsConnected(device.ChipId))
        {
            try
            {
                bool tcpSent = await _tcpCommandGateway.SendCommandAsync(device.ChipId, request.CommandName, cancellationToken);
                if (tcpSent)
                {
                    command.MarkAsSent();
                    Common.Observability.IoTMetrics.CommandsDispatchedCounter.Add(1,
                        new KeyValuePair<string, object?>("transport", "tcp"),
                        new KeyValuePair<string, object?>("command", request.CommandName));

                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    return Result<Guid>.Success(command.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send command over active TCP socket for device {ChipId}, attempting MQTT fallback.", device.ChipId);
            }
        }

        // 6. Prepare MQTT command envelope with correlation and response topic
        string responseTopic = $"devices/{device.ChipId}/commands/ack";
        var envelope = new
        {
            command_id = command.Id,
            client_request_id = request.ClientRequestId,
            command = request.CommandName,
            payload = request.PayloadJson,
            response_topic = responseTopic,
            sent_at = DateTime.UtcNow
        };

        // E3-02: Central topic routing factory
        string topic = _topicRoutingService?.BuildCommandTopic(device.Model, device.ChipId, device.TenantId)
            ?? $"devices/{device.ChipId}/commands";

        uint expirySeconds = (uint)(_topicRoutingService?.GetDefaultCommandExpiry(device.Model).TotalSeconds ?? 300);

        // El envelope genérico vale para los dispositivos de la plataforma, pero algunos firmwares
        // (p.ej. Toour/Fligo) sólo entienden su propio formato {"EventType":...}; su CommandParser
        // descarta cualquier otra cosa. Si el perfil del dispositivo define una codificación propia
        // se usa esa; si no, el envelope genérico.
        string serializedEnvelope =
            _topicRoutingService?.BuildCommandPayload(device.Model, request.CommandName, request.PayloadJson)
            ?? JsonSerializer.Serialize(envelope);

        try
        {
            var publishOptions = new MqttPublishOptions
            {
                Qos = 1,
                Retain = false,
                MessageExpiryInterval = expirySeconds,
                ContentType = "application/json",
                ResponseTopic = responseTopic,
                CorrelationData = System.Text.Encoding.UTF8.GetBytes(command.Id.ToString()),
                UserProperties = new Dictionary<string, string>
                {
                    ["command_id"] = command.Id.ToString(),
                    ["tenant_id"] = request.TenantId.ToString(),
                    ["client_request_id"] = request.ClientRequestId ?? string.Empty
                }
            };

            await _mqttPublisher.PublishAsync(topic, serializedEnvelope, publishOptions, cancellationToken);
            command.MarkAsSent();
            Common.Observability.IoTMetrics.CommandsDispatchedCounter.Add(1,
                new KeyValuePair<string, object?>("transport", "mqtt"),
                new KeyValuePair<string, object?>("command", request.CommandName));

            _logger.LogInformation("Dispatched command {CommandName} ({CommandId}) to MQTT topic {Topic} (expiry: {Expiry}s)",
                request.CommandName, command.Id, topic, expirySeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish MQTT command {CommandId} to topic {Topic}", command.Id, topic);
            command.Fail($"MQTT dispatch failed: {ex.Message}");
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<Guid>.Success(command.Id);
    }
}
