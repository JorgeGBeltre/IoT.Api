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
        string? actor = null,
        CancellationToken cancellationToken = default);

    Task<Result> StartDeviceOtaAsync(
        Guid tenantId,
        Guid deviceId,
        string version,
        string? actor = null,
        CancellationToken cancellationToken = default);

    Task<Result> StartGroupOtaAsync(
        Guid tenantId,
        Guid groupId,
        string version,
        string? actor = null,
        CancellationToken cancellationToken = default);

    Task<Result> RollbackDeviceOtaAsync(
        Guid tenantId,
        Guid deviceId,
        Guid? firmwareRecordId = null,
        string? targetVersion = null,
        string? actor = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Borra una versión de firmware: marca el registro y DESENLAZA el binario del disco.
    /// Rechaza si algún equipo tiene una campaña EN CURSO a esa versión (no se le quita el
    /// binario a un rollout vivo). Sin esto no existía ninguna vía de borrado y el almacén
    /// sólo crecía.
    /// </summary>
    Task<Result> DeleteFirmwareVersionAsync(
        Guid tenantId,
        string version,
        string? actor = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancela la campaña OTA en curso de un dispositivo: lo saca de "actualizando" para que
    /// el operador no tenga que esperar la ventana de rescate. La bomba en vuelo lo detecta en
    /// su relectura por trozo y abandona. Idempotente: si no hay campaña, no hace nada.
    /// </summary>
    Task<Result> CancelDeviceOtaAsync(
        Guid tenantId,
        Guid deviceId,
        string? actor = null,
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
