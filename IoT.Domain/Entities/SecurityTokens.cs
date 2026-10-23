using System.Security.Cryptography;
using System.Text;
using IoT.Domain.Common;

namespace IoT.Domain.Entities;

public enum OtpPurpose
{
    PasswordReset = 1,
    LoginTwoFactor = 2,
    AccountVerification = 3
}

public sealed class OtpCode : Entity<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public Guid? UserId { get; private set; }
    public User? User { get; private set; }
    public string Email { get; private set; }
    public string CodeHash { get; private set; }
    public OtpPurpose Purpose { get; private set; }
    public DateTime ExpirationUtc { get; private set; }
    public bool IsUsed { get; private set; }
    public int AttemptsCount { get; private set; }
    public int MaxAttempts { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private OtpCode(
        Guid id,
        Guid tenantId,
        Guid? userId,
        string email,
        string codeHash,
        OtpPurpose purpose,
        DateTime expirationUtc,
        int maxAttempts = 3) : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        Email = email;
        CodeHash = codeHash;
        Purpose = purpose;
        ExpirationUtc = expirationUtc;
        MaxAttempts = maxAttempts;
        IsUsed = false;
        AttemptsCount = 0;
        CreatedAt = DateTime.UtcNow;
    }

    public static (OtpCode OtpEntity, string PlainCode) Generate(
        Guid tenantId,
        string email,
        Guid? userId = null,
        OtpPurpose purpose = OtpPurpose.PasswordReset,
        int expirationMinutes = 15)
    {
        // Generate secure 6-digit random code
        int codeInt = RandomNumberGenerator.GetInt32(100000, 1000000);
        string plainCode = codeInt.ToString("D6");
        string hash = HashCode(plainCode);

        var entity = new OtpCode(
            Guid.NewGuid(),
            tenantId,
            userId,
            email.Trim().ToLowerInvariant(),
            hash,
            purpose,
            DateTime.UtcNow.AddMinutes(expirationMinutes));

        return (entity, plainCode);
    }

    public static string HashCode(string code)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(code.Trim()));
        return Convert.ToHexString(bytes);
    }

    public bool Verify(string plainCode)
    {
        if (IsUsed || DateTime.UtcNow > ExpirationUtc || AttemptsCount >= MaxAttempts)
            return false;

        AttemptsCount++;
        string hash = HashCode(plainCode);
        if (CodeHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
        {
            IsUsed = true;
            return true;
        }

        return false;
    }
}

public sealed class PasswordResetToken : Entity<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public User? User { get; private set; }
    public string TokenHash { get; private set; }
    public DateTime ExpirationUtc { get; private set; }
    public bool IsUsed { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private PasswordResetToken(Guid id, Guid tenantId, Guid userId, string tokenHash, DateTime expirationUtc) : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        TokenHash = tokenHash;
        ExpirationUtc = expirationUtc;
        IsUsed = false;
        CreatedAt = DateTime.UtcNow;
    }

    public static (PasswordResetToken Entity, string RawToken) Create(Guid tenantId, Guid userId, int expirationMinutes = 30)
    {
        string rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var sha = SHA256.Create();
        string tokenHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));

        var entity = new PasswordResetToken(Guid.NewGuid(), tenantId, userId, tokenHash, DateTime.UtcNow.AddMinutes(expirationMinutes));
        return (entity, rawToken);
    }

    public bool Verify(string rawToken)
    {
        if (IsUsed || DateTime.UtcNow > ExpirationUtc)
            return false;

        using var sha = SHA256.Create();
        string hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(rawToken)));
        if (TokenHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
        {
            IsUsed = true;
            return true;
        }

        return false;
    }
}

public sealed class AuditLog : Entity<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public Guid? UserId { get; private set; }
    public string Username { get; private set; }
    public string Action { get; private set; } // "LoginSuccess", "LoginFailed", "PasswordReset", "CommandDispatched"
    public string Details { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public DateTime TimestampUtc { get; private set; }

    public AuditLog(
        Guid id,
        Guid tenantId,
        Guid? userId,
        string username,
        string action,
        string details,
        string? ipAddress,
        string? userAgent) : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        Username = username;
        Action = action;
        Details = details;
        IpAddress = ipAddress;
        UserAgent = userAgent;
        TimestampUtc = DateTime.UtcNow;
    }
}
