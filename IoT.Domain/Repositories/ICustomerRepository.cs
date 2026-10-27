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
}
