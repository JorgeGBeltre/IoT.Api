using System.Security.Claims;
using IoT.Application.Auth.Commands;
using IoT.Application.Auth.Queries;
using IoT.Application.Common.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Microsoft.AspNetCore.RateLimiting;

namespace IoT.Api.Controllers;

[EnableRateLimiting("AuthRateLimit")]
[ApiController]
[Route("api/v1/[controller]")]
public sealed class AuthController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IMediator mediator,
        ITenantContext tenantContext,
        ILogger<AuthController> logger)
    {
        _mediator = mediator;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public sealed record DemoTenantDto(Guid Id, string Name, string Code, string Description);

    private static readonly List<DemoTenantDto> DemoTenants = new()
    {
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Logística & Transporte Caribe", "CARIBE-LOG", "Flota de camiones GPS y entregas urbanas")
    };

    [HttpGet("demo-tenants")]
    [AllowAnonymous]
    public IActionResult GetDemoTenants()
    {
        return Ok(DemoTenants);
    }

    public sealed record LoginDto(string Username, string Password, Guid? TenantId);

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginDto request, CancellationToken cancellationToken)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();
        var effectiveTenant = request.TenantId ?? (_tenantContext.TenantId != Guid.Empty ? _tenantContext.TenantId : null);

        var result = await _mediator.Send(new LoginCommand(
            request.Username,
            request.Password,
            effectiveTenant,
            ip,
            userAgent), cancellationToken);

        if (result.IsFailure)
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Error de autenticación",
                Detail = result.Error.Message,
                Status = StatusCodes.Status401Unauthorized
            });
        }

        AppendSessionCookies(result.Value);

        return Ok(result.Value);
    }

    public sealed record RegisterAccountDto(
        string FullName,
        string Email,
        string Password,
        string? OrganizationName = null,
        string? Username = null);

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterAccountDto request, CancellationToken cancellationToken)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await _mediator.Send(new RegisterAccountCommand(
            request.FullName,
            request.Email,
            request.Password,
            request.OrganizationName,
            request.Username,
            ip,
            userAgent), cancellationToken);

        if (result.IsFailure)
        {
            var status = result.Error.Code is "Auth.EmailAlreadyRegistered" or "Auth.UsernameTaken"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;

            return StatusCode(status, new ProblemDetails
            {
                Title = "No se pudo completar el registro",
                Detail = result.Error.Message,
                Status = status
            });
        }

        AppendSessionCookies(result.Value);
        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    public sealed record RefreshSessionDto(string? RefreshToken = null);

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshSessionDto? request, CancellationToken cancellationToken)
    {
        var refreshToken = !string.IsNullOrWhiteSpace(request?.RefreshToken)
            ? request!.RefreshToken!
            : Request.Cookies["iot_refresh_token"] ?? string.Empty;

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await _mediator.Send(new RefreshTokenCommand(refreshToken, ip, userAgent), cancellationToken);
        if (result.IsFailure)
        {
            Response.Cookies.Delete("iot_auth_token", new CookieOptions { Path = "/" });
            Response.Cookies.Delete("iot_refresh_token", new CookieOptions { Path = "/api/v1/auth" });
            return Unauthorized(new ProblemDetails
            {
                Title = "Sesión expirada",
                Detail = result.Error.Message,
                Status = StatusCodes.Status401Unauthorized
            });
        }

        AppendSessionCookies(result.Value);
        return Ok(result.Value);
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetMe(CancellationToken cancellationToken)
    {
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(sub, out var userId))
        {
            return Unauthorized(new ProblemDetails { Title = "No autorizado", Detail = "Identificador de usuario inválido en el token." });
        }

        var result = await _mediator.Send(new GetCurrentUserProfileQuery(userId, _tenantContext.TenantId), cancellationToken);
        if (result.IsFailure)
        {
            return NotFound(new ProblemDetails { Title = "Usuario no encontrado", Detail = result.Error.Message });
        }

        return Ok(result.Value);
    }

    public sealed record ChangeTempPasswordDto(string Username, string CurrentPassword, string NewPassword, Guid? TenantId);

    [HttpPost("change-temporary-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ChangeTemporaryPassword([FromBody] ChangeTempPasswordDto request, CancellationToken cancellationToken)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();
        var tenantId = request.TenantId ?? _tenantContext.TenantId;

        var result = await _mediator.Send(new ChangeTemporaryPasswordCommand(
            request.Username,
            request.CurrentPassword,
            request.NewPassword,
            tenantId,
            ip,
            userAgent), cancellationToken);

        if (result.IsFailure)
        {
            return BadRequest(new ProblemDetails { Title = "Error de cambio de contraseña", Detail = result.Error.Message });
        }

        return Ok(result.Value);
    }

    public sealed record ChangePasswordDto(string CurrentPassword, string NewPassword);

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto request, CancellationToken cancellationToken)
    {
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(sub, out var userId) || _tenantContext.TenantId == Guid.Empty)
        {
            return Unauthorized(new ProblemDetails { Title = "No autorizado", Detail = "Sesión inválida." });
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();
        var result = await _mediator.Send(new ChangePasswordCommand(
            userId,
            _tenantContext.TenantId,
            request.CurrentPassword,
            request.NewPassword,
            ip,
            userAgent), cancellationToken);

        if (result.IsFailure)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "No se pudo actualizar la contraseña",
                Detail = result.Error.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Ok(new { message = "Contraseña actualizada correctamente." });
    }

    public sealed record ForgotPasswordDto(string Email, Guid? TenantId);

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto request, CancellationToken cancellationToken)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();
        var tenantId = request.TenantId ?? _tenantContext.TenantId;

        var result = await _mediator.Send(new ForgotPasswordCommand(
            request.Email,
            tenantId,
            ip,
            userAgent), cancellationToken);

        return Ok(new { message = result.Value });
    }

    public sealed record VerifyOtpDto(string Email, string OtpCode, Guid? TenantId);

    [HttpPost("verify-otp")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpDto request, CancellationToken cancellationToken)
    {
        var tenantId = request.TenantId ?? _tenantContext.TenantId;
        var result = await _mediator.Send(new VerifyOtpCommand(request.Email, request.OtpCode, tenantId), cancellationToken);

        if (result.IsFailure)
        {
            return BadRequest(new ProblemDetails { Title = "OTP inválido", Detail = result.Error.Message });
        }

        return Ok(new { resetToken = result.Value });
    }

    public sealed record ResetPasswordDto(string Email, string ResetToken, string NewPassword, Guid? TenantId);

    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto request, CancellationToken cancellationToken)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();
        var tenantId = request.TenantId ?? _tenantContext.TenantId;

        var result = await _mediator.Send(new ResetPasswordCommand(
            request.Email,
            request.ResetToken,
            request.NewPassword,
            tenantId,
            ip,
            userAgent), cancellationToken);

        if (result.IsFailure)
        {
            return BadRequest(new ProblemDetails { Title = "Error al restablecer contraseña", Detail = result.Error.Message });
        }

        return Ok(new { message = result.Value });
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (Guid.TryParse(sub, out var userId) && _tenantContext.TenantId != Guid.Empty)
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var userAgent = Request.Headers.UserAgent.ToString();
            await _mediator.Send(new LogoutCommand(userId, _tenantContext.TenantId, ip, userAgent), cancellationToken);
        }

        Response.Cookies.Delete("iot_auth_token", new CookieOptions { Path = "/" });
        Response.Cookies.Delete("iot_refresh_token", new CookieOptions { Path = "/api/v1/auth" });
        return Ok(new { success = true, message = "Sesión cerrada exitosamente." });
    }

    private void AppendSessionCookies(AuthTokenResult session)
    {
        var accessCookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = new DateTimeOffset(DateTime.SpecifyKind(session.ExpiresAt, DateTimeKind.Utc))
        };
        Response.Cookies.Append("iot_auth_token", session.Token, accessCookieOptions);

        if (!string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            var refreshExpires = session.RefreshExpiresAt.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(session.RefreshExpiresAt.Value, DateTimeKind.Utc))
                : DateTimeOffset.UtcNow.AddDays(14);
            var refreshCookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/api/v1/auth",
                Expires = refreshExpires
            };
            Response.Cookies.Append("iot_refresh_token", session.RefreshToken, refreshCookieOptions);
        }
    }
}
