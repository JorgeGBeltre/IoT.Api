using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Services;

/// <summary>
/// Servicio de evaluación de permisos para RBAC fino (ADR-013, Decisión D3).
/// Resuelve los permisos del rol en BD (<see cref="RolePermission"/>) y aplica los overrides
/// específicos del usuario (<see cref="UserPermission"/>).
/// </summary>
public sealed class PermissionService : IPermissionService
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<PermissionService> _logger;

    public PermissionService(
        ApplicationDbContext context,
        ITenantContext tenantContext,
        ILogger<PermissionService> logger)
    {
        _context = context;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(
        Guid tenantId,
        Guid userId,
        string roleName,
        CancellationToken cancellationToken = default)
    {
        using (_tenantContext.BeginSystemOperation())
        {
            var permissionsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Cargar permisos asociados al rol del usuario en la base de datos
            var rolePermissions = await _context.RolePermissions
                .IgnoreQueryFilters()
                .Include(rp => rp.Role)
                .Include(rp => rp.Permission)
                .Where(rp => (rp.TenantId == tenantId || (rp.Role != null && rp.Role.IsSystemRole))
                             && rp.Role != null
                             && rp.Role.Name.ToLower() == roleName.Trim().ToLower()
                             && rp.Permission != null)
                .Select(rp => rp.Permission!.Code)
                .ToListAsync(cancellationToken);

            foreach (var code in rolePermissions)
            {
                if (!string.IsNullOrWhiteSpace(code))
                    permissionsSet.Add(code);
            }

            // Si el rol en BD no tiene permisos sembrados o aún está en migración, aplicar el fallback por defecto
            if (permissionsSet.Count == 0)
            {
                var defaults = StandardPermissions.GetDefaultPermissionsForRole(roleName);
                foreach (var code in defaults)
                {
                    permissionsSet.Add(code);
                }
            }

            // 2. Aplicar overrides específicos de UserPermission para este usuario (otorgar o revocar)
            var userOverrides = await _context.UserPermissions
                .IgnoreQueryFilters()
                .Include(up => up.Permission)
                .Where(up => up.UserId == userId && up.TenantId == tenantId && up.Permission != null)
                .ToListAsync(cancellationToken);

            foreach (var userPerm in userOverrides)
            {
                if (userPerm.Permission is null) continue;
                if (userPerm.IsGranted)
                {
                    permissionsSet.Add(userPerm.Permission.Code);
                }
                else
                {
                    permissionsSet.Remove(userPerm.Permission.Code);
                }
            }

            return permissionsSet.OrderBy(p => p).ToList();
        }
    }
}
