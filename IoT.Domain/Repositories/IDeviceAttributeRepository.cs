using IoT.Domain.Entities;
using IoT.Domain.Enums;

namespace IoT.Domain.Repositories;

/// <summary>
/// Acceso a los atributos de dispositivo (modelo de atributos de ThingsBoard, P1a). Todas las
/// consultas RESPETAN los filtros globales (sin <c>IgnoreQueryFilters</c>) para que el aislamiento por
/// tenant (ADR-003) y la RLS por subárbol vía el device dueño (ADR-013) acoten el resultado.
/// </summary>
public interface IDeviceAttributeRepository
{
    /// <summary>
    /// Inserta el atributo (<paramref name="scope"/>, <paramref name="key"/>) para el dispositivo o,
    /// si ya existe, actualiza su valor — respetando el índice único
    /// (<c>TenantId</c>, <c>DeviceId</c>, <c>Scope</c>, <c>Key</c>). No persiste por sí mismo: encola
    /// el cambio en la unidad de trabajo; el llamador hace <c>SaveChangesAsync</c>.
    /// </summary>
    Task<DeviceAttribute> UpsertAsync(
        Guid tenantId,
        Guid deviceId,
        AttributeScope scope,
        string key,
        string value,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceAttribute>> ListByDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceAttribute>> GetByDeviceAndScopeAsync(
        Guid deviceId,
        AttributeScope scope,
        CancellationToken cancellationToken = default);
}
