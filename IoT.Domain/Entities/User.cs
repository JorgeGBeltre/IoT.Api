using IoT.Domain.Common;
using IoT.Domain.Enums;

namespace IoT.Domain.Entities;

public sealed class User : AggregateRoot<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public string Username { get; private set; }
    public string Email { get; private set; }
    public string PasswordHash { get; private set; }
    public string FullName { get; private set; }
    public bool IsActive { get; private set; }
    public bool MustChangePassword { get; private set; }
    public int FailedLoginAttempts { get; private set; }
    public DateTime? LockoutEndUtc { get; private set; }
    public string SecurityStamp { get; private set; }
    public Guid? RoleId { get; private set; }
    public Role? Role { get; private set; }
    public string RoleName { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    public ICollection<UserPermission> UserPermissions { get; private set; } = new List<UserPermission>();

    private User(
        Guid id,
        Guid tenantId,
        string username,
        string email,
        string passwordHash,
        string fullName,
        string roleName,
        bool mustChangePassword = false) : base(id)
    {
        TenantId = tenantId;
        Username = username;
        Email = email;
        PasswordHash = passwordHash;
        FullName = fullName;
        RoleName = roleName;
        IsActive = true;
        MustChangePassword = mustChangePassword;
        FailedLoginAttempts = 0;
        SecurityStamp = Guid.NewGuid().ToString("N");
        CreatedAt = DateTime.UtcNow;
    }

    public static Result<User> Create(
        Guid tenantId,
        string username,
        string email,
        string passwordHash,
        string fullName,
        string roleName = "Operator",
        bool mustChangePassword = false)
    {
        if (tenantId == Guid.Empty)
            return Result<User>.Failure(new Error("User.InvalidTenant", "TenantId cannot be empty."));

        if (string.IsNullOrWhiteSpace(username))
            return Result<User>.Failure(new Error("User.InvalidUsername", "Username cannot be empty."));

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return Result<User>.Failure(new Error("User.InvalidEmail", "Valid email address is required."));

        if (string.IsNullOrWhiteSpace(passwordHash))
            return Result<User>.Failure(new Error("User.InvalidPasswordHash", "Password hash cannot be empty."));

        return new User(
            Guid.NewGuid(),
            tenantId,
            username.Trim(),
            email.Trim().ToLowerInvariant(),
            passwordHash,
            string.IsNullOrWhiteSpace(fullName) ? username.Trim() : fullName.Trim(),
            string.IsNullOrWhiteSpace(roleName) ? "Operator" : roleName.Trim(),
            mustChangePassword);
    }

    public void AssignRole(Role role)
    {
        RoleId = role.Id;
        Role = role;
        RoleName = role.Name;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdatePassword(string newPasswordHash)
    {
        PasswordHash = newPasswordHash;
        MustChangePassword = false;
        FailedLoginAttempts = 0;
        LockoutEndUtc = null;
        SecurityStamp = Guid.NewGuid().ToString("N");
        UpdatedAt = DateTime.UtcNow;
    }

    public void RecordSuccessfulLogin()
    {
        FailedLoginAttempts = 0;
        LockoutEndUtc = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void RecordFailedLogin(int maxAttempts = 5, int lockoutMinutes = 15)
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= maxAttempts)
        {
            LockoutEndUtc = DateTime.UtcNow.AddMinutes(lockoutMinutes);
        }
        UpdatedAt = DateTime.UtcNow;
    }

    public bool IsLockedOut => LockoutEndUtc.HasValue && LockoutEndUtc.Value > DateTime.UtcNow;

    public void Deactivate()
    {
        IsActive = false;
        SecurityStamp = Guid.NewGuid().ToString("N");
        UpdatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }
}
