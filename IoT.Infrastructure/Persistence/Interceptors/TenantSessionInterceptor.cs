using System.Data.Common;
using IoT.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Row-Level Security (RLS) Interceptor for PostgreSQL (ADR-003).
/// Injects the current tenant ID into the PostgreSQL connection session via 'set_config('app.tenant_id', ...)'
/// and cleans up session variables upon connection closure to prevent connection pool leakage.
/// Provides defense-in-depth against SQL injection, Dapper queries, or EF Core query filter omissions.
///
/// Reglas de apertura de conexión:
///  - Hay TenantId resuelto            → app.tenant_id = tenant,  app.is_system_session = false (RLS por tenant).
///  - Sin TenantId, operación SISTEMA  → app.tenant_id = '',      app.is_system_session = true  (cross-tenant autorizada, ADR-003 D6).
///  - Sin TenantId ni sistema + enforce → LANZA (nunca se abre una conexión de plano de datos sin tenant, ADR-003 D5).
///  - Sin TenantId ni sistema sin enforce → sesión de sistema (compatibilidad; RLS sigue efectiva para el plano de datos que SÍ trae tenant).
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

    private bool RlsEnforce => _configuration.GetValue<bool>("Platform:Rls:Enforce", false);

    /// <summary>
    /// Decide el SQL de apertura de sesión según el contexto de tenant. Puede LANZAR bajo
    /// modo estricto (sin tenant ni operación de sistema): esa excepción debe propagar.
    /// </summary>
    private string ResolveSessionCommand()
    {
        var tenantId = _tenantContext.TenantId;

        if (tenantId != Guid.Empty)
        {
            // tenantId es un Guid (no texto controlado por el usuario): su interpolación en el
            // literal no es inyectable. set_config no admite parámetros enlazados cómodamente
            // aquí (comando crudo sobre la conexión), y el valor está acotado al formato Guid.
            //
            // app.customer_path (ADR-013): path ltree del subárbol visible, o cadena vacía si el
            // usuario no tiene nodo (raíz / token antiguo) = SIN restricción de subárbol. El valor
            // se resuelve en el servidor desde la tabla customers (no se confía al cliente) y aquí
            // se escapan las comillas simples como defensa adicional (ya es un ltree válido).
            var customerPath = EscapeLiteral(_tenantContext.CustomerPath);
            return $"SELECT set_config('app.tenant_id', '{tenantId}', false); SELECT set_config('app.is_system_session', 'false', false); SELECT set_config('app.customer_path', '{customerPath}', false);";
        }

        if (_tenantContext.IsSystemOperation)
        {
            // Operación de sistema: sin restricción de subárbol (customer_path vacío = raíz).
            return "SELECT set_config('app.tenant_id', '', false); SELECT set_config('app.is_system_session', 'true', false); SELECT set_config('app.customer_path', '', false);";
        }

        if (RlsEnforce)
        {
            _logger.LogCritical("RLS Violation: intento de conexión sin TenantId resuelto y sin operación de sistema explícita bajo modo estricto.");
            throw new InvalidOperationException(
                "Row-Level Security (RLS) Violation: las operaciones requieren un TenantId autenticado o una operación de sistema explícita (ITenantContext.IsSystemOperation).");
        }

        // Modo no estricto (compatibilidad): sin tenant → sesión de sistema. RLS sigue aislando
        // todo el plano de datos que SÍ trae tenant; sólo las rutas sin tenant quedan abiertas.
        return "SELECT set_config('app.tenant_id', '', false); SELECT set_config('app.is_system_session', 'true', false); SELECT set_config('app.customer_path', '', false);";
    }

    /// <summary>
    /// Escapa las comillas simples de un literal para interpolarlo con seguridad en el argumento
    /// de <c>set_config</c>. El path proviene de la tabla <c>customers</c> (ya un ltree válido);
    /// esto es defensa adicional, no la única barrera. <c>null</c> → cadena vacía (sin restricción).
    /// </summary>
    private static string EscapeLiteral(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("'", "''");

    private void ConfigureSession(DbConnection connection)
    {
        if (!IsPostgreSqlConnection(connection)) return;

        // ResolveSessionCommand() puede lanzar bajo enforce: se llama FUERA del try para que la
        // violación RLS propague y no quede silenciada como un fallo de set_config cualquiera.
        var sql = ResolveSessionCommand();

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply PostgreSQL RLS session setting.");
            if (RlsEnforce) throw;
        }
    }

    private async Task ConfigureSessionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (!IsPostgreSqlConnection(connection)) return;

        var sql = ResolveSessionCommand();

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply PostgreSQL RLS session setting.");
            if (RlsEnforce) throw;
        }
    }

    private static void ResetSession(DbConnection connection)
    {
        if (!IsPostgreSqlConnection(connection) || connection.State != System.Data.ConnectionState.Open) return;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "RESET app.tenant_id; RESET app.is_system_session; RESET app.customer_path;";
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
            cmd.CommandText = "RESET app.tenant_id; RESET app.is_system_session; RESET app.customer_path;";
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
