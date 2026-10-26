namespace IoT.Application.Common.Interfaces;

public interface ITenantContext
{
    Guid TenantId { get; set; }

    /// <summary>
    /// Marca la operación en curso como de SISTEMA (cross-tenant autorizada, ADR-003 D6). Cuando
    /// no hay <see cref="TenantId"/> resuelto, el interceptor RLS abre la conexión con
    /// <c>app.is_system_session = true</c> en vez de bloquear/lanzar bajo
    /// <c>Platform:Rls:Enforce</c>. Es para las rutas legítimas sin tenant: seeding, resolución
    /// global de dispositivos en la ingesta, autenticación del broker MQTT, resolución en los
    /// gateways TCP, servicios OTA de fondo, retención y el lookup global del login.
    /// </summary>
    bool IsSystemOperation { get; set; }
}

public static class TenantContextExtensions
{
    /// <summary>
    /// Activa <see cref="ITenantContext.IsSystemOperation"/> (y limpia el TenantId) hasta el
    /// <c>Dispose</c>, restaurando el estado previo. Para acotar una operación de sistema a un
    /// bloque concreto (p. ej. el lookup global del login) sin dejar el flag encendido después.
    /// </summary>
    public static IDisposable BeginSystemOperation(this ITenantContext tenantContext)
    {
        var previousTenant = tenantContext.TenantId;
        var previousSystem = tenantContext.IsSystemOperation;
        tenantContext.TenantId = Guid.Empty;
        tenantContext.IsSystemOperation = true;
        return new SystemOperationScope(() =>
        {
            tenantContext.TenantId = previousTenant;
            tenantContext.IsSystemOperation = previousSystem;
        });
    }

    private sealed class SystemOperationScope : IDisposable
    {
        private Action? _restore;
        public SystemOperationScope(Action restore) => _restore = restore;
        public void Dispose()
        {
            _restore?.Invoke();
            _restore = null;
        }
    }
}
