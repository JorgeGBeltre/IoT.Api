namespace IoT.Domain.Common;

/// <summary>
/// Contrato para entidades cuya visibilidad se acota al SUBÁRBOL del nodo <c>Customer</c> del
/// usuario (ADR-013). Es ortogonal a <see cref="ITenantScoped"/>: el aislamiento por tenant
/// (ADR-003) se preserva intacto y el subárbol filtra DENTRO del tenant.
///
/// <para><see cref="CustomerId"/> es nullable: una fila sin nodo asignado se considera visible
/// dentro del tenant (no se oculta). El caso "sin path de customer" (raíz, operación de sistema
/// o token antiguo sin el claim) NO aplica ninguna restricción de subárbol — equivale a ver todo
/// el tenant — para que la activación sea NO DISRUPTIVA.</para>
/// </summary>
public interface ICustomerScoped
{
    Guid? CustomerId { get; }
}
