using IoT.Domain.Entities;

namespace IoT.Domain.Repositories;

public interface IMqttBrokerConfigRepository
{
    Task<IReadOnlyList<MqttBrokerConfig>> GetByTenantAsync(Guid tenantId, CancellationToken ct);
    Task<MqttBrokerConfig?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task AddAsync(MqttBrokerConfig broker, CancellationToken ct);
    void Update(MqttBrokerConfig broker);
    void Remove(MqttBrokerConfig broker);
}
