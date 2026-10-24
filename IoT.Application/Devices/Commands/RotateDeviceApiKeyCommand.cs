using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Dtos;
using IoT.Application.Devices.Queries;
using IoT.Domain.Common;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Commands;

/// <summary>
/// Genera (o rota) la ApiKey MQTT de un dispositivo. Devuelve el texto plano UNA sola
/// vez; a partir de entonces sólo se expone la huella. Rotar invalida la clave anterior.
/// </summary>
public sealed record RotateDeviceApiKeyCommand(Guid TenantId, Guid DeviceId, string? IssuedBy)
    : IRequest<Result<DeviceCredentialsDto>>;

public sealed class RotateDeviceApiKeyCommandHandler : IRequestHandler<RotateDeviceApiKeyCommand, Result<DeviceCredentialsDto>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDeviceApiKeyService _apiKeyService;
    private readonly IMqttConnectionInfoProvider _connectionInfo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMqttSessionRevoker? _sessionRevoker;

    public RotateDeviceApiKeyCommandHandler(
        IDeviceRepository deviceRepository,
        IDeviceApiKeyService apiKeyService,
        IMqttConnectionInfoProvider connectionInfo,
        IUnitOfWork unitOfWork,
        IMqttSessionRevoker? sessionRevoker = null)
    {
        _deviceRepository = deviceRepository;
        _apiKeyService = apiKeyService;
        _connectionInfo = connectionInfo;
        _unitOfWork = unitOfWork;
        _sessionRevoker = sessionRevoker;
    }

    public async Task<Result<DeviceCredentialsDto>> Handle(RotateDeviceApiKeyCommand request, CancellationToken cancellationToken)
    {
        var device = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (device is null || device.TenantId != request.TenantId)
        {
            return Result<DeviceCredentialsDto>.Failure(
                new Error("Device.NotFound", $"Device '{request.DeviceId}' was not found."));
        }

        var (plainKey, hash, fingerprint) = _apiKeyService.Generate();
        var setResult = device.SetMqttApiKey(hash, fingerprint, request.IssuedBy);
        if (setResult.IsFailure)
        {
            return Result<DeviceCredentialsDto>.Failure(setResult.Error);
        }

        _deviceRepository.Update(device);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Rotar la clave tiene que cerrar la sesión abierta: si no, la credencial vieja
        // seguía dando acceso mientras el equipo no reconectara —días, en un tracker con
        // keepalive largo—. Va DESPUÉS de guardar, para que el equipo no pueda volver a
        // entrar con la clave anterior. Si falla no se deshace la rotación: la re-validación
        // periódica del broker cierra la sesión en el siguiente ciclo.
        if (_sessionRevoker is not null)
        {
            await _sessionRevoker.RevokeSessionAsync(
                device.ChipId, "la ApiKey MQTT del dispositivo se rotó", cancellationToken);
        }

        return Result<DeviceCredentialsDto>.Success(
            DeviceCredentialsMapper.ToDto(device, _connectionInfo, plaintextKey: plainKey));
    }
}
