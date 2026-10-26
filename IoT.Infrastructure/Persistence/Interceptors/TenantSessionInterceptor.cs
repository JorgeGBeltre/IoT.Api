using System.Data.Common;
using IoT.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Row-Level Security (RLS) Interceptor for PostgreSQL (ADR-003).
/// Injects the current tenant ID into the PostgreSQL connection session via 'SELECT set_config('app.tenant_id', ...)'
/// and cleans up session variables upon connection closure to prevent connection pool leakage.
/// Provides defense-in-depth against SQL injection, Dapper queries, or EF Core query filter omissions.
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

    public override void ConnectionOpened(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {
        ConfigureSession(connection);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ConfigureSessionAsync(connection, cancellationToken);
    }

    public override void ConnectionClosed(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {
        ResetSession(connection);
    }

    public override async Task ConnectionClosedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {
        await ResetSessionAsync(connection);
    }

    private void ConfigureSession(DbConnection connection)
    {
        if (!IsPostgreSqlConnection(connection)) return;

        var tenantId = _tenantContext.TenantId;
        var rlsEnforce = _configuration.GetValue<bool>("Platform:Rls:Enforce", false);

        try
        {
            using var cmd = connection.CreateCommand();
            if (tenantId != Guid.Empty)
            {
                cmd.CommandText = $"SELECT set_config('app.tenant_id', '{tenantId}', false); SELECT set_config('app.is_system_session', 'false', false);";
                cmd.ExecuteNonQuery();
                _logger.LogTrace("RLS Session context app.tenant_id set to {TenantId}", tenantId);
            }
            else
            {
                if (rlsEnforce)
                {
                    _logger.LogCritical("RLS Violation: Attempted database connection without a resolved TenantId under strict mode.");
                    throw new InvalidOperationException("Row-Level Security (RLS) Violation: Operations require an authenticated TenantId context.");
                }

                cmd.CommandText = "SELECT set_config('app.tenant_id', '', false); SELECT set_config('app.is_system_session', 'true', false);";
                cmd.ExecuteNonQuery();
                _logger.LogTrace("RLS Session context set: is_system_session=true (Guid.Empty / System)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply PostgreSQL RLS session setting for TenantId {TenantId}", tenantId);
            if (rlsEnforce) throw;
        }
    }

    private async Task ConfigureSessionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (!IsPostgreSqlConnection(connection)) return;

        var tenantId = _tenantContext.TenantId;
        var rlsEnforce = _configuration.GetValue<bool>("Platform:Rls:Enforce", false);

        try
        {
            await using var cmd = connection.CreateCommand();
            if (tenantId != Guid.Empty)
            {
                cmd.CommandText = $"SELECT set_config('app.tenant_id', '{tenantId}', false); SELECT set_config('app.is_system_session', 'false', false);";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                _logger.LogTrace("RLS Session context app.tenant_id set to {TenantId}", tenantId);
            }
            else
            {
                if (rlsEnforce)
                {
                    _logger.LogCritical("RLS Violation: Attempted database connection without a resolved TenantId under strict mode.");
                    throw new InvalidOperationException("Row-Level Security (RLS) Violation: Operations require an authenticated TenantId context.");
                }

                cmd.CommandText = "SELECT set_config('app.tenant_id', '', false); SELECT set_config('app.is_system_session', 'true', false);";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                _logger.LogTrace("RLS Session context set: is_system_session=true (Guid.Empty / System)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply PostgreSQL RLS session setting for TenantId {TenantId}", tenantId);
            if (rlsEnforce) throw;
        }
    }

    private static void ResetSession(DbConnection connection)
    {
        if (!IsPostgreSqlConnection(connection) || connection.State != System.Data.ConnectionState.Open) return;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "RESET app.tenant_id; RESET app.is_system_session;";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Suppress errors during connection closure / disposal
        }
    }

    private static async Task ResetSessionAsync(DbConnection connection)
    {
        if (!IsPostgreSqlConnection(connection) || connection.State != System.Data.ConnectionState.Open) return;

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "RESET app.tenant_id; RESET app.is_system_session;";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Suppress errors during connection closure / disposal
        }
    }

    private static bool IsPostgreSqlConnection(DbConnection connection) =>
        connection.GetType().Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
}
