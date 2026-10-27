using IoT.Application.Common.Interfaces;

namespace IoT.Worker;

public sealed class WorkerTenantContext : ITenantContext
{
    public Guid TenantId { get; set; } = Guid.Empty;

    /// <summary>
    /// El worker no se acota a ningún subárbol de customer: corre tareas de fondo cross-tenant y
    /// como sesión de sistema. Siempre <c>null</c> → sin restricción de subárbol (ADR-013).
    /// </summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Sin path → sin restricción de subárbol (ADR-013). Ver <see cref="CustomerId"/>.</summary>
    public string? CustomerPath { get; set; }

    /// <summary>
    /// El worker nunca se acota a un subárbol (corre cross-tenant / como sesión de sistema), así que
    /// no resuelve descendientes: siempre <c>null</c> → el filtro EF no recorta (ADR-013 E8.3b).
    /// </summary>
    public IReadOnlyCollection<Guid>? CustomerSubtreeIds { get; set; }

    /// <summary>
    /// El worker corre tareas de fondo cross-tenant: por defecto opera como sesión de sistema
    /// para que, bajo <c>Platform:Rls:Enforce</c>, sus consultas sin tenant no sean bloqueadas.
    /// </summary>
    public bool IsSystemOperation { get; set; } = true;
}
