using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoT.Infrastructure.Repositories;

public sealed class TenantSettingsRepository : ITenantSettingsRepository
{
    private readonly ApplicationDbContext _context;

    public TenantSettingsRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<TenantSettings?> GetByTenantAsync(Guid tenantId, CancellationToken ct) =>
        _context.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

    public async Task AddAsync(TenantSettings settings, CancellationToken ct)
    {
        await _context.TenantSettings.AddAsync(settings, ct);
    }

    public void Update(TenantSettings settings) => _context.TenantSettings.Update(settings);
}
