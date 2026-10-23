using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoT.Infrastructure.Repositories;

public sealed class FirmwareVersionRecordRepository : IFirmwareVersionRecordRepository
{
    private readonly ApplicationDbContext _context;

    public FirmwareVersionRecordRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<FirmwareVersionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.FirmwareVersionRecords
            .FirstOrDefaultAsync(f => f.Id == id && !f.IsDeleted, cancellationToken);
    }

    public async Task<FirmwareVersionRecord?> GetByVersionAsync(Guid tenantId, string version, CancellationToken cancellationToken = default)
    {
        string normalized = version.Trim().TrimStart('v', 'V');
        return await _context.FirmwareVersionRecords
            .FirstOrDefaultAsync(f => f.TenantId == tenantId &&
                                      (f.FirmwareVersion == normalized || f.FirmwareVersion == version) &&
                                      !f.IsDeleted,
                                 cancellationToken);
    }

    public async Task<IReadOnlyList<FirmwareVersionRecord>> GetRecordsAsync(
        Guid tenantId,
        string? targetModel = null,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        var query = _context.FirmwareVersionRecords
            .Where(f => f.TenantId == tenantId && !f.IsDeleted);

        if (!string.IsNullOrWhiteSpace(targetModel))
        {
            query = query.Where(f => f.TargetModel == null || f.TargetModel == targetModel);
        }

        return await query
            .OrderByDescending(f => f.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(FirmwareVersionRecord record, CancellationToken cancellationToken = default)
    {
        await _context.FirmwareVersionRecords.AddAsync(record, cancellationToken);
    }

    public void Update(FirmwareVersionRecord record)
    {
        _context.FirmwareVersionRecords.Update(record);
    }

    public void Remove(FirmwareVersionRecord record)
    {
        record.MarkAsDeleted();
        _context.FirmwareVersionRecords.Update(record);
    }
}
