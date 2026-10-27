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
}
