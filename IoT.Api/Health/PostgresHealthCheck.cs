using IoT.Application.Common.Interfaces;
using IoT.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IoT.Api.Health;

public sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ITenantContext? _tenantContext;

    public PostgresHealthCheck(ApplicationDbContext dbContext, ITenantContext? tenantContext = null)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Sonda de conectividad sin tenant: operación de sistema para que bajo
            // Platform:Rls:Enforce el interceptor no rechace la conexión (ADR-003 D6).
            if (_tenantContext != null) _tenantContext.IsSystemOperation = true;

            var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);
            if (canConnect)
            {
                return HealthCheckResult.Healthy("PostgreSQL with PostGIS is reachable and accepting connections.");
            }

            return HealthCheckResult.Unhealthy("Unable to establish connection with PostgreSQL database.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL health check failed.", ex);
        }
    }
}
