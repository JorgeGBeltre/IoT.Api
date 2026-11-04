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
/// Aprovisionamiento multi-modo en UN solo paso (BLE GATT Schema A/B, Compatible SoftAP HTTP,
/// Red Local LAN :8081 /zeroconf/*, Cable USB Web Serial AT+ y Reclamación por BindToken):
/// registra —o reutiliza— un dispositivo por su ChipId dentro del tenant del usuario autenticado,
/// lo deja asignado a un nodo <see cref="Customer"/>, acuña su ApiKey MQTT por-dispositivo,
/// persiste los atributos iniciales (<c>device_key</c>, <c>bind_token</c> y configuración
/// compartida del equipo) y devuelve el PAQUETE de conexión y las tramas listas para cada
/// transporte de adopción.
/// </summary>
public sealed record ProvisionDeviceCommand(
    Guid TenantId,
    string ChipId,
    string? MacAddress,
    string? Name,
    string? Model,
    string? Manufacturer,
    IReadOnlyList<CapabilityType>? Capabilities,
    string? IssuedBy,
    string? FirmwareVersion = null,
    string? PairingMode = null,
    string? DeviceKey = null,
    string? BindToken = null,
    string? WifiSsid = null,
    string? WifiPassword = null,
    IReadOnlyDictionary<string, string?>? DeviceParameters = null) : IRequest<Result<DeviceProvisioningResultDto>>;

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
    private readonly IDeviceAttributeRepository? _attributeRepository;
    private readonly ISharedAttributePushDispatcher? _pushDispatcher;

    public ProvisionDeviceCommandHandler(
        IDeviceRepository deviceRepository,
        ICustomerRepository customerRepository,
        IDeviceApiKeyService apiKeyService,
        IMqttConnectionInfoProvider connectionInfo,
        ITenantContext tenantContext,
        IUnitOfWork unitOfWork,
        IMqttSessionRevoker? sessionRevoker = null,
        IDeviceAttributeRepository? attributeRepository = null,
        ISharedAttributePushDispatcher? pushDispatcher = null)
    {
        _deviceRepository = deviceRepository;
        _customerRepository = customerRepository;
        _apiKeyService = apiKeyService;
        _connectionInfo = connectionInfo;
        _tenantContext = tenantContext;
        _unitOfWork = unitOfWork;
        _sessionRevoker = sessionRevoker;
        _attributeRepository = attributeRepository;
        _pushDispatcher = pushDispatcher;
    }

    public async Task<Result<DeviceProvisioningResultDto>> Handle(ProvisionDeviceCommand request, CancellationToken cancellationToken)
    {
        var chipId = request.ChipId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(chipId))
        {
            return Result<DeviceProvisioningResultDto>.Failure(
                new Error("Device.InvalidChipId", "ChipId is required."));
        }

        string resolvedDeviceKey = NormalizeDeviceKey(request.DeviceKey);
        string resolvedBindToken = NormalizeBindToken(request.BindToken);
        string resolvedPairingMode = string.IsNullOrWhiteSpace(request.PairingMode)
            ? "ble"
            : request.PairingMode.Trim().ToLowerInvariant();

        // ¿Ya existe EN ESTE tenant? -> re-emparejamiento: se rota la ApiKey y se vuelve a
        // entregar el paquete. Cubre el equipo reseteado de fábrica que vuelve a parear y la
        // app que perdió la clave anterior (el texto plano sólo se muestra una vez).
        var existing = await _deviceRepository.GetByChipIdAsync(request.TenantId, chipId, cancellationToken);
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                var renameRes = existing.Rename(request.Name);
                if (renameRes.IsFailure)
                {
                    return Result<DeviceProvisioningResultDto>.Failure(renameRes.Error);
                }
            }

            existing.UpdateHardwareInfo(request.Model, request.Manufacturer);
            ApplyCapabilities(existing, request.Capabilities, request.Model ?? existing.Model, chipId);

            if (!string.IsNullOrWhiteSpace(request.FirmwareVersion))
            {
                existing.UpdateFirmwareVersion(request.FirmwareVersion);
            }

            var (rePlainKey, reError) = MintApiKey(existing, request.IssuedBy);
            if (reError is not null)
            {
                return Result<DeviceProvisioningResultDto>.Failure(reError);
            }

            _deviceRepository.Update(existing);
            await PersistProvisioningAttributesAsync(
                existing,
                resolvedDeviceKey,
                resolvedBindToken,
                resolvedPairingMode,
                request.DeviceParameters,
                cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Rotar la clave debe cerrar la sesión viva: si no, la credencial anterior seguiría
            // válida mientras el equipo no reconectara. Igual que RotateDeviceApiKeyCommand.
            if (_sessionRevoker is not null)
            {
                await _sessionRevoker.RevokeSessionAsync(
                    existing.ChipId, "la ApiKey MQTT del dispositivo se rotó al re-emparejar", cancellationToken);
            }

            await PushSharedAttributesBestEffortAsync(existing, request.DeviceParameters, cancellationToken);

            var reCreds = DeviceCredentialsMapper.ToDto(existing, _connectionInfo, rePlainKey);
            var reBundles = DeviceProvisioningBundleBuilder.Build(
                reCreds,
                resolvedDeviceKey,
                resolvedBindToken,
                request.WifiSsid,
                request.WifiPassword,
                request.DeviceParameters);

            return Result<DeviceProvisioningResultDto>.Success(new DeviceProvisioningResultDto(
                Created: false,
                Credentials: reCreds,
                DeviceKey: resolvedDeviceKey,
                BindToken: resolvedBindToken,
                PairingMode: resolvedPairingMode,
                Bundles: reBundles));
        }

        // Unicidad GLOBAL del ChipId/IMEI: un equipo físico vive en un solo tenant. Mismo
        // chequeo y mismo error no-filtrante que CreateDeviceCommand (impide reclamar el equipo
        // de otro tenant y evita un oráculo de existencia cross-tenant).
        if (await _deviceRepository.ExistsByChipIdGlobalAsync(chipId, cancellationToken))
        {
            return Result<DeviceProvisioningResultDto>.Failure(
                new Error("Device.ChipIdTaken", $"ChipId '{chipId}' ya está registrado en la plataforma."));
        }

        // El alta por BLE/SoftAP/LAN/Serial puede descubrir sólo el ChipId; MacAddress y Name son
        // obligatorios en Device.Create, así que se completan con valores derivados del ChipId
        // cuando la app no los manda (Model/Manufacturer ya tienen defaults en el dominio).
        var createResult = Device.Create(
            request.TenantId,
            chipId,
            string.IsNullOrWhiteSpace(request.MacAddress) ? chipId : request.MacAddress!,
            string.IsNullOrWhiteSpace(request.Name) ? $"Dispositivo {chipId}" : request.Name!,
            request.Model ?? string.Empty,
            request.Manufacturer ?? string.Empty,
            request.FirmwareVersion);
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

        ApplyCapabilities(device, request.Capabilities, request.Model ?? device.Model, chipId);

        var (plainKey, error) = MintApiKey(device, request.IssuedBy);
        if (error is not null)
        {
            return Result<DeviceProvisioningResultDto>.Failure(error);
        }

        await _deviceRepository.AddAsync(device, cancellationToken);
        await PersistProvisioningAttributesAsync(
            device,
            resolvedDeviceKey,
            resolvedBindToken,
            resolvedPairingMode,
            request.DeviceParameters,
            cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await PushSharedAttributesBestEffortAsync(device, request.DeviceParameters, cancellationToken);

        var creds = DeviceCredentialsMapper.ToDto(device, _connectionInfo, plainKey);
        var bundles = DeviceProvisioningBundleBuilder.Build(
            creds,
            resolvedDeviceKey,
            resolvedBindToken,
            request.WifiSsid,
            request.WifiPassword,
            request.DeviceParameters);

        return Result<DeviceProvisioningResultDto>.Success(new DeviceProvisioningResultDto(
            Created: true,
            Credentials: creds,
            DeviceKey: resolvedDeviceKey,
            BindToken: resolvedBindToken,
            PairingMode: resolvedPairingMode,
            Bundles: bundles));
    }

    private async Task PersistProvisioningAttributesAsync(
        Device device,
        string deviceKey,
        string bindToken,
        string pairingMode,
        IReadOnlyDictionary<string, string?>? deviceParameters,
        CancellationToken cancellationToken)
    {
        if (_attributeRepository is null)
            return;

        await _attributeRepository.UpsertAsync(
            device.TenantId, device.Id, AttributeScope.Server, "device_key", deviceKey, cancellationToken);
        await _attributeRepository.UpsertAsync(
            device.TenantId, device.Id, AttributeScope.Server, "bind_token", bindToken, cancellationToken);
        await _attributeRepository.UpsertAsync(
            device.TenantId, device.Id, AttributeScope.Server, "pairing_mode", pairingMode, cancellationToken);

        if (deviceParameters is null)
            return;

        foreach (var (key, value) in deviceParameters)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            string trimmedKey = key.Trim();
            if (string.Equals(trimmedKey, "ota_hmac_key", StringComparison.OrdinalIgnoreCase))
            {
                await _attributeRepository.UpsertAsync(
                    device.TenantId, device.Id, AttributeScope.Server, "ota_hmac_key", value.Trim(), cancellationToken);
                continue;
            }

            if (string.Equals(trimmedKey, "password", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmedKey, "pass", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmedKey, "wifi_password", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await _attributeRepository.UpsertAsync(
                device.TenantId, device.Id, AttributeScope.Shared, trimmedKey, value.Trim(), cancellationToken);
        }
    }

    private async Task PushSharedAttributesBestEffortAsync(
        Device device,
        IReadOnlyDictionary<string, string?>? deviceParameters,
        CancellationToken cancellationToken)
    {
        if (_pushDispatcher is null || deviceParameters is null || deviceParameters.Count == 0)
            return;

        var sharedOnly = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in deviceParameters)
        {
            if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(v))
                continue;
            if (string.Equals(k, "ota_hmac_key", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "password", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "pass", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            sharedOnly[k.Trim()] = v.Trim();
        }

        if (sharedOnly.Count == 0)
            return;

        try
        {
            await _pushDispatcher.PushSharedAttributesAsync(device, sharedOnly, cancellationToken);
        }
        catch
        {
            // Despacho best-effort: el equipo puede estar aún en proceso de reinicio tras el emparejamiento.
        }
    }

    private static string NormalizeDeviceKey(string? rawDeviceKey)
    {
        if (!string.IsNullOrWhiteSpace(rawDeviceKey) &&
            !string.Equals(rawDeviceKey.Trim(), "00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase))
        {
            return rawDeviceKey.Trim();
        }
        return Guid.NewGuid().ToString("D");
    }

    private static string NormalizeBindToken(string? rawBindToken)
    {
        if (!string.IsNullOrWhiteSpace(rawBindToken))
        {
            return rawBindToken.Trim();
        }
        return $"bt_{Guid.NewGuid():N}"[..19];
    }

    internal static void ApplyCapabilities(
        Device device,
        IReadOnlyList<CapabilityType>? capabilities,
        string? model,
        string? chipId = null)
    {
        IReadOnlyList<CapabilityType> resolved = capabilities is { Count: > 0 }
            ? capabilities
            : InferCapabilitiesFromMetadata(model, chipId);

        if (resolved.Count == 0)
        {
            return;
        }

        foreach (var capability in resolved)
        {
            device.AssignCapability(capability);
        }

        // Si se re-aprovisiona un interruptor previamente clasificado por error como GPS (o viceversa),
        // se limpia la capacidad contradictoria que no forma parte del conjunto resuelto.
        if (resolved.Contains(CapabilityType.Switch) &&
            !resolved.Contains(CapabilityType.Gps) &&
            device.HasCapability(CapabilityType.Gps))
        {
            device.RemoveCapability(CapabilityType.Gps);
        }
        else if (resolved.Contains(CapabilityType.Gps) &&
                 !resolved.Contains(CapabilityType.Switch) &&
                 device.HasCapability(CapabilityType.Switch))
        {
            device.RemoveCapability(CapabilityType.Switch);
        }
    }

    internal static IReadOnlyList<CapabilityType> InferCapabilitiesFromMetadata(string? model, string? chipId = null)
    {
        string m = (model ?? string.Empty).Trim().ToLowerInvariant();
        string c = (chipId ?? string.Empty).Trim().ToUpperInvariant();

        if (m.Contains("gps-relay", StringComparison.Ordinal) ||
            (m.Contains("gps", StringComparison.Ordinal) && m.Contains("relay", StringComparison.Ordinal)))
        {
            return [CapabilityType.Gps, CapabilityType.Switch];
        }

        if (m.Contains("relay", StringComparison.Ordinal) ||
            m.Contains("switch", StringComparison.Ordinal) ||
            m.Contains("interruptor", StringComparison.Ordinal) ||
            m.Contains("psc-", StringComparison.Ordinal) ||
            m.Contains("vesis", StringComparison.Ordinal) ||
            m.Contains("plug", StringComparison.Ordinal) ||
            m.Contains("minir", StringComparison.Ordinal) ||
            c.StartsWith("RELAY-", StringComparison.Ordinal) ||
            c.StartsWith("SWITCH-", StringComparison.Ordinal) ||
            c.StartsWith("VESIS-", StringComparison.Ordinal))
        {
            return [CapabilityType.Switch];
        }

        if (m.Contains("glp", StringComparison.Ordinal) ||
            m.Contains("pressure", StringComparison.Ordinal) ||
            m.Contains("level", StringComparison.Ordinal) ||
            m.Contains("tank", StringComparison.Ordinal) ||
            c.StartsWith("GLP-", StringComparison.Ordinal))
        {
            return [CapabilityType.LevelTelemetry, CapabilityType.Pressure];
        }

        if (m.Contains("validator", StringComparison.Ordinal) ||
            m.Contains("fligo", StringComparison.Ordinal) ||
            m.Contains("toour", StringComparison.Ordinal) ||
            m.Contains("ticket", StringComparison.Ordinal) ||
            m.Contains("nfc", StringComparison.Ordinal) ||
            c.StartsWith("FLIGO-", StringComparison.Ordinal) ||
            c.StartsWith("TOOUR-", StringComparison.Ordinal))
        {
            return [CapabilityType.NfcTicket, CapabilityType.QrReader];
        }

        if (m.Contains("gps", StringComparison.Ordinal) ||
            m.Contains("gv50", StringComparison.Ordinal) ||
            m.Contains("tracker", StringComparison.Ordinal) ||
            m.Contains("rastreador", StringComparison.Ordinal) ||
            m.Contains("teltonika", StringComparison.Ordinal) ||
            m.Contains("gt06", StringComparison.Ordinal) ||
            m.Contains("coban", StringComparison.Ordinal) ||
            m.Contains("mictrack", StringComparison.Ordinal) ||
            m.Contains("queclink", StringComparison.Ordinal) ||
            m.Contains("istartek", StringComparison.Ordinal) ||
            m.Contains("jointech", StringComparison.Ordinal) ||
            c.StartsWith("GPS-", StringComparison.Ordinal))
        {
            return [CapabilityType.Gps];
        }

        return Array.Empty<CapabilityType>();
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
