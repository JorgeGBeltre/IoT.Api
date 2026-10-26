using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoT.Infrastructure.Repositories;

public sealed class TenantRepository : ITenantRepository
{
    private readonly ApplicationDbContext _context;

    public TenantRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Tenants.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    public async Task<Tenant?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        return await _context.Tenants.FirstOrDefaultAsync(t => t.Code == code.ToUpperInvariant(), cancellationToken);
    }

    public async Task<Tenant?> GetByBootstrapApiKeyHashAsync(string bootstrapApiKeyHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(bootstrapApiKeyHash))
            return null;

        // Tenant no es ITenantScoped, así que no hay filtro global que esquivar; la consulta
        // es una igualdad exacta sobre el índice único filtrado de BootstrapApiKeyHash.
        return await _context.Tenants
            .FirstOrDefaultAsync(t => t.BootstrapApiKeyHash == bootstrapApiKeyHash, cancellationToken);
    }

    public async Task AddAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        await _context.Tenants.AddAsync(tenant, cancellationToken);
    }

    public void Update(Tenant tenant)
    {
        _context.Tenants.Update(tenant);
    }
}

public sealed class DeviceCommandRepository : IDeviceCommandRepository
{
    private readonly ApplicationDbContext _context;

    public DeviceCommandRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<DeviceCommand?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.DeviceCommands.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<DeviceCommand?> GetByClientRequestIdAsync(Guid tenantId, string clientRequestId, CancellationToken cancellationToken = default)
    {
        return await _context.DeviceCommands
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ClientRequestId == clientRequestId, cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceCommand>> GetPendingCommandsByDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        return await _context.DeviceCommands
            .IgnoreQueryFilters()
            .Where(c => c.DeviceId == deviceId && (c.Status == Domain.Enums.CommandStatus.Pending || c.Status == Domain.Enums.CommandStatus.Sent))
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(DeviceCommand command, CancellationToken cancellationToken = default)
    {
        await _context.DeviceCommands.AddAsync(command, cancellationToken);
    }

    public void Update(DeviceCommand command)
    {
        _context.DeviceCommands.Update(command);
    }

    public Task<int> PurgeClosedCommandsOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
        => PurgeClosedCommandsOlderThanAsync(cutoffUtc, null, cancellationToken);

    public async Task<int> PurgeClosedCommandsOlderThanAsync(DateTime cutoffUtc, Guid? tenantId, CancellationToken cancellationToken = default)
    {
        var closedStatuses = new[]
        {
            Domain.Enums.CommandStatus.Confirmed,
            Domain.Enums.CommandStatus.Failed,
            Domain.Enums.CommandStatus.TimedOut
        };

        var query = _context.DeviceCommands
            .IgnoreQueryFilters()
            .Where(c => closedStatuses.Contains(c.Status) && c.CreatedAt < cutoffUtc);

        if (tenantId.HasValue && tenantId.Value != Guid.Empty)
        {
            query = query.Where(c => c.TenantId == tenantId.Value);
        }

        var expired = await query.ToListAsync(cancellationToken);

        if (expired.Count > 0)
        {
            _context.DeviceCommands.RemoveRange(expired);
            return await _context.SaveChangesAsync(cancellationToken);
        }

        return 0;
    }
}
