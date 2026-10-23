using IoT.Application.Common.Interfaces;

namespace IoT.Worker;

public sealed class WorkerTenantContext : ITenantContext
{
    public Guid TenantId { get; set; } = Guid.Empty;
}
