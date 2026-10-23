using IoT.Api.Common;
using IoT.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Controllers;

/// <summary>
/// Lectura de usuarios del tenant. El alta y modificación se gestionan por el
/// administrador vía <c>AuthController</c> y flujos de invitación — este endpoint
/// expone sólo el listado para la vista de administración.
/// </summary>
[Authorize]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/users")]
public sealed class UsersController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly ITenantContext _tenantContext;

    public UsersController(IUserRepository users, ITenantContext tenantContext)
    {
        _users = users;
        _tenantContext = tenantContext;
    }

    public sealed record UserListItemDto(
        Guid Id,
        string Username,
        string Email,
        string FullName,
        string RoleName,
        bool IsActive,
        bool MustChangePassword,
        int FailedLoginAttempts,
        DateTime? LockoutEndUtc,
        DateTime CreatedAt);

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var list = await _users.GetByTenantAsync(_tenantContext.TenantId, ct);
        var dtos = list.Select(u => new UserListItemDto(
            u.Id, u.Username, u.Email, u.FullName, u.RoleName,
            u.IsActive, u.MustChangePassword, u.FailedLoginAttempts,
            u.LockoutEndUtc, u.CreatedAt)).ToList();
        return Ok(dtos);
    }
}
