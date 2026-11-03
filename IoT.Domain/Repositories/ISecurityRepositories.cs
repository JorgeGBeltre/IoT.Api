using IoT.Domain.Entities;

namespace IoT.Domain.Repositories;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<User?> GetByIdSystemAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default);
    Task<User?> GetByUsernameAsync(Guid tenantId, string username, CancellationToken cancellationToken = default);
    Task<User?> GetByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken = default);
    Task<User?> FindByUsernameOrEmailGlobalAsync(string identifier, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<User>> GetByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task AddAsync(User user, CancellationToken cancellationToken = default);
    void Update(User user);
    void Delete(User user);
}

public interface IRoleRepository
{
    Task<Role?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Role?> GetByNameAsync(Guid tenantId, string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Role>> GetByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task AddAsync(Role role, CancellationToken cancellationToken = default);
}

public interface IOtpRepository
{
    Task AddAsync(OtpCode otp, CancellationToken cancellationToken = default);
    Task<OtpCode?> GetActiveOtpAsync(Guid tenantId, string email, OtpPurpose purpose, CancellationToken cancellationToken = default);
    Task AddResetTokenAsync(PasswordResetToken token, CancellationToken cancellationToken = default);
    Task<PasswordResetToken?> GetActiveResetTokenAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
}

public interface IAuditLogRepository
{
    Task AddAsync(AuditLog log, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditLog>> GetByTenantAsync(Guid tenantId, int limit = 100, CancellationToken cancellationToken = default);
}
