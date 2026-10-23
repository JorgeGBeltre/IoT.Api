using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoT.Infrastructure.Repositories;

public sealed class MqttBrokerConfigRepository : IMqttBrokerConfigRepository
{
    private readonly ApplicationDbContext _context;

    public MqttBrokerConfigRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<MqttBrokerConfig>> GetByTenantAsync(Guid tenantId, CancellationToken ct)
    {
        return await _context.MqttBrokers
            .AsNoTracking()
            .Where(b => b.TenantId == tenantId)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
    }

    public Task<MqttBrokerConfig?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct) =>
        _context.MqttBrokers.FirstOrDefaultAsync(b => b.TenantId == tenantId && b.Id == id, ct);

    public async Task AddAsync(MqttBrokerConfig broker, CancellationToken ct)
    {
        await _context.MqttBrokers.AddAsync(broker, ct);
    }

    public void Update(MqttBrokerConfig broker) => _context.MqttBrokers.Update(broker);

    public void Remove(MqttBrokerConfig broker) => _context.MqttBrokers.Remove(broker);
}
