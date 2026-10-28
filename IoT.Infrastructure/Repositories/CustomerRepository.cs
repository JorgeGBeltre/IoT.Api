using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoT.Infrastructure.Repositories;

public sealed class CustomerRepository : ICustomerRepository
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;

    public CustomerRepository(ApplicationDbContext context, ITenantContext tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task<Customer?> GetByIdAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters: la resolución del nodo del usuario en el login ocurre en scope de
        // sistema, sin tenant/customer ambiental todavía (ADR-013 E8.2).
        return await _context.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken);
    }

    public async Task<IReadOnlyCollection<Guid>> GetSubtreeIdsAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Array.Empty<Guid>();

        // Scope de SISTEMA (ADR-013 E8.3b): el subárbol se calcula sin depender de la GUC
        // app.customer_path de la sesión ni de los filtros globales, y bajo Platform:Rls:Enforce no
        // se autobloquea. La consulta cruda keyless no está sujeta a los filtros de EF; el path se
        // pasa como parámetro ({0}) → sin inyección. "Id" AS "Value": SqlQueryRaw<Guid> espera la
        // columna escalar llamada "Value".
        using (_tenantContext.BeginSystemOperation())
        {
            var ids = await _context.Database
                .SqlQueryRaw<Guid>(@"SELECT ""Id"" AS ""Value"" FROM customers WHERE ""Path"" <@ {0}::ltree", path)
                .ToListAsync(cancellationToken);
            return ids;
        }
    }

    public async Task<Guid?> GetRootCustomerIdAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            return null;

        // Scope de SISTEMA (ADR-013 E8.4a): resolver el raíz sin depender de la GUC de customer de
        // la sesión (el alta puede correr acotada a un nodo) ni autobloquearse bajo
        // Platform:Rls:Enforce. IgnoreQueryFilters porque el filtro por subárbol ocultaría el raíz
        // cuando la sesión está acotada a un descendiente.
        using (_tenantContext.BeginSystemOperation())
        {
            return await _context.Customers
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId && c.ParentCustomerId == null)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
    }

    public async Task<Customer?> GetByIdScopedAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        // SIN IgnoreQueryFilters (a diferencia de GetByIdAsync): el filtro de EF acota al tenant y la
        // RLS de PostgreSQL acota al subárbol de la sesión (app.customer_path). Un nodo de otro
        // subárbol o tenant no es visible → null. Es la lectura segura para la capa de aplicación.
        return await _context.Customers
            .FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken);
    }

    public async Task<IReadOnlyList<Customer>> GetVisibleAsync(CancellationToken cancellationToken = default)
    {
        // SIN IgnoreQueryFilters: la RLS ya acota customers al subárbol de la sesión y el filtro de EF
        // al tenant. Devuelve la lista plana del subárbol visible (el frontend arma el árbol).
        return await _context.Customers
            .OrderBy(c => c.Level)
            .ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Customer>> GetDescendantsAsync(Guid tenantId, string parentPath, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(parentPath))
            return Array.Empty<Customer>();

        return await _context.Customers
            .FromSqlInterpolated($@"SELECT * FROM customers WHERE ""TenantId"" = {tenantId} AND ""Path"" <@ {parentPath}::ltree AND ""Path"" <> {parentPath}::ltree ORDER BY ""Level"" ASC")
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        await _context.Customers.AddAsync(customer, cancellationToken);
    }
}
