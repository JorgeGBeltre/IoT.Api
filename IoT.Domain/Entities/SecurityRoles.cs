using IoT.Domain.Common;

namespace IoT.Domain.Entities;

public sealed class Role : Entity<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public string Name { get; private set; }
    public string? Description { get; private set; }
    public bool IsSystemRole { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public ICollection<RolePermission> RolePermissions { get; private set; } = new List<RolePermission>();
    public ICollection<User> Users { get; private set; } = new List<User>();

    private Role(Guid id, Guid tenantId, string name, string? description, bool isSystemRole) : base(id)
    {
        TenantId = tenantId;
        Name = name;
        Description = description;
        IsSystemRole = isSystemRole;
        CreatedAt = DateTime.UtcNow;
    }

    public static Result<Role> Create(Guid tenantId, string name, string? description = null, bool isSystemRole = false)
    {
        if (tenantId == Guid.Empty)
            return Result<Role>.Failure(new Error("Role.InvalidTenant", "TenantId cannot be empty."));

        if (string.IsNullOrWhiteSpace(name))
            return Result<Role>.Failure(new Error("Role.InvalidName", "Role name cannot be empty."));

        return new Role(Guid.NewGuid(), tenantId, name.Trim(), description?.Trim(), isSystemRole);
    }
}

public sealed class Permission : Entity<Guid>
{
    public string Code { get; private set; } // e.g. "devices.view", "devices.command", "telemetry.export"
    public string Name { get; private set; }
    public string Module { get; private set; } // "Devices", "Telemetry", "Fleet", "Administration"
    public string? Description { get; private set; }

    private Permission(Guid id, string code, string name, string module, string? description) : base(id)
    {
        Code = code;
        Name = name;
        Module = module;
        Description = description;
    }

    public static Result<Permission> Create(string code, string name, string module, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Result<Permission>.Failure(new Error("Permission.InvalidCode", "Permission code cannot be empty."));

        return new Permission(Guid.NewGuid(), code.Trim(), name.Trim(), module.Trim(), description?.Trim());
    }
}

public sealed class RolePermission : Entity<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public Guid RoleId { get; private set; }
    public Role? Role { get; private set; }

    public Guid PermissionId { get; private set; }
    public Permission? Permission { get; private set; }

    public RolePermission(Guid id, Guid tenantId, Guid roleId, Guid permissionId) : base(id)
    {
        TenantId = tenantId;
        RoleId = roleId;
        PermissionId = permissionId;
    }
}

public sealed class UserPermission : Entity<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public User? User { get; private set; }

    public Guid PermissionId { get; private set; }
    public Permission? Permission { get; private set; }
    public bool IsGranted { get; private set; } = true;

    public UserPermission(Guid id, Guid tenantId, Guid userId, Guid permissionId, bool isGranted = true) : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        PermissionId = permissionId;
        IsGranted = isGranted;
    }
}
