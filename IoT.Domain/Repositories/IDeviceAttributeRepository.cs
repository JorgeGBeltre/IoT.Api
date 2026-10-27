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

    /// <summary>
    /// Borra el atributo (<paramref name="scope"/>, <paramref name="key"/>) del dispositivo si existe
    /// y es visible (RESPETANDO los filtros: tenant + subárbol vía el device dueño). Encola el borrado
    /// en la unidad de trabajo; el llamador hace <c>SaveChangesAsync</c>. Devuelve <c>true</c> si había
    /// una fila que borrar, <c>false</c> si no existía/no era visible.
    /// </summary>
    Task<bool> DeleteAsync(
        Guid deviceId,
        AttributeScope scope,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// DESCARTA (detach) de la unidad de trabajo los cambios de <c>DeviceAttribute</c> que quedaron
    /// pendientes (Added/Modified/Deleted) tras un fallo del upsert. Pensado para un upsert
    /// BEST-EFFORT que comparte el mismo contexto con otras escrituras (p.ej. la ingesta): si su
    /// <c>SaveChangesAsync</c> falla (23505 por carrera, 42501 por RLS…), las entidades siguen
    /// encoladas y un <c>SaveChangesAsync</c> POSTERIOR del llamador las reintentaría, re-lanzando el
    /// mismo error y arrastrando esa otra escritura. Llamar en el <c>catch</c> aísla el fallo sin
    /// afectar a lo ya persistido ni a los cambios de otras entidades.
    /// </summary>
    void DiscardPendingChanges();
}
