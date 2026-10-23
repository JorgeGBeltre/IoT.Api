using IoT.Domain.Entities;

namespace IoT.Domain.Repositories;

public interface ITenantSettingsRepository
{
    Task<TenantSettings?> GetByTenantAsync(Guid tenantId, CancellationToken ct);
    Task AddAsync(TenantSettings settings, CancellationToken ct);
    void Update(TenantSettings settings);
}
