global using IoT.Application.Common.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Common;

public sealed class TenantContext : ITenantContext
{
    public Guid TenantId { get; set; } = Guid.Empty;

    /// <summary>Nodo del usuario en la jerarquía (ADR-013). Ver <see cref="ITenantContext"/>.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Path del subárbol visible (ADR-013). Ver <see cref="ITenantContext"/>.</summary>
    public string? CustomerPath { get; set; }

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

                // Jerarquía multinivel (ADR-013): el nodo del usuario viaja en el claim firmado
                // customer_id y su Path (resuelto en el servidor al emitir el token) en
                // customer_path. Se leen SÓLO del JWT verificado, con el mismo anti-spoofing que
                // tenant_id. Si NO vienen (token anterior a E8.2), CustomerPath queda null = SIN
                // restricción de subárbol dentro del tenant (equivale al raíz; NO bloquear).
                var customerClaim = context.User.FindFirst("customer_id");
                if (customerClaim != null && Guid.TryParse(customerClaim.Value, out var customerIdFromClaim) && customerIdFromClaim != Guid.Empty)
                {
                    // Anti-spoofing: un header X-Customer-Id que no coincida con el claim se rechaza
                    // (igual que X-Tenant-Id). El cliente nunca redefine su nodo por cabecera.
                    if (context.Request.Headers.TryGetValue("X-Customer-Id", out var customerHeaderVal) &&
                        Guid.TryParse(customerHeaderVal, out var headerCustomerId) &&
                        headerCustomerId != customerIdFromClaim)
                    {
                        _logger.LogWarning("Security Alert: Customer spoofing detected. User {User} with customer {ClaimCustomer} attempted to access customer {HeaderCustomer}",
                            context.User.Identity?.Name, customerIdFromClaim, headerCustomerId);

                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        context.Response.ContentType = "application/problem+json";
                        await context.Response.WriteAsJsonAsync(new ProblemDetails
                        {
                            Title = "Acceso a Customer Denegado",
                            Detail = "El header X-Customer-Id no coincide con el nodo asignado a tu sesión.",
                            Status = StatusCodes.Status403Forbidden
                        });
                        return;
                    }

                    tenantContext.CustomerId = customerIdFromClaim;

                    // El Path se tomó del claim firmado (emitido server-side desde la tabla
                    // customers). Si por cualquier motivo no viene, CustomerPath queda null → sin
                    // restricción de subárbol (no bloquear); la RLS de tenant sigue aislando.
                    var pathClaim = context.User.FindFirst("customer_path");
                    if (pathClaim != null && !string.IsNullOrWhiteSpace(pathClaim.Value))
                    {
                        tenantContext.CustomerPath = pathClaim.Value;
                    }
                }
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
