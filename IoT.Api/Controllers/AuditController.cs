using IoT.Api.Common;
using IoT.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Controllers;

/// <summary>
/// Lectura del registro de auditoría del tenant. Las entradas se insertan desde
/// los handlers que realizan operaciones sensibles (login, dispatch de comandos,
/// reseteo de contraseñas, cambios de configuración).
/// </summary>
[Authorize]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/audit")]
public sealed class AuditController : ControllerBase
{
    private readonly IAuditLogRepository _audit;
    private readonly ITenantContext _tenantContext;

    public AuditController(IAuditLogRepository audit, ITenantContext tenantContext)
    {
        _audit = audit;
        _tenantContext = tenantContext;
    }

    public sealed record AuditEntryDto(
        Guid Id,
        Guid? UserId,
        string Username,
        string Action,
        string Details,
        string? IpAddress,
        string? UserAgent,
        DateTime TimestampUtc);

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecent(
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        if (limit <= 0 || limit > 1000) limit = 100;
        var list = await _audit.GetByTenantAsync(_tenantContext.TenantId, limit, ct);
        var dtos = list.Select(a => new AuditEntryDto(
            a.Id, a.UserId, a.Username, a.Action, a.Details,
            a.IpAddress, a.UserAgent, a.TimestampUtc)).ToList();
        return Ok(dtos);
    }
}
