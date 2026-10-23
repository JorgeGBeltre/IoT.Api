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

    public GetCurrentUserProfileQueryHandler(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<Result<UserProfileDto>> Handle(GetCurrentUserProfileQuery request, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null || user.TenantId != request.TenantId)
        {
            return Result<UserProfileDto>.Failure(new Error("User.NotFound", "Usuario no encontrado en la empresa actual."));
        }

        var permissions = user.UserPermissions.Where(p => p.IsGranted).Select(p => p.Permission?.Code ?? "").ToList();

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
