using IoT.Domain.Entities;
using IoT.Domain.Enums;

namespace IoT.Domain.Repositories;

public interface IDeviceCommandRepository
{
    Task<DeviceCommand?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DeviceCommand?> GetByClientRequestIdAsync(Guid tenantId, string clientRequestId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceCommand>> GetPendingCommandsByDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task AddAsync(DeviceCommand command, CancellationToken cancellationToken = default);
    void Update(DeviceCommand command);
    Task<int> PurgeClosedCommandsOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
