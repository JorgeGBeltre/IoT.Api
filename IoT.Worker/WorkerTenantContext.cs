using IoT.Application.Common.Interfaces;

namespace IoT.Worker;

public sealed class WorkerTenantContext : ITenantContext
{
    public Guid TenantId { get; set; } = Guid.Empty;

    /// <summary>
    /// El worker corre tareas de fondo cross-tenant: por defecto opera como sesión de sistema
    /// para que, bajo <c>Platform:Rls:Enforce</c>, sus consultas sin tenant no sean bloqueadas.
    /// </summary>
    public bool IsSystemOperation { get; set; } = true;
}
