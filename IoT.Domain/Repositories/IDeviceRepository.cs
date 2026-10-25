using IoT.Domain.Entities;
using IoT.Domain.Enums;

namespace IoT.Domain.Repositories;

public interface IDeviceRepository
{
    Task<Device?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Device?> GetByChipIdAsync(Guid tenantId, string chipId, CancellationToken cancellationToken = default);
    Task<Device?> GetByChipIdGlobalAsync(string chipId, CancellationToken cancellationToken = default);

    /// <summary>¿Existe algún dispositivo con este ChipId en CUALQUIER tenant? (unicidad global de IMEI/ChipId).</summary>
    Task<bool> ExistsByChipIdGlobalAsync(string chipId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Device>> GetByTenantAsync(Guid tenantId, DeviceOperationalStatus? status = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Device>> GetByGroupIdAsync(Guid tenantId, Guid groupId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Dispositivos marcados como "actualizacion en curso". Lo usa la reconciliacion de
    /// arranque: si el proceso se reinicio a mitad de un rollout, la bomba que lo enviaba
    /// murio con el y el equipo queda en ese estado para siempre, sin aborto ni reanudacion.
    /// </summary>
    Task<IReadOnlyList<Device>> GetDevicesWithOtaInProgressAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Toma la campaña OTA de un dispositivo de forma ATÓMICA y CONDICIONAL: una sola
    /// escritura en la base que lo pasa a "actualización en curso" SÓLO si no hay ya una
    /// campaña viva —o la que había lleva más de <paramref name="staleAfter"/> sin avanzar—.
    ///
    /// Reemplaza el leer-luego-escribir de StartDeviceOtaAsync, que no era atómico: dos
    /// peticiones concurrentes (doble clic, reintento del cliente, o start-ota a la vez que el
    /// upload que arranca la campaña por su cuenta) veían ambas OtaInProgress=false y lanzaban
    /// DOS bombas sobre el mismo tópico ota/&lt;chipId&gt; (MEDIUM-6/LOW-1/LOW-4). Aquí la
    /// condición es parte del propio UPDATE, así que exactamente una afecta a 1 fila.
    ///
    /// Escribe los MISMOS campos que <see cref="Device.StartOta"/> (en curso, versión, trozos,
    /// progreso a 0, sin error, marca de tiempo). Devuelve true si ESTA llamada tomó la
    /// campaña (y debe lanzar la bomba); false si ya había una en curso y reciente.
    /// </summary>
    Task<bool> TryStartOtaCampaignAsync(
        Guid tenantId,
        Guid deviceId,
        string targetVersion,
        int totalChunks,
        TimeSpan staleAfter,
        CancellationToken cancellationToken = default);

    Task AddAsync(Device device, CancellationToken cancellationToken = default);
    void Update(Device device);
    void Delete(Device device);
}
