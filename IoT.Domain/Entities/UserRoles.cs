namespace IoT.Domain.Entities;

/// <summary>
/// Roles de usuario ASIGNABLES en el alta en runtime (E8.6a). Conjunto CERRADO de roles de nivel
/// TENANT que un administrador (acotado o no) puede otorgar al crear un usuario:
/// <list type="bullet">
///   <item><description><see cref="Admin"/>: administra su subárbol (TenantAdmin/CustomerAdmin/FirmwareManage).</description></item>
///   <item><description><see cref="Operator"/>: opera la flota (DeviceManage/DeviceCommand).</description></item>
///   <item><description><see cref="Viewer"/>: sólo lectura.</description></item>
/// </list>
/// Coincide con los nombres que evalúan las <c>AuthorizationPolicies</c> de la API. NO incluye ningún
/// rol de plataforma/sistema: un rol fuera de este conjunto se RECHAZA en el alta, de modo que un
/// invitador acotado no pueda escalar privilegios asignando un rol desconocido o de nivel superior.
/// </summary>
public static class UserRoles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";

    /// <summary>Roles que el alta en runtime acepta, en su forma canónica.</summary>
    public static readonly IReadOnlyList<string> Assignable = new[] { Admin, Operator, Viewer };

    /// <summary>¿Es <paramref name="roleName"/> un rol asignable (sin distinguir mayúsculas)?</summary>
    public static bool IsAssignable(string? roleName) => Normalize(roleName) is not null;

    /// <summary>
    /// Devuelve el nombre CANÓNICO (misma caja que la constante) de un rol asignable, o <c>null</c>
    /// si no pertenece al conjunto. Normalizar la caja evita que el claim de rol del JWT quede como
    /// "admin"/"ADMIN" y mantiene coherente la comparación de las políticas.
    /// </summary>
    public static string? Normalize(string? roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName)) return null;
        var trimmed = roleName.Trim();
        foreach (var role in Assignable)
        {
            if (string.Equals(role, trimmed, StringComparison.OrdinalIgnoreCase))
                return role;
        }
        return null;
    }
}
