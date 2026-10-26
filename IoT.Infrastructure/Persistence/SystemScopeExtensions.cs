using IoT.Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace IoT.Infrastructure.Persistence;

public static class SystemScopeExtensions
{
    /// <summary>
    /// Crea un scope de DI y marca su <see cref="ITenantContext"/> como operación de SISTEMA.
    /// Para los servicios de fondo que acceden a la base de datos sin tenant ambiental (broker
    /// MQTT, gateways TCP, servicios OTA): bajo <c>Platform:Rls:Enforce</c> el interceptor
    /// permitirá la lectura/escritura cross-tenant en vez de bloquear la conexión. ADR-003 D6.
    ///
    /// Las escrituras que deban quedar acotadas a un tenant concreto fijan después
    /// <see cref="ITenantContext.TenantId"/> (lo que desactiva la sesión de sistema para esa
    /// conexión), como hace el pipeline de ingesta tras resolver el dispositivo.
    /// </summary>
    public static IServiceScope CreateSystemScope(this IServiceScopeFactory scopeFactory)
    {
        var scope = scopeFactory.CreateScope();
        var tenantContext = scope.ServiceProvider.GetService<ITenantContext>();
        if (tenantContext is not null)
        {
            tenantContext.IsSystemOperation = true;
        }
        return scope;
    }

    /// <summary>
    /// Crea un scope de DI y fija su <see cref="ITenantContext.TenantId"/> a un tenant concreto.
    /// Para tareas de fondo que operan sobre UN solo tenant conocido (p. ej. la transmisión OTA
    /// a los dispositivos de un tenant): a diferencia de <see cref="CreateSystemScope"/>, mantiene
    /// la RLS forzada a ese tenant (las lecturas/escrituras cross-tenant quedan bloqueadas),
    /// en vez de abrir una sesión de sistema sin filtro.
    /// </summary>
    public static IServiceScope CreateTenantScope(this IServiceScopeFactory scopeFactory, Guid tenantId)
    {
        var scope = scopeFactory.CreateScope();
        var tenantContext = scope.ServiceProvider.GetService<ITenantContext>();
        if (tenantContext is not null)
        {
            tenantContext.TenantId = tenantId;
            tenantContext.IsSystemOperation = false;
        }
        return scope;
    }
}
