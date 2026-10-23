using IoT.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IoT.Api.Health;

public sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly ApplicationDbContext _dbContext;

    public PostgresHealthCheck(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
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
