using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Auth.Queries;

public sealed record UserProfileDto(
    Guid Id,
    Guid TenantId,
    string Username,
    string Email,
    string FullName,
    string RoleName,
    bool MustChangePassword,
    IReadOnlyList<string> Permissions);

public sealed record GetCurrentUserProfileQuery(Guid UserId, Guid TenantId) : IRequest<Result<UserProfileDto>>;

public sealed class GetCurrentUserProfileQueryHandler : IRequestHandler<GetCurrentUserProfileQuery, Result<UserProfileDto>>
{
    private readonly IUserRepository _userRepository;
    private readonly IPermissionService? _permissionService;

    public GetCurrentUserProfileQueryHandler(
        IUserRepository userRepository,
        IPermissionService? permissionService = null)
    {
        _userRepository = userRepository;
        _permissionService = permissionService;
    }

    public async Task<Result<UserProfileDto>> Handle(GetCurrentUserProfileQuery request, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null || user.TenantId != request.TenantId)
        {
            return Result<UserProfileDto>.Failure(new Error("User.NotFound", "Usuario no encontrado en la empresa actual."));
        }

        // Permisos EFECTIVOS del usuario (los mismos que viajan en el JWT: permisos del rol +
        // overrides UserPermission), para que la UI coincida con lo que realmente se autoriza. Antes
        // sólo se devolvían los grants per-user (siempre vacíos hoy) → el perfil divergía del
        // enforcement (H4 de la auditoría RBAC). Fail-closed: sin IPermissionService se cae a los
        // permisos por defecto del rol.
        var permissions = _permissionService != null
            ? await _permissionService.GetEffectivePermissionsAsync(user.TenantId, user.Id, user.RoleName, user.RoleId, cancellationToken)
            : StandardPermissions.GetDefaultPermissionsForRole(user.RoleName);

        var dto = new UserProfileDto(
            user.Id,
            user.TenantId,
            user.Username,
            user.Email,
            user.FullName,
            user.RoleName,
            user.MustChangePassword,
            permissions);

        return Result<UserProfileDto>.Success(dto);
    }
}
