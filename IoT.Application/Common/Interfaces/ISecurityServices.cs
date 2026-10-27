using IoT.Domain.Entities;

namespace IoT.Application.Common.Interfaces;

public interface IEmailService
{
    Task<bool> SendTemporaryPasswordEmailAsync(
        string toEmail,
        string username,
        string tempPassword,
        string loginUrl,
        string? companyName = null,
        string? fullName = null,
        CancellationToken cancellationToken = default);

    Task<bool> SendPasswordResetOtpEmailAsync(
        string toEmail,
        string username,
        string otpCode,
        CancellationToken cancellationToken = default);

    Task<bool> SendPasswordChangedNotificationEmailAsync(
        string toEmail,
        string username,
        string? deviceInfo = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);

    Task<bool> SendSecurityAlertEmailAsync(
        string toEmail,
        string username,
        string eventName,
        string? deviceInfo = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
    void ValidateComplexity(string password);
}

public sealed record AuthTokenResult(
    string Token,
    Guid TenantId,
    Guid UserId,
    string Username,
    string Email,
    string Role,
    bool MustChangePassword,
    DateTime ExpiresAt)
{
    public string RoleName => Role;
}

public interface IJwtTokenGenerator
{
    /// <summary>
    /// Emite el JWT de sesión. <paramref name="customerId"/>/<paramref name="customerPath"/> son el
    /// nodo del usuario en la jerarquía (ADR-013), resueltos en el servidor por el handler de login.
    /// Si <paramref name="customerId"/> es <c>null</c> se OMITEN ambos claims (token sin acotación de
    /// subárbol = ve todo su tenant; comportamiento NO DISRUPTIVO).
    /// </summary>
    AuthTokenResult GenerateToken(User user, Guid tenantId, Guid? customerId = null, string? customerPath = null);
}
