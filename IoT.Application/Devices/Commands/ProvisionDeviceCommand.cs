using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Dtos;
using IoT.Application.Devices.Queries;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Commands;

/// <summary>
/// Aprovisionamiento en UN solo paso (emparejamiento rápido): registra —o reutiliza— un
/// dispositivo por su ChipId dentro del tenant del usuario autenticado, lo deja asignado a un
/// nodo <see cref="Customer"/>, acuña su ApiKey MQTT por-dispositivo y devuelve el PAQUETE de
/// conexión que la app inyecta en el equipo por BLE/SoftAP: broker host/puerto/TLS,
/// clientId = ChipId, cuenta = ApiKey en texto plano (visible UNA sola vez) y prefijo de tópicos.
///
/// No introduce credenciales, estados ni rutas de adopción nuevas: COMPONE piezas ya decididas
/// e implementadas —pre-registro (ADR-013; la ingesta nunca auto-crea equipos), credencial
/// por-dispositivo revocable (Decisión #9) y binding a Customer al crear (ADR-013 E8.4a)— que
/// hoy exigen 2-3 llamadas (POST /devices + rotate + opcional /customer) y que el texto plano
/// sólo salga de rotate.
///
/// La "cuenta" que inyecta el firmware ES esta ApiKey: el broker la verifica contra
/// Device.MqttApiKeyHash y exige clientId == ChipId y AdminStatus == Active, aceptando la clave
/// en el username o el password (password vacío admitido). bind_token/device_key/ota_key del
/// firmware viven sólo en el equipo (BLE/LAN) y NUNCA llegan al broker, así que no se modelan
/// aquí: la posesión de esta ApiKey, acuñada durante un claim autenticado, es la prueba del
/// vínculo. Es genérico para cualquier ESP32 con el mismo contrato (username=cuenta,
/// clientId=ChipId): no hay ramas por firmware.
///
/// Reutilizar un ChipId ya presente EN ESTE tenant re-empareja (rota la ApiKey y cierra la
/// sesión viva); un ChipId registrado en OTRO tenant se rechaza con el mismo error no-filtrante
/// del alta normal (unicidad global de IMEI/ChipId).
/// </summary>
public sealed record ProvisionDeviceCommand(
    Guid TenantId,
    string ChipId,
    string? MacAddress,
    string? Name,
    string? Model,
    string? Manufacturer,
    IReadOnlyList<CapabilityType>? Capabilities,
    string? IssuedBy) : IRequest<Result<DeviceProvisioningResultDto>>;

