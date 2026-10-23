using IoT.Domain.Common;

namespace IoT.Domain.Entities;

/// <summary>
/// Registro de versión de firmware para actualizaciones OTA (Over-The-Air) de dispositivos IoT.
/// Portado y adaptado de References/Mqtt-Broker (FirmwareVersionRecords).
/// </summary>
public sealed class FirmwareVersionRecord : Entity<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public string FirmwareVersion { get; private set; }
    public string Src { get; private set; }
    public string? TargetModel { get; private set; }
    public string? FeatureDescription { get; private set; }
    public long FileSizeBytes { get; private set; }
    public string Sha256 { get; private set; }
    public int TotalChunks { get; private set; }
    public int ChunkSizeBytes { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsDeleted { get; private set; }
    public string? UpdatedFromIp { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private FirmwareVersionRecord(
        Guid id,
        Guid tenantId,
        string firmwareVersion,
        string src,
        string? targetModel,
        string? featureDescription,
        long fileSizeBytes,
        string sha256,
        int totalChunks,
        int chunkSizeBytes,
        string? updatedFromIp) : base(id)
    {
        TenantId = tenantId;
        FirmwareVersion = firmwareVersion;
        Src = src;
        TargetModel = targetModel;
        FeatureDescription = featureDescription;
        FileSizeBytes = fileSizeBytes;
        Sha256 = sha256;
        TotalChunks = totalChunks;
        ChunkSizeBytes = chunkSizeBytes;
        IsActive = true;
        IsDeleted = false;
        UpdatedFromIp = updatedFromIp;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public static Result<FirmwareVersionRecord> Create(
        Guid tenantId,
        string firmwareVersion,
        string src,
        long fileSizeBytes,
        string sha256,
        int totalChunks,
        int chunkSizeBytes = 2048,
        string? targetModel = null,
        string? featureDescription = null,
        string? updatedFromIp = null)
    {
        if (tenantId == Guid.Empty)
            return Result<FirmwareVersionRecord>.Failure(new Error("Firmware.InvalidTenant", "TenantId cannot be empty."));

        if (string.IsNullOrWhiteSpace(firmwareVersion))
            return Result<FirmwareVersionRecord>.Failure(new Error("Firmware.InvalidVersion", "FirmwareVersion cannot be empty."));

        if (string.IsNullOrWhiteSpace(src))
            return Result<FirmwareVersionRecord>.Failure(new Error("Firmware.InvalidSrc", "Src path cannot be empty."));

        if (string.IsNullOrWhiteSpace(sha256))
            return Result<FirmwareVersionRecord>.Failure(new Error("Firmware.InvalidSha256", "Sha256 hash cannot be empty."));

        if (fileSizeBytes <= 0)
            return Result<FirmwareVersionRecord>.Failure(new Error("Firmware.InvalidFileSize", "File size must be greater than 0."));

        return new FirmwareVersionRecord(
            Guid.NewGuid(),
            tenantId,
            firmwareVersion.Trim(),
            src.Trim(),
            targetModel?.Trim(),
            featureDescription?.Trim(),
            fileSizeBytes,
            sha256.Trim().ToLowerInvariant(),
            totalChunks,
            chunkSizeBytes,
            updatedFromIp?.Trim());
    }

    public void MarkAsDeleted()
    {
        IsDeleted = true;
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetActive(bool active)
    {
        IsActive = active;
        UpdatedAt = DateTime.UtcNow;
    }
}
