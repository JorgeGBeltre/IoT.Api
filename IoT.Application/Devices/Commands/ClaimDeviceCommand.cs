using System.Security.Cryptography;
using System.Text;
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
/// Reclamación / vinculación de dispositivo por clave de reclamación (<c>bind_token</c> o <c>device_key</c>),
/// inspirada en el flujo de Device Claiming de ThingsBoard.
/// <para>
/// Cubre la adopción desde PCs o laptops sin Bluetooth:
/// <list type="number">
///   <item><description>Cuando un equipo ya fue pre-aprovisionado o conectado por SmartConfig / Portal Cautivo con un <c>bind_token</c> o <c>device_key</c>, valida en tiempo constante que el secreto coincida, vincula el equipo al nodo <see cref="Customer"/> del operador, rota la ApiKey MQTT y devuelve el paquete completo.</description></item>
///   <item><description>Cuando el equipo aún no existe en el tenant (y no pertenece a otro tenant), lo da de alta vinculando el <c>claimSecret</c> como su <c>bind_token</c> oficial.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed record ClaimDeviceCommand(
    Guid TenantId,
    string ChipId,
    string ClaimSecret,
    string? Name = null,
    string? Model = null,
    string? Manufacturer = null,
    string? MacAddress = null,
    Guid? TargetCustomerId = null,
    string? IssuedBy = null) : IRequest<Result<DeviceProvisioningResultDto>>;

