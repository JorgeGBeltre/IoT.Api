global using IoT.Application.Common.Interfaces;

using System.Text.RegularExpressions;
using IoT.Domain.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Common;

public sealed class TenantContext : ITenantContext
{
    public Guid TenantId { get; set; } = Guid.Empty;

    /// <summary>Nodo del usuario en la jerarquía (ADR-013). Ver <see cref="ITenantContext"/>.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Path del subárbol visible (ADR-013). Ver <see cref="ITenantContext"/>.</summary>
    public string? CustomerPath { get; set; }

    /// <summary>Ids del subárbol resuelto por request (ADR-013 E8.3b). Ver <see cref="ITenantContext"/>.</summary>
    public IReadOnlyCollection<Guid>? CustomerSubtreeIds { get; set; }

    /// <summary>Operación de sistema (cross-tenant autorizada). Ver <see cref="ITenantContext"/>.</summary>
    public bool IsSystemOperation { get; set; }
}

public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    // Un path ltree válido es una secuencia de etiquetas separadas por '.'; cada etiqueta sólo
    // admite [A-Za-z0-9_] (nuestras etiquetas son 'c' + 32 hex, ADR-013). Validar aquí deja el
    // chequeo FAIL-CLOSED y evita que un valor malformado llegue al cast ::ltree de la GUC.
    private static readonly Regex LtreePathPattern =
        new(@"^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$", RegexOptions.Compiled);

    private static bool IsValidLtreePath(string value) => LtreePathPattern.IsMatch(value);

    public TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext, ICustomerRepository customerRepository)
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
                    // customers). NUESTRO JwtTokenGenerator emite customer_id y customer_path de
                    // forma solidaria: un token que trae customer_id (nodo asignado) pero NO un
                    // customer_path válido es INCONSISTENTE (manipulado o emitido por un generador
                    // desalineado). FAIL-CLOSED: se responde 403, igual que el anti-spoofing de
                    // X-Tenant-Id / X-Customer-Id, y NO se continúa sin restricción de subárbol
                    // (eso sería fail-open). El caso "ambos ausentes" (token anterior a E8.2) ni
                    // siquiera entra aquí: queda arriba con CustomerPath null = sin restricción.
                    var pathClaim = context.User.FindFirst("customer_path");
                    var customerPath = pathClaim?.Value;
                    if (string.IsNullOrWhiteSpace(customerPath) || !IsValidLtreePath(customerPath))
                    {
                        _logger.LogWarning("Security Alert: Inconsistent token. User {User} presents customer_id {ClaimCustomer} without a valid customer_path claim.",
                            context.User.Identity?.Name, customerIdFromClaim);

                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        context.Response.ContentType = "application/problem+json";
                        await context.Response.WriteAsJsonAsync(new ProblemDetails
                        {
                            Title = "Token Inconsistente",
                            Detail = "El token incluye un nodo asignado pero no un alcance (customer_path) válido. Vuelve a iniciar sesión.",
                            Status = StatusCodes.Status403Forbidden
                        });
                        return;
                    }

                    tenantContext.CustomerPath = customerPath;

                    // Resolver UNA vez por request el conjunto de descendientes del subárbol
                    // (ADR-013 E8.3b), de forma ASÍNCRONA y en scope de sistema, para que el
                    // filtro global de EF lo lea sin ejecutar SQL síncrono reentrante dentro de
                    // la evaluación del filtro.
                    tenantContext.CustomerSubtreeIds =
                        await customerRepository.GetSubtreeIdsAsync(customerPath, context.RequestAborted);
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
