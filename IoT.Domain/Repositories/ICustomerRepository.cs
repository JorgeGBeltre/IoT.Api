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

    /// <summary>
    /// Devuelve el <c>Id</c> del <see cref="Customer"/> RAÍZ del tenant (<c>ParentCustomerId</c>
    /// nulo), o <c>null</c> si el tenant aún no tiene raíz. Se resuelve en SCOPE DE SISTEMA
    /// (<c>IgnoreQueryFilters</c>) para no depender del contexto de customer de la sesión ni
    /// autobloquearse bajo <c>Platform:Rls:Enforce</c>. Es el nodo por defecto al que se asigna un
    /// <see cref="Device"/> o <see cref="User"/> recién creado cuando la sesión no acota a un nodo
    /// concreto (ADR-013 E8.4a): así NUNCA se persiste una fila con <c>CustomerId</c> nulo.
    /// </summary>
    Task<Guid?> GetRootCustomerIdAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Carga un <see cref="Customer"/> por su Id RESPETANDO los filtros globales (operación de la
    /// capa de aplicación, ADR-013 E8.4b): el filtro de EF acota al tenant y la RLS de PostgreSQL
    /// (<c>tenant_isolation_customers</c> por <c>app.customer_path</c>) acota al SUBÁRBOL de la
    /// sesión. Por tanto una sesión acotada a un subárbol recibe <c>null</c> para un nodo de otro
    /// subárbol (p. ej. un hermano) o de otro tenant: es la vía segura para resolver el padre de un
    /// alta o el nodo destino de una reasignación. Contrasta con <see cref="GetByIdAsync"/>, que
    /// ignora los filtros para el login en scope de sistema.
    /// </summary>
    Task<Customer?> GetByIdScopedAsync(Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve los <see cref="Customer"/> VISIBLES para la sesión actual (su subárbol) como lista
    /// plana, RESPETANDO los filtros globales: el de EF por tenant y la RLS por subárbol. El cliente
    /// arma el árbol a partir de <c>ParentCustomerId</c>. Ordenada por <c>Level</c> y <c>Name</c> para
    /// una presentación estable.
    /// </summary>
    Task<IReadOnlyList<Customer>> GetVisibleAsync(CancellationToken cancellationToken = default);

    /// <summary>Añade un nuevo <see cref="Customer"/> al contexto (se persiste con el UnitOfWork).</summary>
    Task AddAsync(Customer customer, CancellationToken cancellationToken = default);
}
