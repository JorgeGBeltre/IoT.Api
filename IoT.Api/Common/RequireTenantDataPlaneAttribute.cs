using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IoT.Api.Common;

/// <summary>
/// Filtro de autorización de aislamiento de Tenant Data Plane (Adaptado de SaaS-Ecf):
/// Garantiza que el usuario esté autenticado y posea un contexto de Tenant válido en su token.
/// Cada usuario accede estrictamente a los dispositivos y telemetría de su propio ámbito empresarial.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class RequireTenantDataPlaneAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var endpoint = context.HttpContext.GetEndpoint();
        if (endpoint?.Metadata?.GetMetadata<IAllowAnonymous>() != null)
        {
            return Task.CompletedTask;
        }

        var user = context.HttpContext.User;
        if (!user.Identity?.IsAuthenticated ?? true)
        {
            context.Result = new UnauthorizedObjectResult(new ProblemDetails
            {
                Title = "No autenticado",
                Detail = "Debes iniciar sesión para acceder al plano de datos operativos de la empresa.",
                Status = StatusCodes.Status401Unauthorized
            });
            return Task.CompletedTask;
        }

        var tenantClaim = user.FindFirst("tenant_id")?.Value;
        if (string.IsNullOrWhiteSpace(tenantClaim) || !Guid.TryParse(tenantClaim, out var tenantGuid) || tenantGuid == Guid.Empty)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Title = "Contexto de Tenant Requerido",
                Detail = "Se requiere un contexto de empresa válido para acceder al plano de datos telemático.",
                Status = StatusCodes.Status403Forbidden
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }
}
