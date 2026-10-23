using IoT.Domain.Common;
using IoT.Domain.Enums;

namespace IoT.Domain.Entities;

public sealed class Device : AggregateRoot<Guid>, ITenantScoped
{
    private readonly List<DeviceCapability> _capabilities = [];

    public Guid TenantId { get; private set; }
    public Guid? GroupId { get; private set; }
    public string ChipId { get; private set; }
    public string MacAddress { get; private set; }
    public string Name { get; private set; }
    public string Model { get; private set; }
    public string Manufacturer { get; private set; }
    public string? FirmwareVersion { get; private set; }
    public DeviceAdminStatus AdminStatus { get; private set; }
    public DeviceOperationalStatus OperationalStatus { get; private set; }
    public DateTime? LastCommunicationAt { get; private set; }
    public DateTime? LastTelemetryAt { get; private set; }
    public string MetadataJson { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    // Credencial MQTT por dispositivo (modelo de adopción por ApiKey).
    // Sólo se persiste el hash; el texto plano se muestra una vez al generarla.
    public string? MqttApiKeyHash { get; private set; }
    public string? MqttApiKeyFingerprint { get; private set; }
    public DateTime? MqttApiKeyIssuedAt { get; private set; }
    public string? MqttApiKeyIssuedBy { get; private set; }

    public bool HasMqttApiKey => !string.IsNullOrEmpty(MqttApiKeyHash);

    // OTA State tracking (portado de References/Mqtt-Broker DeviceStatus)
    public bool OtaInProgress { get; private set; }
    public string? OtaTargetVersion { get; private set; }
    public int OtaLastChunkSent { get; private set; }
    public int OtaTotalChunks { get; private set; }
    public bool OtaCompleted { get; private set; }
    public string? OtaErrorMessage { get; private set; }
    public DateTime? OtaUpdatedAt { get; private set; }

    public IReadOnlyCollection<DeviceCapability> Capabilities => _capabilities.AsReadOnly();

    private Device(
        Guid id,
        Guid tenantId,
        Guid? groupId,
        string chipId,
        string macAddress,
        string name,
        string model,
        string manufacturer,
        string? firmwareVersion) : base(id)
    {
        TenantId = tenantId;
        GroupId = groupId;
        ChipId = chipId;
        MacAddress = macAddress;
        Name = name;
        Model = model;
        Manufacturer = manufacturer;
        FirmwareVersion = firmwareVersion;
        AdminStatus = DeviceAdminStatus.Active;
        OperationalStatus = DeviceOperationalStatus.Offline;
        MetadataJson = "{}";
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public static Result<Device> Create(
        Guid tenantId,
        string chipId,
        string macAddress,
        string name,
        string model,
        string manufacturer,
        string? firmwareVersion = null,
        Guid? groupId = null)
    {
        if (tenantId == Guid.Empty)
            return Result<Device>.Failure(new Error("Device.InvalidTenant", "TenantId cannot be empty."));

        if (string.IsNullOrWhiteSpace(chipId))
            return Result<Device>.Failure(new Error("Device.InvalidChipId", "ChipId is required."));

        if (string.IsNullOrWhiteSpace(macAddress))
            return Result<Device>.Failure(new Error("Device.InvalidMacAddress", "MacAddress is required."));

        if (string.IsNullOrWhiteSpace(name))
            return Result<Device>.Failure(new Error("Device.InvalidName", "Device name is required."));

        return new Device(
            Guid.NewGuid(),
            tenantId,
            groupId,
            chipId.Trim(),
            macAddress.Trim().ToUpperInvariant(),
            name.Trim(),
            string.IsNullOrWhiteSpace(model) ? "Generic-IoT" : model.Trim(),
            string.IsNullOrWhiteSpace(manufacturer) ? "Generic" : manufacturer.Trim(),
            firmwareVersion?.Trim());
    }

    public Result AssignCapability(CapabilityType capabilityType, string? configurationJson = null)
    {
        if (_capabilities.Any(c => c.CapabilityType == capabilityType))
        {
            var existing = _capabilities.First(c => c.CapabilityType == capabilityType);
            existing.Enable();
            if (!string.IsNullOrWhiteSpace(configurationJson))
                existing.UpdateConfiguration(configurationJson);
            return Result.Success();
        }

        var capability = new DeviceCapability(Guid.NewGuid(), Id, capabilityType, configurationJson);
        _capabilities.Add(capability);
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public Result RemoveCapability(CapabilityType capabilityType)
    {
        var existing = _capabilities.FirstOrDefault(c => c.CapabilityType == capabilityType);
        if (existing is null)
            return Result.Failure(new Error("Device.CapabilityNotFound", $"Capability '{capabilityType}' is not assigned to this device."));

        _capabilities.Remove(existing);
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public bool HasCapability(CapabilityType capabilityType) =>
        _capabilities.Any(c => c.CapabilityType == capabilityType && c.IsEnabled);

    public void RecordHeartbeat(DateTime timestamp, string? ipAddress = null)
    {
        LastCommunicationAt = timestamp;
        OperationalStatus = DeviceOperationalStatus.Online;
        UpdatedAt = DateTime.UtcNow;
    }

    public void RecordTelemetry(DateTime timestamp)
    {
        LastCommunicationAt = timestamp;
        LastTelemetryAt = timestamp;
        OperationalStatus = DeviceOperationalStatus.Online;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateFirmwareVersion(string newVersion)
    {
        if (string.IsNullOrWhiteSpace(newVersion)) return;
        FirmwareVersion = newVersion.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetAdminStatus(DeviceAdminStatus status)
    {
        AdminStatus = status;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetOperationalStatus(DeviceOperationalStatus status)
    {
        OperationalStatus = status;
        UpdatedAt = DateTime.UtcNow;
    }

    public void AssignToGroup(Guid? groupId)
    {
        GroupId = groupId;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateMetadata(string metadataJson)
    {
        MetadataJson = string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Asigna/rota la credencial MQTT del dispositivo. Recibe el hash (no el texto
    /// plano) y una huella corta no reversible para mostrar en la UI. El texto plano
    /// lo genera y lo muestra una sola vez la capa de aplicación.
    /// </summary>
    public Result SetMqttApiKey(string hash, string fingerprint, string? issuedBy = null)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return Result.Failure(new Error("Device.InvalidApiKeyHash", "El hash de la ApiKey es obligatorio."));
        if (string.IsNullOrWhiteSpace(fingerprint))
            return Result.Failure(new Error("Device.InvalidApiKeyFingerprint", "La huella de la ApiKey es obligatoria."));

        MqttApiKeyHash = hash;
        MqttApiKeyFingerprint = fingerprint;
        MqttApiKeyIssuedAt = DateTime.UtcNow;
        MqttApiKeyIssuedBy = issuedBy;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public void StartOta(string targetVersion, int totalChunks)
    {
        OtaInProgress = true;
        OtaCompleted = false;
        OtaTargetVersion = targetVersion?.Trim();
        OtaTotalChunks = totalChunks;
        OtaLastChunkSent = 0;
        OtaErrorMessage = null;
        OtaUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateOtaProgress(int lastChunkSent, int totalChunks)
    {
        OtaLastChunkSent = lastChunkSent;
        OtaTotalChunks = totalChunks;
        OtaUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void CompleteOta(string newVersion)
    {
        FirmwareVersion = newVersion?.Trim();
        OtaInProgress = false;
        OtaCompleted = true;
        OtaErrorMessage = null;
        OtaUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void FailOta(string errorMessage)
    {
        OtaInProgress = false;
        OtaCompleted = false;
        OtaErrorMessage = errorMessage;
        OtaUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void ResetOta()
    {
        OtaInProgress = false;
        OtaCompleted = false;
        OtaErrorMessage = null;
        OtaLastChunkSent = 0;
        OtaTotalChunks = 0;
        OtaTargetVersion = null;
        OtaUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}
