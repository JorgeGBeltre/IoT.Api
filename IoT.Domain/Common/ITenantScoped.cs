namespace IoT.Domain.Common;

/// <summary>
/// Contrato para entidades aisladas estrictamente por empresa/tenant (Data Plane Isolation).
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}
