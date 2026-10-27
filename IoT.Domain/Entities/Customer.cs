using IoT.Domain.Common;

namespace IoT.Domain.Entities;

/// <summary>
/// Nodo de la jerarquía multinivel <c>System → Tenant → Customer → Sub-Customers</c>
/// (ADR-013). Es <see cref="ITenantScoped"/> y auto-referenciado por <see cref="ParentCustomerId"/>:
/// el árbol es un bosque POR tenant (la RLS de tenant sigue aislando entre tenants; el subárbol
/// filtra dentro del tenant). El camino al nodo se materializa en <see cref="Path"/> (tipo
/// <c>ltree</c> en PostgreSQL) para que "pertenece al subárbol" sea un predicado indexado.
/// </summary>
public sealed class Customer : AggregateRoot<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public Guid? ParentCustomerId { get; private set; }
    public string Name { get; private set; }
    public string? Code { get; private set; }
    public string Path { get; private set; }
    public int Level { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private Customer(Guid id, Guid tenantId, string name) : base(id)
    {
        TenantId = tenantId;
        Name = name;
        Path = Label(id);
        Level = 0;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public static Result<Customer> Create(Guid tenantId, string name, Customer? parent = null, string? code = null)
    {
        if (tenantId == Guid.Empty)
            return Result<Customer>.Failure(new Error("Customer.InvalidTenant", "TenantId cannot be empty."));

        if (string.IsNullOrWhiteSpace(name))
            return Result<Customer>.Failure(new Error("Customer.InvalidName", "Customer name cannot be empty."));

        if (parent is not null && parent.TenantId != tenantId)
            return Result<Customer>.Failure(new Error("Customer.ParentTenantMismatch", "El customer padre pertenece a otro tenant."));

        var customer = new Customer(Guid.NewGuid(), tenantId, name.Trim())
        {
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim()
        };
        customer.AttachToParent(parent);
        return customer;
    }

    public static Result<Customer> CreateWithId(Guid id, Guid tenantId, string name, Customer? parent = null, string? code = null)
    {
        if (id == Guid.Empty)
            return Result<Customer>.Failure(new Error("Customer.InvalidId", "Customer ID cannot be empty."));

        if (tenantId == Guid.Empty)
            return Result<Customer>.Failure(new Error("Customer.InvalidTenant", "TenantId cannot be empty."));

        if (string.IsNullOrWhiteSpace(name))
            return Result<Customer>.Failure(new Error("Customer.InvalidName", "Customer name cannot be empty."));

        if (parent is not null && parent.TenantId != tenantId)
            return Result<Customer>.Failure(new Error("Customer.ParentTenantMismatch", "El customer padre pertenece a otro tenant."));

        var customer = new Customer(id, tenantId, name.Trim())
        {
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim()
        };
        customer.AttachToParent(parent);
        return customer;
    }

    /// <summary>
    /// (Re)ubica el nodo bajo <paramref name="parent"/>. Raíz (<c>parent == null</c>):
    /// <see cref="Path"/> = su propia etiqueta y <see cref="Level"/> = 0. Hijo: hereda el
    /// path del padre con su etiqueta anexada y <see cref="Level"/> = padre + 1.
    /// </summary>
    public void AttachToParent(Customer? parent)
    {
        if (parent is null)
        {
            ParentCustomerId = null;
            Level = 0;
            Path = Label(Id);
        }
        else
        {
            ParentCustomerId = parent.Id;
            Level = parent.Level + 1;
            Path = parent.Path + "." + Label(Id);
        }

        UpdatedAt = DateTime.UtcNow;
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Name = name.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Etiqueta <c>ltree</c> del nodo: <c>c</c> + los 32 hex del Id. El prefijo <c>c</c> hace
    /// que empiece por letra (una etiqueta <c>ltree</c> válida no puede empezar por dígito).
    /// </summary>
    private static string Label(Guid id) => "c" + id.ToString("N");
}
