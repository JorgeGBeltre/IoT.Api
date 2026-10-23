using IoT.Application.Firmware;
using IoT.Domain.Common;

namespace IoT.Application.Common.Interfaces;

public interface IFirmwareOtaService
{
    Task<Result<FirmwareVersionRecordDto>> UploadFirmwareAsync(
        Guid tenantId,
        Stream fileStream,
        string fileName,
        string version,
        string? targetModel = null,
        string? featureDescription = null,
        string? updatedFromIp = null,
        CancellationToken cancellationToken = default);

    Task<Result> StartDeviceOtaAsync(
        Guid tenantId,
        Guid deviceId,
        string version,
        CancellationToken cancellationToken = default);

    Task<Result> StartGroupOtaAsync(
        Guid tenantId,
        Guid groupId,
        string version,
        CancellationToken cancellationToken = default);

    Task<Result> RollbackDeviceOtaAsync(
        Guid tenantId,
        Guid deviceId,
        Guid? firmwareRecordId = null,
        string? targetVersion = null,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<FirmwareVersionRecordDto>>> GetFirmwareRecordsAsync(
        Guid tenantId,
        string? targetModel = null,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default);

    Task<Result<DeviceOtaStatusDto>> GetDeviceOtaStatusAsync(
        Guid tenantId,
        Guid deviceId,
        CancellationToken cancellationToken = default);

    Task HandleDeviceOtaReportAsync(
        string chipId,
        string otaAction,
        string? payloadJson,
        CancellationToken cancellationToken = default);
}