public sealed class ProvisionDeviceCommandHandler
    : IRequestHandler<ProvisionDeviceCommand, Result<DeviceProvisioningResultDto>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly IDeviceApiKeyService _apiKeyService;
    private readonly IMqttConnectionInfoProvider _connectionInfo;
    private readonly ITenantContext _tenantContext;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMqttSessionRevoker? _sessionRevoker;

    public ProvisionDeviceCommandHandler(
        IDeviceRepository deviceRepository,
        ICustomerRepository customerRepository,
        IDeviceApiKeyService apiKeyService,
        IMqttConnectionInfoProvider connectionInfo,
        ITenantContext tenantContext,
        IUnitOfWork unitOfWork,
        IMqttSessionRevoker? sessionRevoker = null)
    {
        _deviceRepository = deviceRepository;
        _customerRepository = customerRepository;
        _apiKeyService = apiKeyService;
        _connectionInfo = connectionInfo;
        _tenantContext = tenantContext;
        _unitOfWork = unitOfWork;
        _sessionRevoker = sessionRevoker;
    }

    public async Task<Result<DeviceProvisioningResultDto>> Handle(ProvisionDeviceCommand request, CancellationToken cancellationToken)
    {
        var chipId = request.ChipId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(chipId))
        {
            return Result<DeviceProvisioningResultDto>.Failure(
                new Error("Device.InvalidChipId", "ChipId is required."));
        }

        // ¿Ya existe EN ESTE tenant? -> re-emparejamiento: se rota la ApiKey y se vuelve a
        // entregar el paquete. Cubre el equipo reseteado de fábrica que vuelve a parear y la
        // app que perdió la clave anterior (el texto plano sólo se muestra una vez).
        var existing = await _deviceRepository.GetByChipIdAsync(request.TenantId, chipId, cancellationToken);
        if (existing is not null)
        {
            AssignCapabilities(existing, request.Capabilities);

            var (rePlainKey, reError) = MintApiKey(existing, request.IssuedBy);
            if (reError is not null)
            {
                return Result<DeviceProvisioningResultDto>.Failure(reError);
            }

            _deviceRepository.Update(existing);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Rotar la clave debe cerrar la sesión viva: si no, la credencial anterior seguiría
            // válida mientras el equipo no reconectara. Igual que RotateDeviceApiKeyCommand.
            if (_sessionRevoker is not null)
            {
                await _sessionRevoker.RevokeSessionAsync(
                    existing.ChipId, "la ApiKey MQTT del dispositivo se rotó al re-emparejar", cancellationToken);
            }

            return Result<DeviceProvisioningResultDto>.Success(new DeviceProvisioningResultDto(
                Created: false,
                Credentials: DeviceCredentialsMapper.ToDto(existing, _connectionInfo, rePlainKey)));
        }

        // Unicidad GLOBAL del ChipId/IMEI: un equipo físico vive en un solo tenant. Mismo
        // chequeo y mismo error no-filtrante que CreateDeviceCommand (impide reclamar el equipo
        // de otro tenant y evita un oráculo de existencia cross-tenant).
        if (await _deviceRepository.ExistsByChipIdGlobalAsync(chipId, cancellationToken))
        {
            return Result<DeviceProvisioningResultDto>.Failure(
                new Error("Device.ChipIdTaken", $"ChipId '{chipId}' ya está registrado en la plataforma."));
        }

        // El alta por BLE/SoftAP sólo descubre el ChipId (MAC eFuse); MacAddress y Name son
        // obligatorios en Device.Create, así que se completan con valores derivados del ChipId
        // cuando la app no los manda (Model/Manufacturer ya tienen defaults en el dominio).
        var createResult = Device.Create(
            request.TenantId,
            chipId,
            string.IsNullOrWhiteSpace(request.MacAddress) ? chipId : request.MacAddress!,
            string.IsNullOrWhiteSpace(request.Name) ? $"Dispositivo {chipId}" : request.Name!,
            request.Model ?? string.Empty,
            request.Manufacturer ?? string.Empty);
        if (createResult.IsFailure)
        {
            return Result<DeviceProvisioningResultDto>.Failure(createResult.Error);
        }

        var device = createResult.Value;

        // Mismo binding a Customer que el alta normal (ADR-013 E8.4a): nodo de la sesión si está
        // acotada; si no, el Customer RAÍZ del tenant. Nunca queda CustomerId nulo (NOT NULL + RLS).
        var customerId = _tenantContext.CustomerId
            ?? await _customerRepository.GetRootCustomerIdAsync(request.TenantId, cancellationToken);
        if (customerId is null || customerId.Value == Guid.Empty)
        {
            return Result<DeviceProvisioningResultDto>.Failure(new Error(
                "Device.NoCustomerNode",
                "El tenant no tiene un nodo Customer raíz al que asignar el dispositivo."));
        }
        device.AssignToCustomer(customerId.Value);

        AssignCapabilities(device, request.Capabilities);

        var (plainKey, error) = MintApiKey(device, request.IssuedBy);
        if (error is not null)
        {
            return Result<DeviceProvisioningResultDto>.Failure(error);
        }

        await _deviceRepository.AddAsync(device, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<DeviceProvisioningResultDto>.Success(new DeviceProvisioningResultDto(
            Created: true,
            Credentials: DeviceCredentialsMapper.ToDto(device, _connectionInfo, plainKey)));
    }

    private static void AssignCapabilities(Device device, IReadOnlyList<CapabilityType>? capabilities)
    {
        if (capabilities is null)
        {
            return;
        }

        foreach (var capability in capabilities)
        {
            device.AssignCapability(capability);
        }
    }

    /// <summary>
    /// Genera una ApiKey por-dispositivo y fija su hash+huella en el equipo. Devuelve el texto
    /// plano (a inyectar como cuenta) o el error si el dominio rechaza la credencial.
    /// </summary>
    private (string PlainKey, Error? Error) MintApiKey(Device device, string? issuedBy)
    {
        var (plainKey, hash, fingerprint) = _apiKeyService.Generate();
        var setResult = device.SetMqttApiKey(hash, fingerprint, issuedBy);
        return setResult.IsFailure ? (string.Empty, setResult.Error) : (plainKey, null);
    }
}
