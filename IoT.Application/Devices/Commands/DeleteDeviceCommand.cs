using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Commands;

public sealed record DeleteDeviceCommand(
    Guid TenantId,
    Guid DeviceId,
    Guid? PerformedByUserId = null,
    string? PerformedByUsername = null,
    string? CallerIpAddress = null,
    string? UserAgent = null) : IRequest<Result>;

public sealed class DeleteDeviceCommandHandler : IRequestHandler<DeleteDeviceCommand, Result>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditLogRepository? _auditLogRepository;
    private readonly IMqttSessionRevoker? _sessionRevoker;
    private readonly IDeviceAttributeRepository? _attributeRepository;
    private readonly IMqttPublisher? _mqttPublisher;
    private readonly IMqttClientMap? _mqttClientMap;
    private readonly IMqttPeerEndpointRegistry? _peerEndpointRegistry;
    private readonly IUnlinkedDeviceRegistry? _unlinkedDeviceRegistry;
    private readonly IFleetNotificationService? _notificationService;

    public DeleteDeviceCommandHandler(
        IDeviceRepository deviceRepository,
        IUnitOfWork unitOfWork,
        IAuditLogRepository? auditLogRepository = null,
        IMqttSessionRevoker? sessionRevoker = null,
        IDeviceAttributeRepository? attributeRepository = null,
        IMqttPublisher? mqttPublisher = null,
        IMqttClientMap? mqttClientMap = null,
        IMqttPeerEndpointRegistry? peerEndpointRegistry = null,
        IUnlinkedDeviceRegistry? unlinkedDeviceRegistry = null,
        IFleetNotificationService? notificationService = null)
    {
        _deviceRepository = deviceRepository;
        _unitOfWork = unitOfWork;
        _auditLogRepository = auditLogRepository;
        _sessionRevoker = sessionRevoker;
        _attributeRepository = attributeRepository;
        _mqttPublisher = mqttPublisher;
        _mqttClientMap = mqttClientMap;
        _peerEndpointRegistry = peerEndpointRegistry;
        _unlinkedDeviceRegistry = unlinkedDeviceRegistry;
        _notificationService = notificationService;
    }

    public async Task<Result> Handle(DeleteDeviceCommand request, CancellationToken cancellationToken)
    {
        var result = await UnlinkDeviceCommandHandler.ExecuteUnlinkAsync(
            new UnlinkDeviceCommand(
                request.TenantId,
                request.DeviceId,
                request.PerformedByUserId,
                request.PerformedByUsername,
                request.CallerIpAddress,
                request.UserAgent),
            _deviceRepository,
            _unitOfWork,
            _auditLogRepository,
            _sessionRevoker,
            _attributeRepository,
            _mqttPublisher,
            _mqttClientMap,
            _peerEndpointRegistry,
            _unlinkedDeviceRegistry,
            _notificationService,
            allowTransportFlushDelay: _mqttClientMap is not null && _sessionRevoker is not null,
            cancellationToken);

        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }
}
