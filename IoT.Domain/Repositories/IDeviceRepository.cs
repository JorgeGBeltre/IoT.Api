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
    Task AddAsync(Device device, CancellationToken cancellationToken = default);
    void Update(Device device);
    void Delete(Device device);
}
