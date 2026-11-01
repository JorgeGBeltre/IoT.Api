using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Commands;

/// <summary>
/// Importa (fija) como credencial per-device una ApiKey MQTT YA conocida y gestionada fuera de
/// la plataforma, en lugar de acuñar una aleatoria como <see cref="RotateDeviceApiKeyCommand"/>.
///
/// Caso de uso: flotas cuyo firmware trae una clave de fábrica compartida y conecta con ella
/// (ClientId=ChipId, usuario=clave). Registrando cada ChipId con esa clave, el equipo autentica
/// por la vía PER-DEVICE del broker (ChipId existe + la clave verifica contra su hash +
/// ClientId==ChipId) SIN tocar el firmware, y además queda habilitada la bajada (puede
/// suscribirse a su propio canal de comandos). El tenant se resuelve device→tenant en la
/// ingesta, así que varias flotas con la MISMA clave pueden vivir en tenants distintos.
///
/// Devuelve SÓLO la huella de la clave (para confirmar la versión fijada); nunca el texto plano.
/// La garantía es menor que la de una clave acuñada aleatoria: el secreto lo gestiona el
/// operador y suele ser compartido, por lo que quien lo posea podría publicar suplantando
/// cualquier ChipId REGISTRADO del tenant (el pre-registro y el aislamiento por tenant lo acotan).
/// </summary>
public sealed record ImportDeviceApiKeyCommand(Guid TenantId, Guid DeviceId, string KnownApiKey, string? IssuedBy)
    : IRequest<Result<string>>;

public sealed class ImportDeviceApiKeyCommandHandler : IRequestHandler<ImportDeviceApiKeyCommand, Result<string>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDeviceApiKeyService _apiKeyService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMqttSessionRevoker? _sessionRevoker;

    public ImportDeviceApiKeyCommandHandler(
        IDeviceRepository deviceRepository,
        IDeviceApiKeyService apiKeyService,
        IUnitOfWork unitOfWork,
        IMqttSessionRevoker? sessionRevoker = null)
    {
        _deviceRepository = deviceRepository;
        _apiKeyService = apiKeyService;
        _unitOfWork = unitOfWork;
        _sessionRevoker = sessionRevoker;
    }

    public async Task<Result<string>> Handle(ImportDeviceApiKeyCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.KnownApiKey))
        {
            return Result<string>.Failure(new Error("Device.InvalidApiKey", "La ApiKey a importar es obligatoria."));
        }

        var device = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (device is null || device.TenantId != request.TenantId)
        {
            return Result<string>.Failure(new Error("Device.NotFound", $"Device '{request.DeviceId}' was not found."));
        }

        // Se recorta para limpiar artefactos de copiado/pegado; el equipo presenta la clave
        // literal compilada (sin espacios), así que el hash almacenado debe ser el de la clave limpia.
        var (hash, fingerprint) = _apiKeyService.DeriveFromKnownKey(request.KnownApiKey.Trim());
        var setResult = device.SetMqttApiKey(hash, fingerprint, request.IssuedBy);
        if (setResult.IsFailure)
        {
            return Result<string>.Failure(setResult.Error);
        }

        _deviceRepository.Update(device);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Cambiar la credencial cierra cualquier sesión viva con la clave anterior, igual que la
        // rotación. Va DESPUÉS de guardar; si falla, la re-validación periódica del broker la
        // cierra en el siguiente ciclo.
        if (_sessionRevoker is not null)
        {
            await _sessionRevoker.RevokeSessionAsync(
                device.ChipId, "se importó una ApiKey MQTT conocida para el dispositivo", cancellationToken);
        }

        return Result<string>.Success(fingerprint);
    }
}
