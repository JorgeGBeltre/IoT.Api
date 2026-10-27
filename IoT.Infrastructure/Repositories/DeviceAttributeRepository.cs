using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoT.Infrastructure.Repositories;

public sealed class DeviceAttributeRepository : IDeviceAttributeRepository
{
    private readonly ApplicationDbContext _context;

    public DeviceAttributeRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<DeviceAttribute> UpsertAsync(
        Guid tenantId,
        Guid deviceId,
        AttributeScope scope,
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = key?.Trim();

        // Nota sobre `tenantId`: el AISLAMIENTO por tenant/subárbol lo IMPONE el scope ambiente (el
        // filtro global de EF por tenant + la RLS de PostgreSQL por tenant y subárbol vía el device
        // dueño), no este parámetro. `tenantId` sólo SELLA el TenantId de una fila nueva vía
        // DeviceAttribute.Create (invariante de la entidad); si no coincide con el tenant de la sesión,
        // el WITH CHECK de la política rechaza la escritura. El scope ambiente manda.

        // La consulta RESPETA los filtros globales: la RLS acota por tenant/subárbol. Se busca la
        // fila existente de esa (scope, key) para actualizar en vez de insertar un duplicado (lo que
        // violaría el índice único (TenantId, DeviceId, Scope, Key)).
        var existing = await _context.DeviceAttributes
            .FirstOrDefaultAsync(
                a => a.DeviceId == deviceId && a.Scope == scope && a.Key == normalizedKey,
                cancellationToken);

        if (existing is not null)
        {
            existing.UpdateValue(value);
            _context.DeviceAttributes.Update(existing);
            return existing;
        }

        var created = DeviceAttribute.Create(tenantId, deviceId, scope, normalizedKey ?? string.Empty, value);
        if (created.IsFailure)
        {
            throw new InvalidOperationException(
                $"No se pudo crear el atributo de dispositivo: {created.Error.Code} - {created.Error.Message}");
        }

        await _context.DeviceAttributes.AddAsync(created.Value, cancellationToken);
        return created.Value;
    }

    public async Task<IReadOnlyList<DeviceAttribute>> ListByDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        return await _context.DeviceAttributes
            .Where(a => a.DeviceId == deviceId)
            .OrderBy(a => a.Scope)
            .ThenBy(a => a.Key)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceAttribute>> GetByDeviceAndScopeAsync(
        Guid deviceId,
        AttributeScope scope,
        CancellationToken cancellationToken = default)
    {
        return await _context.DeviceAttributes
            .Where(a => a.DeviceId == deviceId && a.Scope == scope)
            .OrderBy(a => a.Key)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        Guid deviceId,
        AttributeScope scope,
        string key,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = key?.Trim();

        // RESPETA los filtros globales: la RLS acota por tenant/subárbol. Si la fila no es visible
        // (otro subárbol/tenant), no se encuentra y no se borra nada (mismo trato que inexistente).
        var existing = await _context.DeviceAttributes
            .FirstOrDefaultAsync(
                a => a.DeviceId == deviceId && a.Scope == scope && a.Key == normalizedKey,
                cancellationToken);

        if (existing is null)
        {
            return false;
        }

        _context.DeviceAttributes.Remove(existing);
        return true;
    }

    public void DiscardPendingChanges()
    {
        // DETACH de toda entrada de DeviceAttribute aún pendiente (Added/Modified/Deleted) tras un fallo
        // del upsert best-effort. Así el change-tracker queda limpio de estas filas y un SaveChanges
        // POSTERIOR del mismo contexto (p.ej. confirmación de comando en la ingesta) no las reintenta ni
        // re-lanza el error. NO toca entidades de otros tipos ya encoladas por el llamador.
        foreach (var entry in _context.ChangeTracker.Entries<DeviceAttribute>().ToList())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                entry.State = EntityState.Detached;
            }
        }
    }
}
