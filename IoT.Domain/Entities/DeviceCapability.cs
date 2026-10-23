using IoT.Domain.Common;
using IoT.Domain.Enums;

namespace IoT.Domain.Entities;

public sealed class DeviceCapability : Entity<Guid>
{
    public Guid DeviceId { get; private set; }
    public CapabilityType CapabilityType { get; private set; }
    public bool IsEnabled { get; private set; }
    public string ConfigurationJson { get; private set; }
    public DateTime CreatedAt { get; private set; }

    internal DeviceCapability(Guid id, Guid deviceId, CapabilityType capabilityType, string? configurationJson = null) : base(id)
    {
        DeviceId = deviceId;
        CapabilityType = capabilityType;
        IsEnabled = true;
        ConfigurationJson = string.IsNullOrWhiteSpace(configurationJson) ? "{}" : configurationJson;
        CreatedAt = DateTime.UtcNow;
    }

    public void Enable() => IsEnabled = true;
    public void Disable() => IsEnabled = false;

    public void UpdateConfiguration(string newConfigJson)
    {
        ConfigurationJson = string.IsNullOrWhiteSpace(newConfigJson) ? "{}" : newConfigJson;
    }
}