public sealed class ClaimDeviceCommandHandler
    : IRequestHandler<ClaimDeviceCommand, Result<DeviceProvisioningResultDto>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly IDeviceAttributeRepository _attributeRepository;
    private readonly IDeviceApiKeyService _apiKeyService;
    private readonly IMqttConnectionInfoProvider _connectionInfo;
    private readonly ITenantContext _tenantContext;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMqttSessionRevoker? _sessionRevoker;

    public ClaimDeviceCommandHandler(
        IDeviceRepository deviceRepository,
        ICustomerRepository customerRepository,
        IDeviceAttributeRepository attributeRepository,
        IDeviceApiKeyService apiKeyService,
        IMqttConnectionInfoProvider connectionInfo,
        ITenantContext tenantContext,
        IUnitOfWork unitOfWork,
        IMqttSessionRevoker? sessionRevoker = null)
    {
        _deviceRepository = deviceRepository;
        _customerRepository = customerRepository;
        _attributeRepository = attributeRepository;
        _apiKeyService = apiKeyService;
        _connectionInfo = connectionInfo;
        _tenantContext = tenantContext;
        _unitOfWork = unitOfWork;
        _sessionRevoker = sessionRevoker;
    }

    public async Task<Result<DeviceProvisioningResultDto>> Handle(
        ClaimDeviceCommand request,
        CancellationToken cancellationToken)
    {
        string chipId = request.ChipId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(chipId))
        {
            return Result<DeviceProvisioningResultDto>.Failure(
                new Error("Device.InvalidChipId", "El ChipId es obligatorio para reclamar el dispositivo."));
        }

        string claimSecret = request.ClaimSecret?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(claimSecret) || claimSecret.Length < 6)
        {
            return Result<DeviceProvisioningResultDto>.Failure(
                new Error("Device.InvalidClaimSecret", "El token de reclamación (bind_token / device_key) debe tener al menos 6 caracteres."));
        }

        var existing = await _deviceRepository.GetByChipIdAsync(request.TenantId, chipId, cancellationToken);
        if (existing is not null)
        {
            var serverAttrs = await _attributeRepository.GetByDeviceAndScopeAsync(
                existing.Id, AttributeScope.Server, cancellationToken);
            var clientAttrs = await _attributeRepository.GetByDeviceAndScopeAsync(
                existing.Id, AttributeScope.Client, cancellationToken);

            string? storedBindToken = serverAttrs.FirstOrDefault(a => string.Equals(a.Key, "bind_token", StringComparison.OrdinalIgnoreCase))?.Value
                ?? clientAttrs.FirstOrDefault(a => string.Equals(a.Key, "bind_token", StringComparison.OrdinalIgnoreCase))?.Value;
            string? storedDeviceKey = serverAttrs.FirstOrDefault(a => string.Equals(a.Key, "device_key", StringComparison.OrdinalIgnoreCase))?.Value
                ?? clientAttrs.FirstOrDefault(a => string.Equals(a.Key, "device_key", StringComparison.OrdinalIgnoreCase))?.Value;

            bool hasConfiguredSecret = !string.IsNullOrWhiteSpace(storedBindToken) || !string.IsNullOrWhiteSpace(storedDeviceKey);
            if (hasConfiguredSecret)
            {
                bool matchesBind = ConstantTimeEquals(claimSecret, storedBindToken);
                bool matchesKey = ConstantTimeEquals(claimSecret, storedDeviceKey);
                if (!matchesBind && !matchesKey)
                {
                    return Result<DeviceProvisioningResultDto>.Failure(
                        new Error("Device.InvalidClaimSecret", "El token de reclamación no coincide con el bind_token o device_key registrado del dispositivo."));
                }
            }

            Guid? targetCustomer = request.TargetCustomerId ?? _tenantContext.CustomerId;
            if (targetCustomer.HasValue && targetCustomer.Value != Guid.Empty)
            {
                existing.AssignToCustomer(targetCustomer.Value);
            }

            var (plainKey, keyErr) = MintApiKey(existing, request.IssuedBy);
            if (keyErr is not null)
            {
                return Result<DeviceProvisioningResultDto>.Failure(keyErr);
            }

            string effectiveDeviceKey = !string.IsNullOrWhiteSpace(storedDeviceKey)
                ? storedDeviceKey!
                : Guid.NewGuid().ToString("D");
            string effectiveBindToken = !string.IsNullOrWhiteSpace(storedBindToken)
                ? storedBindToken!
                : claimSecret;

            _deviceRepository.Update(existing);
            await _attributeRepository.UpsertAsync(
                existing.TenantId, existing.Id, AttributeScope.Server, "device_key", effectiveDeviceKey, cancellationToken);
            await _attributeRepository.UpsertAsync(
                existing.TenantId, existing.Id, AttributeScope.Server, "bind_token", effectiveBindToken, cancellationToken);
            await _attributeRepository.UpsertAsync(
                existing.TenantId, existing.Id, AttributeScope.Server, "pairing_mode", "claim", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            if (_sessionRevoker is not null)
            {
                await _sessionRevoker.RevokeSessionAsync(
                    existing.ChipId, "la ApiKey MQTT se rotó al reclamar el dispositivo", cancellationToken);
            }

            var creds = DeviceCredentialsMapper.ToDto(existing, _connectionInfo, plainKey);
            var bundles = DeviceProvisioningBundleBuilder.Build(creds, effectiveDeviceKey, effectiveBindToken);

            return Result<DeviceProvisioningResultDto>.Success(new DeviceProvisioningResultDto(
                Created: false,
                Credentials: creds,
                DeviceKey: effectiveDeviceKey,
                BindToken: effectiveBindToken,
                PairingMode: "claim",
                Bundles: bundles));
        }

        if (await _deviceRepository.ExistsByChipIdGlobalAsync(chipId, cancellationToken))
        {
            return Result<DeviceProvisioningResultDto>.Failure(
                new Error("Device.ChipIdTaken", $"ChipId '{chipId}' ya está registrado en la plataforma."));
        }

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
        var customerId = request.TargetCustomerId
            ?? _tenantContext.CustomerId
            ?? await _customerRepository.GetRootCustomerIdAsync(request.TenantId, cancellationToken);

        if (customerId is null || customerId.Value == Guid.Empty)
        {
            return Result<DeviceProvisioningResultDto>.Failure(new Error(
                "Device.NoCustomerNode",
                "El tenant no tiene un nodo Customer raíz al que asignar el dispositivo."));
        }

        device.AssignToCustomer(customerId.Value);
        device.AssignCapability(CapabilityType.Switch);

        var (newPlainKey, mintErr) = MintApiKey(device, request.IssuedBy);
        if (mintErr is not null)
        {
            return Result<DeviceProvisioningResultDto>.Failure(mintErr);
        }

        string newDeviceKey = claimSecret.Length == 36 ? claimSecret : Guid.NewGuid().ToString("D");

        await _deviceRepository.AddAsync(device, cancellationToken);
        await _attributeRepository.UpsertAsync(
            device.TenantId, device.Id, AttributeScope.Server, "device_key", newDeviceKey, cancellationToken);
        await _attributeRepository.UpsertAsync(
            device.TenantId, device.Id, AttributeScope.Server, "bind_token", claimSecret, cancellationToken);
        await _attributeRepository.UpsertAsync(
            device.TenantId, device.Id, AttributeScope.Server, "pairing_mode", "claim", cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var newCreds = DeviceCredentialsMapper.ToDto(device, _connectionInfo, newPlainKey);
        var newBundles = DeviceProvisioningBundleBuilder.Build(newCreds, newDeviceKey, claimSecret);

        return Result<DeviceProvisioningResultDto>.Success(new DeviceProvisioningResultDto(
            Created: true,
            Credentials: newCreds,
            DeviceKey: newDeviceKey,
            BindToken: claimSecret,
            PairingMode: "claim",
            Bundles: newBundles));
    }

    private (string PlainKey, Error? Error) MintApiKey(Device device, string? issuedBy)
    {
        var (plainKey, hash, fingerprint) = _apiKeyService.Generate();
        var setResult = device.SetMqttApiKey(hash, fingerprint, issuedBy);
        return setResult.IsFailure ? (string.Empty, setResult.Error) : (plainKey, null);
    }

    private static bool ConstantTimeEquals(string? candidate, string? expected)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(expected))
            return false;

        byte[] a = Encoding.UTF8.GetBytes(candidate.Trim());
        byte[] b = Encoding.UTF8.GetBytes(expected.Trim());
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

