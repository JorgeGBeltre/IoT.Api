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
    /// Lee de la configuración los roles que pueden gestionar firmware. Nunca devuelve una
    /// lista vacía: si la configuración está vacía o mal puesta, se cae al valor por defecto
    /// en vez de dejar la política sin requisitos (que la volvería permisiva).
    /// </summary>
    public static string[] ResolveFirmwareManageRoles(IConfiguration configuration)
    {
        var fromArray = configuration.GetSection("Authorization:FirmwareManageRoles").Get<string[]>();
        if (fromArray is { Length: > 0 })
        {
            var cleaned = fromArray
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .ToArray();
            if (cleaned.Length > 0) return cleaned;
        }

        var raw = configuration["Authorization:FirmwareManageRoles"];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var cleaned = raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            if (cleaned.Length > 0) return cleaned;
        }

        return DefaultFirmwareManageRoles;
    }

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
}
