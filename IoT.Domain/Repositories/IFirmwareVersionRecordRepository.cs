using IoT.Domain.Entities;

namespace IoT.Domain.Repositories;

public interface IFirmwareVersionRecordRepository
{
    Task<FirmwareVersionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<FirmwareVersionRecord?> GetByVersionAsync(Guid tenantId, string version, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FirmwareVersionRecord>> GetRecordsAsync(Guid tenantId, string? targetModel = null, int skip = 0, int take = 50, CancellationToken cancellationToken = default);
    Task AddAsync(FirmwareVersionRecord record, CancellationToken cancellationToken = default);
    void Update(FirmwareVersionRecord record);
    void Remove(FirmwareVersionRecord record);
}
