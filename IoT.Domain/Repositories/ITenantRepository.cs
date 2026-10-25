using IoT.Domain.Entities;

namespace IoT.Domain.Repositories;

public interface ITenantRepository
{
    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Tenant?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resuelve el tenant dueño de una credencial de bootstrap por el HASH de la clave
    /// presentada. Lo usa el broker en el CONNECT para acotar la sesión a un tenant. La
    /// entidad Tenant no es tenant-scoped, así que esta consulta no la filtra el filtro
    /// global por tenant (que, al correr el broker sin tenant ambiental, la dejaría vacía).
    /// Devuelve null si ningún tenant tiene esa credencial.
    /// </summary>
    Task<Tenant?> GetByBootstrapApiKeyHashAsync(string bootstrapApiKeyHash, CancellationToken cancellationToken = default);

    Task AddAsync(Tenant tenant, CancellationToken cancellationToken = default);
    void Update(Tenant tenant);
}
