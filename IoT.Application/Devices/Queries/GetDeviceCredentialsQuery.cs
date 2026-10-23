using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Dtos;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Queries;

/// <summary>
/// Devuelve los datos de conexión MQTT del dispositivo y la huella de su ApiKey.
/// Nunca devuelve el texto plano de la clave (sólo se expone al rotarla).
/// </summary>
public sealed record GetDeviceCredentialsQuery(Guid TenantId, Guid DeviceId) : IRequest<Result<DeviceCredentialsDto>>;

public sealed class GetDeviceCredentialsQueryHandler : IRequestHandler<GetDeviceCredentialsQuery, Result<DeviceCredentialsDto>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IMqttConnectionInfoProvider _connectionInfo;

    public GetDeviceCredentialsQueryHandler(
        IDeviceRepository deviceRepository,
        IMqttConnectionInfoProvider connectionInfo)
    {
        _deviceRepository = deviceRepository;
        _connectionInfo = connectionInfo;
    }

    public async Task<Result<DeviceCredentialsDto>> Handle(GetDeviceCredentialsQuery request, CancellationToken cancellationToken)
    {
        var device = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (device is null || device.TenantId != request.TenantId)
        {
            return Result<DeviceCredentialsDto>.Failure(
                new Error("Device.NotFound", $"Device '{request.DeviceId}' was not found."));
        }

        return Result<DeviceCredentialsDto>.Success(
            DeviceCredentialsMapper.ToDto(device, _connectionInfo, plaintextKey: null));
    }
}

/// <summary>Construye el DTO de credenciales a partir del dispositivo y los datos de conexión.</summary>
internal static class DeviceCredentialsMapper
{
    public static DeviceCredentialsDto ToDto(Device device, IMqttConnectionInfoProvider conn, string? plaintextKey)
    {
        return new DeviceCredentialsDto(
            DeviceId: device.Id,
            ChipId: device.ChipId,
            // El dispositivo se identifica por su ChipId; la ApiKey es la "contraseña".
            MqttUsername: device.ChipId,
            MqttPasswordPlaintext: plaintextKey,
            MqttPasswordFingerprint: device.MqttApiKeyFingerprint,
            MqttTopicPrefix: $"devices/{device.ChipId}",
            BrokerHost: conn.PublicHost,
            BrokerPort: conn.Port,
            BrokerTls: conn.Tls,
            IssuedAt: device.MqttApiKeyIssuedAt ?? device.CreatedAt,
            IssuedBy: device.MqttApiKeyIssuedBy);
    }
}
