namespace IoT.Application.Common.Interfaces;

public interface ITenantContext
{
    Guid TenantId { get; set; }

    /// <summary>
    /// Nodo <c>Customer</c> al que pertenece el usuario autenticado (ADR-013), resuelto desde el
    /// claim <c>customer_id</c> del JWT verificado. <c>null</c> cuando no hay claim (token antiguo)
    /// o la petición no está autenticada: en ese caso NO se restringe por subárbol (equivale al
    /// raíz, comportamiento NO DISRUPTIVO).
    /// </summary>
    Guid? CustomerId { get; set; }

    /// <summary>
    /// Path <c>ltree</c> del <see cref="CustomerId"/> del usuario, resuelto EN EL SERVIDOR desde la
    /// tabla <c>customers</c> (nunca se confía al cliente). Define el subárbol visible: una fila es
    /// visible si su propio path desciende-o-es-igual de este. <c>null</c> o vacío = SIN restricción
    /// de subárbol dentro del tenant (raíz / operación de sistema / token antiguo).
    /// </summary>
    string? CustomerPath { get; set; }

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
        var previousCustomerId = tenantContext.CustomerId;
        var previousCustomerPath = tenantContext.CustomerPath;
        tenantContext.TenantId = Guid.Empty;
        tenantContext.IsSystemOperation = true;
        // Una operación de sistema no se acota a ningún subárbol: sin path → el filtro EF y la RLS
        // de customer no restringen (ADR-013).
        tenantContext.CustomerId = null;
        tenantContext.CustomerPath = null;
        return new SystemOperationScope(() =>
        {
            tenantContext.TenantId = previousTenant;
            tenantContext.IsSystemOperation = previousSystem;
            tenantContext.CustomerId = previousCustomerId;
            tenantContext.CustomerPath = previousCustomerPath;
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
