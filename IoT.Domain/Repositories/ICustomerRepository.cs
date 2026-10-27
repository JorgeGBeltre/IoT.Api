using IoT.Domain.Entities;

namespace IoT.Domain.Repositories;

public interface ICustomerRepository
{
    /// <summary>
    /// Carga un <see cref="Customer"/> por su Id IGNORANDO los filtros globales (operación de
    /// sistema). Se usa en el login para resolver el <c>Path</c> del nodo del usuario ANTES de que
    /// exista contexto de tenant/customer resuelto. Devuelve <c>null</c> si no existe.
    /// </summary>
    Task<Customer?> GetByIdAsync(Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve los <c>Id</c> del SUBÁRBOL de <paramref name="path"/> (customers cuyo <c>Path</c>
    /// desciende-o-es-igual de él, <c>&lt;@</c>), resueltos en SCOPE DE SISTEMA para no depender de
    /// la GUC de sesión ni de los filtros globales, y para no autobloquearse bajo
    /// <c>Platform:Rls:Enforce</c> (ADR-013 E8.3b). Lo consume el
    /// <c>TenantResolutionMiddleware</c> una vez por request y lo guarda en
    /// <c>ITenantContext.CustomerSubtreeIds</c>, que a su vez alimenta el filtro global de EF (en
    /// lugar de ejecutar SQL síncrono dentro del filtro).
    /// </summary>
    Task<IReadOnlyCollection<Guid>> GetSubtreeIdsAsync(string path, CancellationToken cancellationToken = default);
}
