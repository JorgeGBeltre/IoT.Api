using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace IoT.Api.Health;

public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;

    public RedisHealthCheck(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_redis.IsConnected)
            {
                return Task.FromResult(HealthCheckResult.Healthy("Redis cache & state store is connected."));
            }

            return Task.FromResult(HealthCheckResult.Degraded("Redis connection is not currently active."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Redis health check threw an exception.", ex));
        }
    }
}
