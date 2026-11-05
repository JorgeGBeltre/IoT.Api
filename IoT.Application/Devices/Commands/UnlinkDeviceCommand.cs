using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Commands;

public sealed record DeviceUnlinkResultDto(
    Guid DeviceId,
    string ChipId,
    bool Unlinked,
    bool FactoryResetDispatchedViaMqtt,
    bool PendingOfflineUnlinkTombstoneRecorded,
    string? UnlinkProof,
    string? LocalIp,
    DateTime UnlinkedAt);

public sealed record UnlinkDeviceCommand(
    Guid TenantId,
    Guid DeviceId,
    Guid? PerformedByUserId = null,
    string? PerformedByUsername = null,
    string? CallerIpAddress = null,
    string? UserAgent = null) : IRequest<Result<DeviceUnlinkResultDto>>;

public sealed class UnlinkDeviceCommandHandler : IRequestHandler<UnlinkDeviceCommand, Result<DeviceUnlinkResultDto>>
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

    public UnlinkDeviceCommandHandler(
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

    public Task<Result<DeviceUnlinkResultDto>> Handle(UnlinkDeviceCommand request, CancellationToken cancellationToken)
        => ExecuteUnlinkAsync(
            request,
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

    internal static async Task<Result<DeviceUnlinkResultDto>> ExecuteUnlinkAsync(
        UnlinkDeviceCommand request,
        IDeviceRepository deviceRepository,
        IUnitOfWork unitOfWork,
        IAuditLogRepository? auditLogRepository,
        IMqttSessionRevoker? sessionRevoker,
        IDeviceAttributeRepository? attributeRepository,
        IMqttPublisher? mqttPublisher,
        IMqttClientMap? mqttClientMap,
        IMqttPeerEndpointRegistry? peerEndpointRegistry,
        IUnlinkedDeviceRegistry? unlinkedDeviceRegistry,
        IFleetNotificationService? notificationService,
        bool allowTransportFlushDelay,
        CancellationToken cancellationToken)
    {
        var device = await deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (device is null || device.TenantId != request.TenantId)
        {
            return Result<DeviceUnlinkResultDto>.Failure(
                new Error("Device.NotFound", "No se encontró el dispositivo solicitado o no pertenece a su cuenta."));
        }

        Guid deviceId = device.Id;
        Guid tenantId = device.TenantId;
        string chipId = device.ChipId;
        string deviceName = device.Name;
        string? mqttApiKeyHash = device.MqttApiKeyHash;
        DateTime utcNow = DateTime.UtcNow;

        string? storedDeviceKey = null;
        string? localIp = null;

        if (attributeRepository is not null)
        {
            var attrs = await attributeRepository.ListByDeviceAsync(deviceId, cancellationToken);
            foreach (var attr in attrs)
            {
                if (attr.Scope == AttributeScope.Server &&
                    string.Equals(attr.Key, "device_key", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(attr.Value))
                {
                    storedDeviceKey = attr.Value.Trim().Trim('"');
                }
                else if (attr.Scope == AttributeScope.Client &&
                         string.Equals(attr.Key, "device_key", StringComparison.OrdinalIgnoreCase) &&
                         string.IsNullOrWhiteSpace(storedDeviceKey) &&
                         !string.IsNullOrWhiteSpace(attr.Value))
                {
                    storedDeviceKey = attr.Value.Trim().Trim('"');
                }
                else if (attr.Scope == AttributeScope.Client &&
                         string.Equals(attr.Key, "local_ip", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(attr.Value))
                {
                    localIp = attr.Value.Trim().Trim('"');
                }
            }
        }

        if (string.IsNullOrWhiteSpace(localIp) && peerEndpointRegistry is not null)
        {
            localIp = peerEndpointRegistry.GetPeerIpByChipId(chipId);
        }

        string? effectiveDeviceKey = !string.IsNullOrWhiteSpace(storedDeviceKey)
            ? storedDeviceKey
            : TryDeriveDefaultEsp32DeviceKey(chipId);

        string? unlinkProof = ComputeUnlinkProofHex(effectiveDeviceKey, chipId);

        string? activeClientId = mqttClientMap is not null
            ? await mqttClientMap.GetClientIdAsync(chipId, cancellationToken)
            : null;
        bool isOnlineOnMqtt = !string.IsNullOrWhiteSpace(activeClientId) ||
                              (mqttClientMap is null && device.OperationalStatus == DeviceOperationalStatus.Online);

        bool dispatchedViaMqtt = false;
        if (mqttPublisher is not null)
        {
            if (isOnlineOnMqtt && !string.IsNullOrWhiteSpace(unlinkProof))
            {
                string requestId = Guid.NewGuid().ToString("N")[..16];
                string topic = $"devices/{chipId}/unlink/command";
                string payload = JsonSerializer.Serialize(new
                {
                    action = "unlink_account",
                    request_id = requestId,
                    device_id = chipId,
                    reason = "ACCOUNT_UNLINKED",
                    unlink_proof = unlinkProof,
                    timestamp = utcNow.ToString("O")
                });

                await mqttPublisher.PublishAsync(
                    topic,
                    payload,
                    new MqttPublishOptions
                    {
                        Qos = 1,
                        Retain = false,
                        MessageExpiryInterval = 60,
                        ContentType = "application/json",
                        CorrelationData = Encoding.UTF8.GetBytes(requestId),
                        UserProperties = new Dictionary<string, string>
                        {
                            ["action"] = "unlink_account",
                            ["request_id"] = requestId
                        }
                    },
                    cancellationToken);

                dispatchedViaMqtt = true;
            }

            // Limpia cualquier mensaje retained obsoleto en status/{chipId}.
            await mqttPublisher.PublishAsync(
                $"status/{chipId}",
                string.Empty,
                qos: 0,
                retain: true,
                cancellationToken);
        }

        bool tombstoneRecorded = false;
        if (unlinkedDeviceRegistry is not null && !string.IsNullOrWhiteSpace(unlinkProof))
        {
            await unlinkedDeviceRegistry.RecordUnlinkedAsync(
                new UnlinkedDeviceTombstone(
                    ChipId: chipId,
                    TenantId: tenantId,
                    MqttApiKeyHash: mqttApiKeyHash,
                    UnlinkProof: unlinkProof,
                    UnlinkedAt: utcNow),
                cancellationToken);
            tombstoneRecorded = true;
        }

        deviceRepository.Delete(device);

        if (auditLogRepository is not null)
        {
            await auditLogRepository.AddAsync(
                new AuditLog(
                    Guid.NewGuid(),
                    tenantId,
                    request.PerformedByUserId,
                    string.IsNullOrWhiteSpace(request.PerformedByUsername) ? "usuario" : request.PerformedByUsername!,
                    "DeviceUnlinked",
                    $"Dispositivo '{deviceName}' ({deviceId}, ChipId={chipId}) desvinculado de la cuenta (mqttDispatch={dispatchedViaMqtt}, offlineTombstone={tombstoneRecorded}).",
                    ipAddress: request.CallerIpAddress,
                    userAgent: request.UserAgent),
                cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (sessionRevoker is not null)
        {
            if (dispatchedViaMqtt && allowTransportFlushDelay)
            {
                await Task.Delay(120, cancellationToken);
            }

            string disconnectReason = !string.IsNullOrWhiteSpace(unlinkProof)
                ? $"ACCOUNT_UNLINKED:{unlinkProof}"
                : "el dispositivo fue desvinculado de la cuenta";

            await sessionRevoker.RevokeSessionAsync(chipId, disconnectReason, cancellationToken);
        }

        if (notificationService is not null)
        {
            await notificationService.NotifyDeviceStatusChangedAsync(
                tenantId,
                deviceId,
                chipId,
                "Unlinked",
                utcNow,
                cancellationToken);
        }

        return Result<DeviceUnlinkResultDto>.Success(new DeviceUnlinkResultDto(
            DeviceId: deviceId,
            ChipId: chipId,
            Unlinked: true,
            FactoryResetDispatchedViaMqtt: dispatchedViaMqtt,
            PendingOfflineUnlinkTombstoneRecorded: tombstoneRecorded,
            UnlinkProof: unlinkProof,
            LocalIp: localIp,
            UnlinkedAt: utcNow));
    }

    public static string? ComputeUnlinkProofHex(string? deviceKey, string? chipId)
    {
        if (string.IsNullOrWhiteSpace(deviceKey) || string.IsNullOrWhiteSpace(chipId))
        {
            return null;
        }

        byte[] input = Encoding.UTF8.GetBytes($"{deviceKey.Trim()}:UNLINK:{chipId.Trim()}");
        byte[] hash = SHA256.HashData(input);
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    public static string? TryDeriveDefaultEsp32DeviceKey(string? chipId)
    {
        if (string.IsNullOrWhiteSpace(chipId))
        {
            return null;
        }

        string trimmed = chipId.Trim();
        if (trimmed.Length != 12)
        {
            return null;
        }

        for (int i = 0; i < 12; i++)
        {
            if (!Uri.IsHexDigit(trimmed[i]))
            {
                return null;
            }
        }

        ushort hi16 = Convert.ToUInt16(trimmed.Substring(0, 4), 16);
        uint lo32 = Convert.ToUInt32(trimmed.Substring(4, 8), 16);
        ulong mac = ((ulong)hi16 << 32) | lo32;

        return $"{(uint)(mac & 0xFFFFFFFFUL):X8}-{(ushort)((mac >> 32) & 0xFFFFUL):X4}-4{(ushort)((mac >> 16) & 0x0FFFUL):X3}-{(ushort)(((mac >> 48) & 0x3FFFUL) | 0x8000UL):X4}-{(mac ^ 0x5A5A5A5A5A5AUL):X12}";
    }
}

