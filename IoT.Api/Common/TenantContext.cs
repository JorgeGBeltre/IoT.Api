global using IoT.Application.Common.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Common;

public sealed class TenantContext : ITenantContext
{
    public Guid TenantId { get; set; } = Guid.Empty;

    /// <summary>Operación de sistema (cross-tenant autorizada). Ver <see cref="ITenantContext"/>.</summary>
    public bool IsSystemOperation { get; set; }
}

public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        // 1. Authenticated user: resolve tenant strictly from the cryptographically verified JWT claim
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var claim = context.User.FindFirst("tenant_id");
            if (claim != null && Guid.TryParse(claim.Value, out var tenantIdFromClaim) && tenantIdFromClaim != Guid.Empty)
            {
                // Check for tenant spoofing via X-Tenant-Id header or query string
                if (context.Request.Headers.TryGetValue("X-Tenant-Id", out var headerVal) &&
                    Guid.TryParse(headerVal, out var headerTenantId) &&
                    headerTenantId != tenantIdFromClaim)
                {
                    _logger.LogWarning("Security Alert: Tenant spoofing detected. User {User} with tenant {ClaimTenant} attempted to access tenant {HeaderTenant}",
                        context.User.Identity?.Name, tenantIdFromClaim, headerTenantId);

                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/problem+json";
                    await context.Response.WriteAsJsonAsync(new ProblemDetails
                    {
                        Title = "Acceso a Tenant Denegado",
                        Detail = "El header X-Tenant-Id no coincide con la empresa asignada a tu sesión.",
                        Status = StatusCodes.Status403Forbidden
                    });
                    return;
                }

                tenantContext.TenantId = tenantIdFromClaim;
            }
        }
        else
        {
            // Unauthenticated requests cannot set data plane tenant context via headers
            tenantContext.TenantId = Guid.Empty;
        }

        await _next(context);
    }
}
