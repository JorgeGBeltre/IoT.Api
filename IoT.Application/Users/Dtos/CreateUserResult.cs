namespace IoT.Application.Users.Dtos;

/// <summary>
/// Resultado del alta de usuario en runtime (E8.6a). Incluye la contraseña TEMPORAL en texto plano,
/// que se muestra UNA sola vez a quien crea el usuario (mismo patrón que la ApiKey de dispositivo y la
/// credencial de bootstrap). A partir de ahí la clave sólo existe como hash BCrypt; el usuario debe
/// cambiarla en el primer inicio de sesión (<see cref="MustChangePassword"/> = <c>true</c>, flujo
/// ChangeTemporaryPassword). <see cref="TemporaryPassword"/> NUNCA se persiste, registra ni se vuelve a
/// exponer: si se pierde, hay que rotarla.
/// </summary>
public sealed record CreateUserResult(
    Guid Id,
    string Username,
    string Email,
    string FullName,
    string RoleName,
    Guid CustomerId,
    bool MustChangePassword,
    string TemporaryPassword,
    string? InvitationLink = null);
