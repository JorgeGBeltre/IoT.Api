using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoT.Infrastructure.Repositories;

/// <summary>
/// Repositorio de convertidores dinámicos por tenant. Las consultas van sobre el
/// <see cref="ApplicationDbContext"/>, cuyo filtro global por tenant y la RLS de PostgreSQL
/// (<c>tenant_isolation</c>) acotan automáticamente al tenant de la sesión. El parámetro
/// <c>tenantId</c> hace explícita la intención y refuerza el filtro, pero la línea dura es la RLS.
/// </summary>
public sealed class ProtocolConverterScriptRepository : IProtocolConverterScriptRepository
{
    private readonly ApplicationDbContext _context;

    public ProtocolConverterScriptRepository(ApplicationDbContext context) => _context = context;

    public Task<ProtocolConverterScript?> GetByTenantAndProfileKeyAsync(Guid tenantId, string profileKey, CancellationToken ct)
    {
        var key = ProtocolConverterScript.NormalizeKey(profileKey);
        return _context.ProtocolConverterScripts
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.ProfileKey == key, ct);
    }

    public async Task<IReadOnlyList<ProtocolConverterScript>> GetByTenantAsync(Guid tenantId, CancellationToken ct)
    {
        return await _context.ProtocolConverterScripts
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.ProfileKey)
            .ToListAsync(ct);
    }

    public async Task AddAsync(ProtocolConverterScript script, CancellationToken ct) =>
        await _context.ProtocolConverterScripts.AddAsync(script, ct);

    public void Update(ProtocolConverterScript script) => _context.ProtocolConverterScripts.Update(script);
}
