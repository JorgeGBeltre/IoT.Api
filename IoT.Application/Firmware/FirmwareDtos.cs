namespace IoT.Application.Firmware;

public sealed record FirmwareVersionRecordDto(
    Guid Id,
    Guid TenantId,
    string FirmwareVersion,
    string Src,
    string? TargetModel,
    string? FeatureDescription,
    long FileSizeBytes,
    string Sha256,
    int TotalChunks,
    int ChunkSizeBytes,
    bool IsActive,
    string? UpdatedFromIp,
    DateTime CreatedAt);

public sealed record DeviceOtaStatusDto(
    Guid DeviceId,
    string ChipId,
    string DeviceName,
    string? CurrentFirmwareVersion,
    bool OtaInProgress,
    string? OtaTargetVersion,
    int OtaLastChunkSent,
    int OtaTotalChunks,
    int Percentage,
    bool OtaCompleted,
    string? OtaErrorMessage,
    DateTime? OtaUpdatedAt);

/// <summary>
/// Contrato sanitizado para interfaz de usuario final: expone únicamente disponibilidad,
/// versión nueva compatible y notas de versión, sin exponer jamás URLs internas ni hashes SHA-256.
/// </summary>
public sealed record DeviceFirmwareAvailabilityDto(
    Guid DeviceId,
    bool UpdateAvailable,
    string? CurrentVersion,
    string? AvailableVersion,
    string? ReleaseNotes,
    string Channel,
    DateTime? PublishedAt);

public sealed class StartDeviceOtaRequest
{
    public string FirmwareVersion { get; set; } = string.Empty;
}

public sealed class StartGroupOtaRequest
{
    public string FirmwareVersion { get; set; } = string.Empty;
}

public sealed class RollbackFirmwareRequest
{
    public Guid? FirmwareRecordId { get; set; }
    public string? TargetVersion { get; set; }
}

/// <summary>
/// Parámetros genéricos heredados del broker de referencia (References/Mqtt-Broker)
/// para 100% retrocompatibilidad con clientes existentes.
/// </summary>
public sealed class ReferenceGenericParameters
{
    public Guid? GroupId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? FirmwareRecordId { get; set; }
    public string? FirmwareVersion { get; set; }
}
