namespace IoT.Domain.Common;

/// <summary>
/// Catálogo de permisos estándar para el RBAC fino de la plataforma (ADR-013, Decisión D3).
/// Estructurado por módulo y operación estilo ThingsBoard PE.
/// </summary>
public static class StandardPermissions
{
    // Módulo: Dispositivos
    public const string DevicesRead = "devices.read";
    public const string DevicesCreate = "devices.create";
    public const string DevicesUpdate = "devices.update";
    public const string DevicesDelete = "devices.delete";
    public const string DevicesCommand = "devices.command";
    public const string DevicesManage = "devices.manage";

    // Módulo: Telemetría y Mapas
    public const string TelemetryRead = "telemetry.read";
    public const string TelemetryExport = "telemetry.export";
    public const string GpsRead = "gps.read";
    public const string ReportsRead = "reports.read";

    // Módulo: Firmware y OTA
    public const string FirmwareManage = "firmware.manage";

    // Módulo: Clientes y Jerarquía Multinivel
    public const string CustomersManage = "customers.manage";

    // Módulo: Administración y Configuración
    public const string UsersManage = "users.manage";
    public const string SettingsManage = "settings.manage";

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
