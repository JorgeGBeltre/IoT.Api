using System.Net;
using System.Net.Sockets;
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

public sealed record ChangeDeviceWifiResultDto(
    Guid DeviceId,
    string ChipId,
    string TargetSsid,
    string RequestId,
    DateTime DispatchedAtUtc);

public sealed record ChangeDeviceWifiCommand(
    Guid TenantId,
    Guid DeviceId,
    string TargetSsid,
    string? TargetPassword,
    string? CallerIpAddress,
    string? ClientWifiSsid = null,
    string? LanProof = null,
    string? ClientPlatform = null,
    Guid? PerformedByUserId = null,
    string? PerformedByUsername = null,
    string? UserAgent = null) : IRequest<Result<ChangeDeviceWifiResultDto>>;

public sealed class ChangeDeviceWifiCommandHandler : IRequestHandler<ChangeDeviceWifiCommand, Result<ChangeDeviceWifiResultDto>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDeviceAttributeRepository _attributeRepository;
    private readonly IMqttPublisher _mqttPublisher;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMqttClientMap? _mqttClientMap;
    private readonly IMqttPeerEndpointRegistry? _peerEndpointRegistry;
    private readonly IAuditLogRepository? _auditLogRepository;

    public ChangeDeviceWifiCommandHandler(
        IDeviceRepository deviceRepository,
        IDeviceAttributeRepository attributeRepository,
        IMqttPublisher mqttPublisher,
        IUnitOfWork unitOfWork,
        IMqttClientMap? mqttClientMap = null,
        IMqttPeerEndpointRegistry? peerEndpointRegistry = null,
        IAuditLogRepository? auditLogRepository = null)
    {
        _deviceRepository = deviceRepository;
        _attributeRepository = attributeRepository;
        _mqttPublisher = mqttPublisher;
        _unitOfWork = unitOfWork;
        _mqttClientMap = mqttClientMap;
        _peerEndpointRegistry = peerEndpointRegistry;
        _auditLogRepository = auditLogRepository;
    }

    public async Task<Result<ChangeDeviceWifiResultDto>> Handle(ChangeDeviceWifiCommand request, CancellationToken cancellationToken)
    {
        var device = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (device is null || device.TenantId != request.TenantId)
        {
            return Result<ChangeDeviceWifiResultDto>.Failure(
                new Error("Device.NotFound", "No se encontró el dispositivo solicitado o no pertenece a su cuenta."));
        }

        string targetSsid = (request.TargetSsid ?? string.Empty).Trim();
        int ssidByteLen = Encoding.UTF8.GetByteCount(targetSsid);
        if (ssidByteLen is < 1 or > 32)
        {
            return Result<ChangeDeviceWifiResultDto>.Failure(
                new Error("Device.InvalidWifiSsid", "El nombre de la red Wi-Fi (SSID) debe tener entre 1 y 32 caracteres."));
        }

        string targetPassword = request.TargetPassword ?? string.Empty;
        int passByteLen = Encoding.UTF8.GetByteCount(targetPassword);
        if (passByteLen > 0 && passByteLen is < 8 or > 64)
        {
            return Result<ChangeDeviceWifiResultDto>.Failure(
                new Error("Device.InvalidWifiPassword", "La contraseña Wi-Fi debe estar vacía (red abierta) o tener entre 8 y 64 caracteres."));
        }

        // 1. Verificar sesión MQTT 5.0 activa del dispositivo.
        string? activeClientId = _mqttClientMap is not null
            ? await _mqttClientMap.GetClientIdAsync(device.ChipId, cancellationToken)
            : null;

        if (string.IsNullOrWhiteSpace(activeClientId) &&
            device.OperationalStatus != DeviceOperationalStatus.Online)
        {
            return Result<ChangeDeviceWifiResultDto>.Failure(
                new Error("Device.Offline", "El dispositivo debe estar en línea por MQTT 5.0 para cambiar su red Wi-Fi."));
        }

        // 2. Cargar identidad de red actual reportada por el dispositivo (wifi_ssid, local_ip) y device_key.
        var allAttrs = await _attributeRepository.ListByDeviceAsync(device.Id, cancellationToken);
        string? deviceWifiSsid = null;
        string? deviceLocalIp = null;
        string? deviceKey = null;

        foreach (var attr in allAttrs)
        {
            if (attr.Scope == AttributeScope.Client && string.Equals(attr.Key, "wifi_ssid", StringComparison.OrdinalIgnoreCase))
            {
                deviceWifiSsid = attr.Value?.Trim();
            }
            else if (attr.Scope == AttributeScope.Client && string.Equals(attr.Key, "local_ip", StringComparison.OrdinalIgnoreCase))
            {
                deviceLocalIp = attr.Value?.Trim();
            }
            else if (attr.Scope == AttributeScope.Server && string.Equals(attr.Key, "device_key", StringComparison.OrdinalIgnoreCase))
            {
                deviceKey = attr.Value?.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(deviceLocalIp) && _peerEndpointRegistry is not null)
        {
            deviceLocalIp = _peerEndpointRegistry.GetPeerIpByChipId(device.ChipId);
        }

        // 3. Guardia estricta de misma red Wi-Fi / misma subred LAN local (/24 RFC1918).
        var callerIpv4 = TryNormalizePrivateIpv4(request.CallerIpAddress);
        var deviceIpv4 = TryNormalizePrivateIpv4(deviceLocalIp);

        if (callerIpv4 is null || deviceIpv4 is null || !AreSameIpv4Subnet24(callerIpv4, deviceIpv4))
        {
            return Result<ChangeDeviceWifiResultDto>.Failure(
                new Error(
                    "Device.WifiNotSameNetwork",
                    "Operación bloqueada: su teléfono o PC no está conectado a la misma red Wi-Fi local que el dispositivo."));
        }

        string deviceIpString = deviceIpv4.ToString();
        string? expectedLanProof = ComputeLanProofHex(deviceKey, device.ChipId, deviceIpString);
        bool hasLanProof = !string.IsNullOrWhiteSpace(request.LanProof);
        bool validLanProof = false;

        if (hasLanProof)
        {
            if (string.IsNullOrWhiteSpace(expectedLanProof) ||
                !FixedTimeEqualsHex(request.LanProof!.Trim(), expectedLanProof))
            {
                return Result<ChangeDeviceWifiResultDto>.Failure(
                    new Error(
                        "Device.WifiNotSameNetwork",
                        "La verificación local del dispositivo falló: su teléfono o PC no se encuentra en la misma red Wi-Fi del equipo."));
            }
            validLanProof = true;
        }

        string? callerSsid = string.IsNullOrWhiteSpace(request.ClientWifiSsid)
            ? null
            : request.ClientWifiSsid.Trim();

        if (callerSsid is not null && !string.IsNullOrWhiteSpace(deviceWifiSsid))
        {
            if (!string.Equals(callerSsid, deviceWifiSsid, StringComparison.OrdinalIgnoreCase))
            {
                return Result<ChangeDeviceWifiResultDto>.Failure(
                    new Error(
                        "Device.WifiNotSameNetwork",
                        $"Operación bloqueada: su equipo está en la red '{callerSsid}', pero el dispositivo está conectado a '{deviceWifiSsid}'. Conéctese primero a '{deviceWifiSsid}'."));
            }
        }

        bool isMobileCaller = string.Equals(request.ClientPlatform, "mobile", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(deviceWifiSsid) && callerSsid is null && !validLanProof)
        {
            return Result<ChangeDeviceWifiResultDto>.Failure(
                new Error(
                    "Device.WifiNotSameNetwork",
                    $"No se pudo confirmar que su {(isMobileCaller ? "teléfono" : "PC")} esté en la misma red Wi-Fi ('{deviceWifiSsid}') que el dispositivo."));
        }

        if (isMobileCaller && callerSsid is null && !validLanProof)
        {
            return Result<ChangeDeviceWifiResultDto>.Failure(
                new Error(
                    "Device.WifiNotSameNetwork",
                    "Active el Wi-Fi en su teléfono y conéctese a la misma red local del dispositivo antes de cambiar sus credenciales."));
        }

        // 4. Publicar credenciales únicamente por el tópico descendente MQTT 5.0 (QoS 1, Retain = false, TTL = 60 s).
        string requestId = Guid.NewGuid().ToString("N")[..16];
        string topic = $"devices/{device.ChipId}/wifi/command";
        string payload = JsonSerializer.Serialize(new
        {
            action = "set_wifi",
            request_id = requestId,
            ssid = targetSsid,
            password = targetPassword
        });

        await _mqttPublisher.PublishAsync(
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
                    ["action"] = "set_wifi",
                    ["request_id"] = requestId
                }
            },
            cancellationToken);

        // 5. Registrar auditoría redactada (JAMAŚ almacenar ni loguear la contraseña Wi-Fi).
        if (_auditLogRepository is not null)
        {
            await _auditLogRepository.AddAsync(
                new AuditLog(
                    Guid.NewGuid(),
                    device.TenantId,
                    request.PerformedByUserId,
                    string.IsNullOrWhiteSpace(request.PerformedByUsername) ? "usuario" : request.PerformedByUsername!,
                    "DeviceWifiChangeRequested",
                    $"Cambio de red Wi-Fi por MQTT 5.0 enviado a '{device.Name}' ({device.ChipId}) hacia SSID '{targetSsid}' (credencial redactada).",
                    ipAddress: callerIpv4.ToString(),
                    userAgent: request.UserAgent),
                cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result<ChangeDeviceWifiResultDto>.Success(
            new ChangeDeviceWifiResultDto(
                device.Id,
                device.ChipId,
                targetSsid,
                requestId,
                DateTime.UtcNow));
    }

    public static string? ComputeLanProofHex(string? deviceKey, string? chipId, string? localIp)
    {
        if (string.IsNullOrWhiteSpace(deviceKey) ||
            string.IsNullOrWhiteSpace(chipId) ||
            string.IsNullOrWhiteSpace(localIp))
        {
            return null;
        }

        byte[] input = Encoding.UTF8.GetBytes($"{deviceKey.Trim()}:{chipId.Trim()}:{localIp.Trim()}");
        byte[] hash = SHA256.HashData(input);
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static bool FixedTimeEqualsHex(string candidate, string expected)
    {
        byte[] a = Encoding.ASCII.GetBytes(candidate.ToLowerInvariant());
        byte[] b = Encoding.ASCII.GetBytes(expected.ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static IPAddress? TryNormalizePrivateIpv4(string? rawIp)
    {
        if (string.IsNullOrWhiteSpace(rawIp))
        {
            return null;
        }

        string trimmed = rawIp.Trim();
        if (!IPAddress.TryParse(trimmed, out var ip))
        {
            return null;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        byte[] b = ip.GetAddressBytes();
        bool isRfc1918 =
            b[0] == 10 ||
            (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
            (b[0] == 192 && b[1] == 168);

        return isRfc1918 ? ip : null;
    }

    private static bool AreSameIpv4Subnet24(IPAddress a, IPAddress b)
    {
        byte[] ba = a.GetAddressBytes();
        byte[] bb = b.GetAddressBytes();
        return ba.Length == 4 &&
               bb.Length == 4 &&
               ba[0] == bb[0] &&
               ba[1] == bb[1] &&
               ba[2] == bb[2];
    }
}

