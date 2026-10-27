using System.Security.Claims;
using IoT.Api.Common;
using IoT.Application.Users.Commands;
using IoT.Domain.Repositories;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Controllers;

/// <summary>
/// Lectura y ALTA de usuarios del tenant. El listado alimenta la vista de administración; el alta
/// (E8.6a) crea un usuario asignado a un nodo Customer del subárbol de la sesión, con una contraseña
/// TEMPORAL que se muestra UNA sola vez. El recorte por subárbol lo aplican la RLS de PostgreSQL y el
/// código del handler (igual que el CRUD de Customer). Controlador fino: media a MediatR / repositorio.
/// </summary>
[Authorize(Policy = AuthorizationPolicies.TenantAdmin)]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/users")]
public sealed class UsersController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly ICustomerRepository _customers;
    private readonly ITenantContext _tenantContext;
    private readonly IMediator _mediator;

    public UsersController(IUserRepository users, ICustomerRepository customers, ITenantContext tenantContext, IMediator mediator)
    {
        _users = users;
        _customers = customers;
        _tenantContext = tenantContext;
        _mediator = mediator;
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
        DateTime CreatedAt,
        // E8.6b (aditivo): nodo Customer al que pertenece el usuario, para mostrar en la UI a qué
        // cliente/organización corresponde cada cuenta. Nullable por los usuarios aún sin nodo (raíz
        // histórico) y por los cuyo nodo no esté en el subárbol visible (el nombre se resuelve en memoria).
        Guid? CustomerId,
        string? CustomerName);

    /// <summary>
    /// Alta de usuario. Si <see cref="CreateUserRequest.TargetCustomerId"/> es <c>null</c>, el usuario
    /// se asigna al nodo de la sesión (o al raíz del tenant si no está acotada). El rol debe ser uno de
    /// los asignables (Admin/Operator/Viewer).
    /// </summary>
    public sealed record CreateUserRequest(
        string Username,
        string Email,
        string FullName,
        string RoleName,
        Guid? TargetCustomerId);

    /// <summary>
    /// Respuesta del alta: datos del usuario y la contraseña TEMPORAL en texto plano, devuelta UNA sola
    /// vez (el usuario debe cambiarla en el primer inicio de sesión). No se vuelve a exponer.
    /// </summary>
    public sealed record CreateUserResponse(
        Guid Id,
        string Username,
        string Email,
        string FullName,
        string RoleName,
        Guid CustomerId,
        bool MustChangePassword,
        string TemporaryPassword,
        string Notice);

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var list = await _users.GetByTenantAsync(_tenantContext.TenantId, ct);

        // Nombre del nodo Customer dueño de cada usuario, resuelto en UNA sola consulta que RESPETA los
        // filtros (RLS por subárbol + EF por tenant, SIN IgnoreQueryFilters): mismo patrón que
        // GetDevicesQuery. Los customers devueltos son exactamente el subárbol visible de la sesión —el
        // mismo del que cuelgan los usuarios visibles—, así que el nombre se resuelve en memoria sin
        // consultas por usuario (sin N+1) ni fuga de otros subárboles.
        var customerNames = (await _customers.GetVisibleAsync(ct))
            .ToDictionary(c => c.Id, c => c.Name);

        var dtos = list.Select(u => new UserListItemDto(
            u.Id, u.Username, u.Email, u.FullName, u.RoleName,
            u.IsActive, u.MustChangePassword, u.FailedLoginAttempts,
            u.LockoutEndUtc, u.CreatedAt,
            u.CustomerId,
            u.CustomerId is Guid cid && customerNames.TryGetValue(cid, out var name) ? name : null)).ToList();
        return Ok(dtos);
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.CustomerAdmin)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var actorId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        Guid? performedByUserId = Guid.TryParse(actorId, out var parsed) ? parsed : null;
        var performedByUsername = User.Identity?.Name ?? User.FindFirst("unique_name")?.Value;

        var result = await _mediator.Send(
            new CreateUserCommand(
                request.Username,
                request.Email,
                request.FullName,
                request.RoleName,
                request.TargetCustomerId,
                performedByUserId,
                performedByUsername),
            ct);

        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error.Message });
        }

        var value = result.Value;
        var response = new CreateUserResponse(
            value.Id,
            value.Username,
            value.Email,
            value.FullName,
            value.RoleName,
            value.CustomerId,
            value.MustChangePassword,
            value.TemporaryPassword,
            "Esta contraseña temporal se muestra una sola vez. Entréguela al usuario por un canal seguro; deberá cambiarla al iniciar sesión.");

        return CreatedAtAction(nameof(GetAll), new { }, response);
    }
}
