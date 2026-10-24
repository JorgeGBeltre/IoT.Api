using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoT.Infrastructure.Repositories;

public sealed class DeviceRepository : IDeviceRepository
{
    private readonly ApplicationDbContext _context;

    public DeviceRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Device?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Devices
            .Include(d => d.Capabilities)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    public async Task<Device?> GetByChipIdAsync(Guid tenantId, string chipId, CancellationToken cancellationToken = default)
    {
        return await _context.Devices
            .Include(d => d.Capabilities)
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.ChipId == chipId, cancellationToken);
    }

    public async Task<Device?> GetByChipIdGlobalAsync(string chipId, CancellationToken cancellationToken = default)
    {
        // ChipId NO es único a nivel global (sólo (TenantId, ChipId) lo es). Para
        // dispositivos sin JWT (TCP/HTTP) la identidad es sólo el ChipId/IMEI; si dos
        // tenants comparten IMEI, resolver "el primero" atribuiría la telemetría a un
        // tenant arbitrario (fuga cross-tenant). Por eso, ante COLISIÓN devolvemos null
        // (ambiguo => no adoptable) en vez de elegir al azar.
        var matches = await _context.Devices
            .IgnoreQueryFilters()
            .Include(d => d.Capabilities)
            .Where(d => d.ChipId == chipId)
            .Take(2)
            .ToListAsync(cancellationToken);

        return matches.Count == 1 ? matches[0] : null;
    }

    public async Task<bool> ExistsByChipIdGlobalAsync(string chipId, CancellationToken cancellationToken = default)
    {
        return await _context.Devices
            .IgnoreQueryFilters()
            .AnyAsync(d => d.ChipId == chipId, cancellationToken);
    }

    /// <summary>
    /// Deliberadamente SIN filtro de tenant y sin Include: lo usa la reconciliacion de
    /// arranque, que es una tarea del proceso y no de una peticion, y solo necesita el estado
    /// OTA de cada equipo.
    /// </summary>
    public async Task<IReadOnlyList<Device>> GetDevicesWithOtaInProgressAsync(CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters es IMPRESCINDIBLE aqui: el DbContext aplica un filtro global
        // TenantId == CurrentTenantId a toda entidad con tenant, y esta consulta corre al
        // ARRANCAR el proceso, cuando no hay tenant ambiental. Sin esto devolvia vacio EN
        // SILENCIO y la reconciliacion no cerraba ninguna campana (comprobado en vivo: el
        // equipo seguia marcado como "actualizando" tras reiniciar el servicio).
        return await _context.Devices
            .IgnoreQueryFilters()
            .Where(d => d.OtaInProgress)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Device>> GetByTenantAsync(Guid tenantId, DeviceOperationalStatus? status = null, CancellationToken cancellationToken = default)
    {
        var query = _context.Devices
            .Include(d => d.Capabilities)
            .Where(d => d.TenantId == tenantId);

        if (status.HasValue)
        {
            query = query.Where(d => d.OperationalStatus == status.Value);
        }

        return await query.ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Device>> GetByGroupIdAsync(Guid tenantId, Guid groupId, CancellationToken cancellationToken = default)
    {
        return await _context.Devices
            .Include(d => d.Capabilities)
            .Where(d => d.TenantId == tenantId && d.GroupId == groupId)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Device device, CancellationToken cancellationToken = default)
    {
        await _context.Devices.AddAsync(device, cancellationToken);
    }

    public void Update(Device device)
    {
        _context.Devices.Update(device);
    }

    public void Delete(Device device)
    {
        _context.Devices.Remove(device);
    }
}
