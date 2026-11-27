using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoT.Infrastructure.Repositories;

public sealed class UserRepository : IUserRepository
{
    private readonly ApplicationDbContext _context;

    public UserRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Users
            .Include(u => u.Role)
            .Include(u => u.UserPermissions)
            .ThenInclude(up => up.Permission)
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
    }

    public async Task<User?> GetByIdSystemAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Users
            .IgnoreQueryFilters()
            .Include(u => u.Role)
            .Include(u => u.UserPermissions)
            .ThenInclude(up => up.Permission)
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == id, cancellationToken);
    }

    public async Task<User?> GetByUsernameAsync(Guid tenantId, string username, CancellationToken cancellationToken = default)
    {
        return await _context.Users
            .IgnoreQueryFilters()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Username.ToLower() == username.Trim().ToLower(), cancellationToken);
    }

    public async Task<User?> GetByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken = default)
    {
        return await _context.Users
            .IgnoreQueryFilters()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Email.ToLower() == email.Trim().ToLower(), cancellationToken);
    }

    public async Task<User?> FindByUsernameOrEmailGlobalAsync(string identifier, CancellationToken cancellationToken = default)
    {
        var normalized = identifier.Trim().ToLower();
        return await _context.Users
            .Include(u => u.Role)
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Username.ToLower() == normalized || u.Email.ToLower() == normalized, cancellationToken);
    }

    public async Task<IReadOnlyList<User>> GetByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        return await _context.Users
            .Include(u => u.Role)
            .Where(u => u.TenantId == tenantId)
            .OrderBy(u => u.Username)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        await _context.Users.AddAsync(user, cancellationToken);
    }

    public void Update(User user)
    {
        _context.Users.Update(user);
    }

    public void Delete(User user)
    {
        _context.Users.Remove(user);
    }
}

public sealed class RoleRepository : IRoleRepository
{
    private readonly ApplicationDbContext _context;

    public RoleRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Role?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Roles
            .Include(r => r.RolePermissions)
            .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<Role?> GetByNameAsync(Guid tenantId, string name, CancellationToken cancellationToken = default)
    {
        return await _context.Roles
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name.ToLower() == name.Trim().ToLower(), cancellationToken);
    }

    public async Task<IReadOnlyList<Role>> GetByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        return await _context.Roles
            .Where(r => r.TenantId == tenantId || r.IsSystemRole)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Role role, CancellationToken cancellationToken = default)
    {
        await _context.Roles.AddAsync(role, cancellationToken);
    }
}

public sealed class OtpRepository : IOtpRepository
{
    private readonly ApplicationDbContext _context;
    private readonly IoT.Application.Common.Interfaces.ITenantContext? _tenantContext;

    public OtpRepository(
        ApplicationDbContext context,
        IoT.Application.Common.Interfaces.ITenantContext? tenantContext = null)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task AddAsync(OtpCode otp, CancellationToken cancellationToken = default)
    {
        await _context.OtpCodes.AddAsync(otp, cancellationToken);
    }

    public async Task<OtpCode?> GetActiveOtpAsync(Guid tenantId, string email, OtpPurpose purpose, CancellationToken cancellationToken = default)
    {
        using var _ = IoT.Application.Common.Interfaces.TenantContextExtensions.BeginSystemOperationIfPresent(_tenantContext);
        var normalizedEmail = email.Trim().ToLowerInvariant();
        return await _context.OtpCodes
            .IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId && o.Email == normalizedEmail && o.Purpose == purpose && !o.IsUsed && o.ExpirationUtc > DateTime.UtcNow)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task AddResetTokenAsync(PasswordResetToken token, CancellationToken cancellationToken = default)
    {
        await _context.PasswordResetTokens.AddAsync(token, cancellationToken);
    }

    public async Task<PasswordResetToken?> GetActiveResetTokenAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        using var _ = IoT.Application.Common.Interfaces.TenantContextExtensions.BeginSystemOperationIfPresent(_tenantContext);
        return await _context.PasswordResetTokens
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.UserId == userId && !t.IsUsed && t.ExpirationUtc > DateTime.UtcNow)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }
}

public sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly ApplicationDbContext _context;

    public AuditLogRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(AuditLog log, CancellationToken cancellationToken = default)
    {
        await _context.AuditLogs.AddAsync(log, cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLog>> GetByTenantAsync(Guid tenantId, int limit = 100, CancellationToken cancellationToken = default)
    {
        return await _context.AuditLogs
            .Where(l => l.TenantId == tenantId)
            .OrderByDescending(l => l.TimestampUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
