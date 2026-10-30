namespace IoT.Domain.Common;

/// <summary>
/// Catálogo de permisos estándar para el RBAC fino de la plataforma (ADR-013, Decisión D3).
/// Estructurado por módulo y operación estilo ThingsBoard PE.
///
/// ESTADO REAL DE APLICACIÓN (auditoría RBAC fino, 2026-10-29; decisión de modelo "C-lean", 2026-10-30).
/// Hoy la autorización efectiva es POR ROL. De los 14 códigos de este catálogo, SÓLO 5 son
/// "load-bearing": los consume una política con nombre en <c>AuthorizationPolicies</c>/<c>Program.cs</c>
/// vía <c>HasPermissionOrRole</c> (permiso fino OR rol de fallback):
///   • <see cref="DevicesCommand"/>  → política DeviceCommand
///   • <see cref="DevicesManage"/>   → política DeviceManage
///   • <see cref="FirmwareManage"/>  → política FirmwareManage
///   • <see cref="CustomersManage"/> → política CustomerAdmin
///   • <see cref="SettingsManage"/>  → política TenantAdmin
/// Los otros 9 códigos (los marcados DECLARATIVO abajo) NO los evalúa ninguna política: no gatean
/// nada. Además NO existe superficie de runtime (endpoint/comando/UI) que ESCRIBA concesiones o
/// revocaciones por usuario (<c>UserPermission</c>) ni roles a medida: sólo el seeder crea
/// <c>RolePermissions</c>, y <c>CreateUserCommand</c> restringe el rol al conjunto cerrado
/// Admin/Operator/Viewer. En consecuencia, los permisos efectivos de un usuario son EXACTAMENTE
/// los de su rol, y como las 5 políticas hacen OR con esos mismos roles, el claim <c>permission</c>
/// nunca cambia por sí solo una decisión de autorización.
///
/// Este catálogo se conserva INTACTO a propósito (no se podan los 9 códigos): alimenta los
/// defaults por rol de <see cref="GetDefaultPermissionsForRole"/> y lo que siembra el seeder, de
/// modo que recortarlo cambiaría el contenido de los tokens sin ningún beneficio funcional. Hacer
/// cumplir los 9 códigos, o colapsar <c>HasPermissionOrRole</c> a rol puro, es una decisión mayor
/// aparte ("C-full") que NO se ha tomado. Hasta entonces, trátense los códigos DECLARATIVO como
/// reservados para una futura capa de permisos, no como controles vigentes.
/// </summary>
public static class StandardPermissions
{
    // Módulo: Dispositivos
    public const string DevicesRead = "devices.read";       // DECLARATIVO (no aplicado): lectura abierta a todo autenticado del tenant; la RLS acota los datos.
    public const string DevicesCreate = "devices.create";   // DECLARATIVO (no aplicado): el alta pasa por rol, no por este permiso.
    public const string DevicesUpdate = "devices.update";   // DECLARATIVO (no aplicado): la edición pasa por rol, no por este permiso.
    public const string DevicesDelete = "devices.delete";   // DECLARATIVO (no aplicado): la baja pasa por rol, no por este permiso.
    public const string DevicesCommand = "devices.command"; // APLICADO: política DeviceCommand (accionamiento físico).
    public const string DevicesManage = "devices.manage";   // APLICADO: política DeviceManage (registro/credenciales).

    // Módulo: Telemetría y Mapas
    public const string TelemetryRead = "telemetry.read";     // DECLARATIVO (no aplicado): lectura acotada por RLS, no por este permiso.
    public const string TelemetryExport = "telemetry.export"; // DECLARATIVO (no aplicado): la exportación no gatea por este permiso.
    public const string GpsRead = "gps.read";                 // DECLARATIVO (no aplicado): el mapa/rastreo no gatea por este permiso.
    public const string ReportsRead = "reports.read";         // DECLARATIVO (no aplicado): los reportes no gatean por este permiso.

    // Módulo: Firmware y OTA
    public const string FirmwareManage = "firmware.manage"; // APLICADO: política FirmwareManage (escritura de firmware).

    // Módulo: Clientes y Jerarquía Multinivel
    public const string CustomersManage = "customers.manage"; // APLICADO: política CustomerAdmin (jerarquía multinivel).

    // Módulo: Administración y Configuración
    public const string UsersManage = "users.manage";       // DECLARATIVO (no aplicado): UsersController gatea por TenantAdmin (settings.manage), no por este permiso.
    public const string SettingsManage = "settings.manage"; // APLICADO: política TenantAdmin (configuración del tenant).

    public static readonly IReadOnlyList<(string Code, string Name, string Module, string Description)> All = new[]
    {
        (DevicesRead, "Lectura de dispositivos", "Devices", "Ver dispositivos y telemetría básica"),
        (DevicesCreate, "Alta de dispositivos", "Devices", "Registrar nuevos dispositivos en la flota"),
        (DevicesUpdate, "Modificación de dispositivos", "Devices", "Editar metadatos y configuración de dispositivos"),
        (DevicesDelete, "Baja de dispositivos", "Devices", "Eliminar dispositivos de la plataforma"),
        (DevicesCommand, "Envío de comandos", "Devices", "Enviar comandos remotos a dispositivos (corte, reboot, etc.)"),
        (DevicesManage, "Gestión de dispositivos", "Devices", "Administración integral de dispositivos y credenciales"),
        (TelemetryRead, "Lectura de telemetría", "Telemetry", "Consultar datos y series de tiempo de telemetría"),
        (TelemetryExport, "Exportación de datos", "Telemetry", "Descargar reportes y exportar telemetría"),
        (GpsRead, "Monitoreo GPS", "Telemetry", "Visualización en mapa cartográfico y rastreo GPS"),
        (ReportsRead, "Lectura de reportes", "Telemetry", "Generar y consultar reportes operativos"),
        (FirmwareManage, "Gestión de Firmware OTA", "Administration", "Cargar y desplegar paquetes de firmware"),
        (CustomersManage, "Gestión de Clientes", "Administration", "Administrar sub-organizaciones de la jerarquía"),
        (UsersManage, "Gestión de Usuarios", "Administration", "Crear, invitar y gestionar usuarios"),
        (SettingsManage, "Configuración del Sistema", "Administration", "Configuración de brokers, llaves y políticas")
    };

    public static IReadOnlyList<string> GetDefaultPermissionsForRole(string roleName)
    {
        var normalized = (roleName ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "admin" => All.Select(p => p.Code).ToList(),
            "operator" => new[]
            {
                DevicesRead,
                DevicesCommand,
                TelemetryRead,
                TelemetryExport,
                GpsRead,
                ReportsRead
            },
            "viewer" => new[]
            {
                DevicesRead,
                TelemetryRead,
                GpsRead,
                ReportsRead
            },
            _ => new[] { DevicesRead }
        };
    }
}
