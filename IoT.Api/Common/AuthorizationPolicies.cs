namespace IoT.Api.Common;

/// <summary>
/// Políticas de autorización con nombre.
///
/// Hasta ahora la API no tenía NINGUNA: las 15 anotaciones <c>[Authorize]</c> eran desnudas y
/// la política de respaldo sólo exigía estar autenticado, de modo que cualquier usuario del
/// tenant —incluido el perfil más bajo, que es el que crea <c>User.Create</c> por defecto
/// ("Operator")— podía subir un binario arbitrario y flashear la flota completa. El claim de
/// rol se emitía en cada token (<c>ClaimTypes.Role</c> en JwtTokenGenerator) y no se evaluaba
/// en ningún sitio.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Operaciones que escriben firmware en hardware real: subir una imagen, lanzar o
    /// revertir una actualización. Son físicamente destructivas e irreversibles, así que
    /// exigen un rol explícito, no sólo sesión válida.
    /// </summary>
    public const string FirmwareManage = "FirmwareManage";

    /// <summary>
    /// Roles admitidos por defecto para <see cref="FirmwareManage"/>. Es el rol con el que se
    /// siembran los administradores; se puede ampliar por configuración sin tocar código con
    /// <c>Authorization:FirmwareManageRoles</c> (array o lista separada por comas).
    /// </summary>
    public static readonly string[] DefaultFirmwareManageRoles = { "Admin" };

    /// <summary>
    /// Enviar un comando a un dispositivo o confirmar su resultado. También es accionamiento
    /// físico —apagar un relé por API no debería requerir sólo una sesión válida— pero es la
    /// operación del DÍA A DÍA, así que su conjunto de roles por defecto es más amplio que el
    /// de firmware: flashear es excepcional y irreversible, mandar un comando es rutina.
    /// </summary>
    public const string DeviceCommand = "DeviceCommand";

    /// <summary>
    /// Roles admitidos por defecto para <see cref="DeviceCommand"/>. Incluye "Operator",
    /// que es el rol por defecto de <c>User.Create</c> y el perfil que opera la flota;
    /// dejarlo fuera rompería el uso normal del producto. Los perfiles de sólo consulta
    /// quedan excluidos. Ampliable con <c>Authorization:DeviceCommandRoles</c>.
    /// </summary>
    public static readonly string[] DefaultDeviceCommandRoles = { "Admin", "Operator" };

    /// <summary>
    /// Lee de la configuración los roles de una política. Nunca devuelve una lista vacía: si
    /// la configuración está vacía o mal puesta, se cae al valor por defecto en vez de dejar
    /// la política sin requisitos (que la volvería permisiva).
    ///
    /// Admite las dos formas de escribirlo: array JSON y cadena separada por comas.
    /// </summary>
    public static string[] ResolveRoles(IConfiguration configuration, string key, string[] defaults)
    {
        var fromArray = configuration.GetSection(key).Get<string[]>();
        if (fromArray is { Length: > 0 })
        {
            var cleaned = fromArray
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .ToArray();
            if (cleaned.Length > 0) return cleaned;
        }

        var raw = configuration[key];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var cleaned = raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            if (cleaned.Length > 0) return cleaned;
        }

        return defaults;
    }

    /// <summary>
    /// Operaciones de gestión de dispositivos: registrar dispositivos y leer/rotar credenciales.
    /// Permitido a administradores y operadores de flota.
    /// </summary>
    public const string DeviceManage = "DeviceManage";

    /// <summary>
    /// Roles admitidos por defecto para <see cref="DeviceManage"/>.
    /// </summary>
    public static readonly string[] DefaultDeviceManageRoles = { "Admin", "Operator" };

    /// <summary>
    /// Operaciones administrativas del tenant: configuración general, brokers MQTT,
    /// usuarios y registro de auditoría. Exclusivo de administradores del tenant.
    /// </summary>
    public const string TenantAdmin = "TenantAdmin";

    /// <summary>
    /// Roles admitidos por defecto para <see cref="TenantAdmin"/>.
    /// </summary>
    public static readonly string[] DefaultTenantAdminRoles = { "Admin" };

    /// <summary>
    /// Operar la jerarquía multinivel Customer (ADR-013, E8.4b): crear/renombrar/activar sub-customers
    /// y reasignar el nodo dueño de un dispositivo.
    ///
    /// IMPORTANTE — esta política controla SÓLO el ROL, no el alcance. El SCOPING por subárbol lo
    /// aplica AUTOMÁTICAMENTE la Row-Level Security de PostgreSQL (política
    /// <c>tenant_isolation_customers</c> por la GUC <c>app.customer_path</c>) y, para Device/User, el
    /// filtro global de EF por <c>CustomerSubtreeIds</c>. Es decir: un <c>CustomerAdmin</c> acotado a un
    /// subárbol sólo ve y sólo puede escribir dentro de ese subárbol aunque la política de rol sea la
    /// misma para todos los administradores; no hay que —ni se debe— recodificar ese recorte aquí. La
    /// política únicamente impide que un perfil de sólo lectura toque la jerarquía.
    /// </summary>
    public const string CustomerAdmin = "CustomerAdmin";

    /// <summary>
    /// Roles admitidos por defecto para <see cref="CustomerAdmin"/>. Es el rol con el que se siembran
    /// los administradores; ampliable sin tocar código con <c>Authorization:CustomerAdminRoles</c>.
    /// </summary>
    public static readonly string[] DefaultCustomerAdminRoles = { "Admin" };

    public static string[] ResolveDeviceManageRoles(IConfiguration configuration) =>
        ResolveRoles(configuration, "Authorization:DeviceManageRoles", DefaultDeviceManageRoles);

    public static string[] ResolveTenantAdminRoles(IConfiguration configuration) =>
        ResolveRoles(configuration, "Authorization:TenantAdminRoles", DefaultTenantAdminRoles);

    public static string[] ResolveCustomerAdminRoles(IConfiguration configuration) =>
        ResolveRoles(configuration, "Authorization:CustomerAdminRoles", DefaultCustomerAdminRoles);

    public static string[] ResolveFirmwareManageRoles(IConfiguration configuration) =>
        ResolveRoles(configuration, "Authorization:FirmwareManageRoles", DefaultFirmwareManageRoles);

    public static string[] ResolveDeviceCommandRoles(IConfiguration configuration) =>
        ResolveRoles(configuration, "Authorization:DeviceCommandRoles", DefaultDeviceCommandRoles);

    /// <summary>
    /// ¿Tiene el usuario alguno de los roles indicados?
    ///
    /// Se compara SIN distinguir mayúsculas a propósito: el nombre del rol es una cadena
    /// libre que escribe quien crea el usuario, y <c>RequireRole</c> del framework compara el
    /// VALOR del claim de forma sensible a la caja, de modo que un usuario guardado como
    /// "admin" quedaba denegado aunque fuese administrador. No se recorta el valor: un
    /// "admin " con espacio es otro dato almacenado y debe corregirse en el registro, no
    /// colarse en una decisión de seguridad.
    ///
    /// Se miran los tres tipos de claim habituales porque el nombre depende de si el token lo
    /// mapeó el handler JWT o se construyó a mano.
    /// </summary>
    public static bool HasAnyRole(System.Security.Claims.ClaimsPrincipal user, string[] roles)
    {
        if (roles.Length == 0) return false;

        foreach (var claim in user.Claims)
        {
            bool isRoleClaim =
                claim.Type == System.Security.Claims.ClaimTypes.Role ||
                claim.Type == "role" ||
                claim.Type == "roles";

            if (isRoleClaim && roles.Contains(claim.Value, StringComparer.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// ¿Tiene el usuario el permiso fino requerido (claim "permission") o alguno de los roles de fallback?
    /// Permite adopción gradual de RBAC fino (decisión D3) preservando compatibilidad absoluta.
    /// </summary>
    public static bool HasPermissionOrRole(System.Security.Claims.ClaimsPrincipal user, string permissionCode, string[] fallbackRoles)
    {
        // 1. Verificación directa de permiso fino en claims del token
        foreach (var claim in user.Claims)
        {
            if ((claim.Type == "permission" || claim.Type == "permissions") &&
                string.Equals(claim.Value, permissionCode, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 2. Fallback a roles tradicionales por nombre
        return HasAnyRole(user, fallbackRoles);
    }
}
