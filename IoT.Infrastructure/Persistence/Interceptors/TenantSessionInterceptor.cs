using System.Data.Common;
using IoT.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Row-Level Security (RLS) Interceptor for PostgreSQL (ADR-003).
/// Injects the current tenant ID into PostgreSQL transaction session via 'SET LOCAL app.tenant_id = ...'
/// providing defense-in-depth against SQL injection, Dapper queries, or EF Core query filter omissions.
/// </summary>
public sealed class TenantSessionInterceptor : DbConnectionInterceptor
{
    private readonly ITenantContext _tenantContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantSessionInterceptor> _logger;

    public TenantSessionInterceptor(
        ITenantContext tenantContext,
        IConfiguration configuration,
        ILogger<TenantSessionInterceptor> logger)
    {
        _tenantContext = tenantContext;
        _configuration = configuration;
        _logger = logger;
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.TenantId;
        var rlsEnforce = _configuration.GetValue<bool>("Platform:Rls:Enforce", false);

        // Verify if provider is PostgreSQL before executing PostgreSQL-specific session variables
        var isPostgreSql = connection.GetType().Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);

        if (tenantId == Guid.Empty)
        {
            if (rlsEnforce)
            {
                _logger.LogCritical("RLS Violation: Attempted database connection without a resolved TenantId under strict mode.");
                throw new InvalidOperationException("Row-Level Security (RLS) Violation: Operations require an authenticated TenantId context.");
            }

            _logger.LogDebug("Database connection opened with Guid.Empty tenant context (system or migration).");
            return;
        }

        if (isPostgreSql)
        {
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SET LOCAL app.tenant_id = '{tenantId}';";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                _logger.LogTrace("RLS Session context app.tenant_id set to {TenantId}", tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply PostgreSQL RLS session setting for TenantId {TenantId}", tenantId);
                if (rlsEnforce)
                {
                    throw;
                }
            }
        }
    }
}
