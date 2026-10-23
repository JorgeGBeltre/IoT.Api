using IoT.Domain.Common;

namespace IoT.Domain.Entities;

public sealed class Tenant : AggregateRoot<Guid>
{
    public string Name { get; private set; }
    public string Code { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private Tenant(Guid id, string name, string code) : base(id)
    {
        Name = name;
        Code = code;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public static Result<Tenant> Create(string name, string code)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result<Tenant>.Failure(new Error("Tenant.InvalidName", "Tenant name cannot be empty."));

        if (string.IsNullOrWhiteSpace(code))
            return Result<Tenant>.Failure(new Error("Tenant.InvalidCode", "Tenant code cannot be empty."));

        return new Tenant(Guid.NewGuid(), name.Trim(), code.Trim().ToUpperInvariant());
    }

    public static Result<Tenant> CreateWithId(Guid id, string name, string code)
    {
        if (id == Guid.Empty)
            return Result<Tenant>.Failure(new Error("Tenant.InvalidId", "Tenant ID cannot be empty."));

        if (string.IsNullOrWhiteSpace(name))
            return Result<Tenant>.Failure(new Error("Tenant.InvalidName", "Tenant name cannot be empty."));

        if (string.IsNullOrWhiteSpace(code))
            return Result<Tenant>.Failure(new Error("Tenant.InvalidCode", "Tenant code cannot be empty."));

        return new Tenant(id, name.Trim(), code.Trim().ToUpperInvariant());
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
}
