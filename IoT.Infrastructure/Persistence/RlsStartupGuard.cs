using Microsoft.Extensions.Logging;
using Npgsql;

namespace IoT.Infrastructure.Persistence;

/// <summary>
/// Guarda de arranque (E8.4c) que impide que el aislamiento multi-tenant/subárbol por Row-Level
/// Security se degrade EN SILENCIO en producción. La RLS sólo surte efecto si (a) el modo estricto
/// está activo (<c>Platform:Rls:Enforce == true</c>, para que el interceptor nunca abra una conexión
/// de plano de datos sin tenant) y (b) el ROL con el que el runtime se conecta
/// (<c>ConnectionStrings:Postgres</c>) NO es superusuario ni tiene <c>BYPASSRLS</c> (ambos omiten las
/// políticas RLS por completo). Si en PRODUCCIÓN cualquiera de las dos falla, se lanza (fail-fast: la
/// app no arranca). En NO producción (Development/Testing) nunca se falla: los tests y el dev pueden
/// correr con el rol admin; sólo se registra el aviso. ADR-003 D4/D6, ADR-013.
/// </summary>
public static class RlsStartupGuard
{
    /// <summary>Resultado puro (sin E/S) de evaluar las condiciones de aislamiento.</summary>
    public readonly record struct Evaluation(bool IsSatisfied, bool ShouldFail, string Message);

    /// <summary>
    /// Evalúa las condiciones de aislamiento SIN tocar la base. <paramref name="runtimeRoleIsSuperuser"/>
    /// y <paramref name="runtimeRoleBypassesRls"/> describen el rol de runtime ya consultado. Sólo
    /// produce <see cref="Evaluation.ShouldFail"/> en producción; en otros entornos
    /// <see cref="Evaluation.IsSatisfied"/> puede ser <c>false</c> (hay que avisar) pero nunca se exige
    /// fallar.
    /// </summary>
    public static Evaluation Evaluate(
        bool isProduction,
        bool enforce,
        bool runtimeRoleIsSuperuser,
        bool runtimeRoleBypassesRls)
    {
        var problems = new List<string>();
        if (!enforce)
        {
            problems.Add("la configuración 'Platform:Rls:Enforce' no es true (el interceptor podría abrir conexiones de plano de datos sin tenant)");
        }
        if (runtimeRoleIsSuperuser)
        {
            problems.Add("el rol de runtime (ConnectionStrings:Postgres) es SUPERUSER (omite por completo la RLS)");
        }
        if (runtimeRoleBypassesRls)
        {
            problems.Add("el rol de runtime (ConnectionStrings:Postgres) tiene BYPASSRLS (omite por completo la RLS)");
        }

        if (problems.Count == 0)
        {
            return new Evaluation(
                IsSatisfied: true,
                ShouldFail: false,
                Message: "RLS: aislamiento forzado (Enforce=true y rol de runtime NOBYPASSRLS/NOSUPERUSER).");
        }

        var detail = string.Join("; ", problems);
        var message = isProduction
            ? $"Critical Security Failure: el aislamiento multi-tenant/subárbol por Row-Level Security NO está garantizado en producción: {detail}. Configure 'Platform:Rls:Enforce=true' y 'ConnectionStrings:Postgres' con un rol NOBYPASSRLS NOSUPERUSER (p. ej. iot_app)."
            : $"RLS: el aislamiento por Row-Level Security no está completamente forzado ({detail}). Permitido fuera de producción (dev/test pueden usar el rol admin); en producción esto haría fallar el arranque.";

        return new Evaluation(
            IsSatisfied: false,
            ShouldFail: isProduction,
            Message: message);
    }

    /// <summary>
    /// Consulta por la CONEXIÓN DE RUNTIME el estado del rol actual (<c>current_user</c>) en
    /// <c>pg_roles</c> y aplica <see cref="Evaluate"/>. En producción lanza
    /// <see cref="InvalidOperationException"/> si el aislamiento no está forzado; fuera de producción
    /// sólo registra el aviso y continúa. Se invoca en el arranque, DESPUÉS del
    /// <see cref="RlsRoleBootstrapper"/> (el rol de runtime ya existe con sus atributos).
    /// </summary>
    public static async Task EnsureIsolationEnforcedAsync(
        string runtimeConnectionString,
        bool isProduction,
        bool enforce,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        bool runtimeRoleIsSuperuser;
        bool runtimeRoleBypassesRls;

        await using (var connection = new NpgsqlConnection(runtimeConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            // rolsuper / rolbypassrls del rol con el que la app se conecta de verdad. Son columnas
            // legibles de pg_roles (no rolpassword). current_user refleja el rol de esta conexión.
            command.CommandText = "SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                // Sin fila para current_user no se puede probar el aislamiento: trátalo como el peor caso.
                runtimeRoleIsSuperuser = true;
                runtimeRoleBypassesRls = true;
            }
            else
            {
                runtimeRoleIsSuperuser = reader.GetBoolean(0);
                runtimeRoleBypassesRls = reader.GetBoolean(1);
            }
        }

        var evaluation = Evaluate(isProduction, enforce, runtimeRoleIsSuperuser, runtimeRoleBypassesRls);

        if (evaluation.ShouldFail)
        {
            logger.LogCritical("{Message}", evaluation.Message);
            throw new InvalidOperationException(evaluation.Message);
        }

        if (evaluation.IsSatisfied)
        {
            logger.LogInformation("{Message}", evaluation.Message);
        }
        else
        {
            logger.LogWarning("{Message}", evaluation.Message);
        }
    }
}
