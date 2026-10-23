using IoT.Domain.Common;

namespace IoT.Domain.Entities;

public sealed class DeviceGroup : Entity<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public string Name { get; private set; }
    public string? Description { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private DeviceGroup(Guid id, Guid tenantId, string name, string? description) : base(id)
    {
        TenantId = tenantId;
        Name = name;
        Description = description;
        CreatedAt = DateTime.UtcNow;
    }

    public static Result<DeviceGroup> Create(Guid tenantId, string name, string? description = null)
    {
        if (tenantId == Guid.Empty)
            return Result<DeviceGroup>.Failure(new Error("DeviceGroup.InvalidTenant", "TenantId cannot be empty."));

        if (string.IsNullOrWhiteSpace(name))
            return Result<DeviceGroup>.Failure(new Error("DeviceGroup.InvalidName", "Group name cannot be empty."));

        return new DeviceGroup(Guid.NewGuid(), tenantId, name.Trim(), description?.Trim());
    }
}
