using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IoT.Infrastructure.Persistence;

/// <summary>
/// Provisiona (y rota en cada arranque) el rol de aplicación de PostgreSQL usado por el runtime
/// para que la Row-Level Security SEA EFECTIVA (ADR-003 D4/D6).
///
/// Por qué existe este paso y no vive en una migración EF:
///  - El rol lleva un SECRETO (la contraseña), que no debe versionarse en una migración. Aquí
///    se toma de la cadena de conexión de runtime (<c>ConnectionStrings:Postgres</c>), fuente
///    única de verdad del usuario/clave con los que la app se conecta.
///  - La RLS sólo surte efecto si la app se conecta con un rol <c>NOBYPASSRLS NOSUPERUSER</c>.
///    Las migraciones y el seeding, en cambio, requieren el rol admin (<c>iot_admin</c>,
///    superusuario de la imagen Postgres, que omite RLS); por eso corren con la conexión admin.
///
/// Corre como ADMIN, DESPUÉS de aplicar las migraciones (las tablas deben existir para los GRANT).
/// Es idempotente: si el rol ya existe, sólo ajusta atributos/clave y re-aplica los grants.
///
/// Compatibilidad: si la conexión de runtime usa el MISMO usuario que la admin (no se configuró
/// un rol separado), no hay nada que provisionar y el paso se omite — el comportamiento previo
/// (sin split de roles) se preserva sin romper despliegues que aún no migraron su configuración.
/// </summary>
public static class RlsRoleBootstrapper
{
    private static readonly Regex SafeRoleName = new("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.Compiled);
    private static readonly Regex SafeDbName = new("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.Compiled);

    // CREATE/ALTER ROLE no admite parámetros enlazados: la contraseña se incrusta como literal.
    // Se restringe a un juego de caracteres seguro (sin comilla simple, comilla doble, backslash,
    // '$' ni caracteres de control) para que no pueda romper el literal ni el bloque DO $$ ... $$.
    private static readonly Regex SafePassword = new(@"^[A-Za-z0-9!#%^&*()\-_=+.:,;/?@ ]{12,128}$", RegexOptions.Compiled);

    public static async Task EnsureAppRoleAsync(
        ApplicationDbContext adminContext,
        string runtimeConnectionString,
        string adminConnectionString,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var runtime = new NpgsqlConnectionStringBuilder(runtimeConnectionString);
        var admin = new NpgsqlConnectionStringBuilder(adminConnectionString);

        var appUser = runtime.Username;
        var appPassword = runtime.Password;
        var dbName = admin.Database ?? runtime.Database;

        if (string.IsNullOrWhiteSpace(appUser) ||
            string.Equals(appUser, admin.Username, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "RLS: la conexión de runtime no usa un rol de aplicación separado del admin; se omite el aprovisionamiento del rol. " +
                "RLS no quedará forzada a nivel de rol hasta configurar ConnectionStrings:Postgres con un usuario NOBYPASSRLS distinto.");
            return;
        }

        if (string.IsNullOrWhiteSpace(appPassword) || !SafePassword.IsMatch(appPassword))
        {
            throw new InvalidOperationException(
                "RLS: el rol de aplicación requiere una contraseña en ConnectionStrings:Postgres de 12-128 caracteres y sin ' \" \\ $ ni caracteres de control.");
        }

        if (!SafeRoleName.IsMatch(appUser))
        {
            throw new InvalidOperationException($"RLS: nombre de rol de aplicación no válido: '{appUser}'.");
        }

        if (string.IsNullOrWhiteSpace(dbName) || !SafeDbName.IsMatch(dbName))
        {
            throw new InvalidOperationException($"RLS: nombre de base de datos no válido para los GRANT: '{dbName}'.");
        }

        var passwordLiteral = appPassword.Replace("'", "''");

        var sql = $@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{appUser}') THEN
        CREATE ROLE ""{appUser}"" WITH LOGIN NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{passwordLiteral}';
    ELSE
        ALTER ROLE ""{appUser}"" WITH LOGIN NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{passwordLiteral}';
    END IF;
END $$;

GRANT CONNECT ON DATABASE ""{dbName}"" TO ""{appUser}"";
GRANT USAGE, CREATE ON SCHEMA public TO ""{appUser}"";
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO ""{appUser}"";
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO ""{appUser}"";
GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA public TO ""{appUser}"";
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL PRIVILEGES ON TABLES TO ""{appUser}"";
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL PRIVILEGES ON SEQUENCES TO ""{appUser}"";
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL PRIVILEGES ON FUNCTIONS TO ""{appUser}"";";

        await adminContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);

        logger.LogInformation(
            "RLS: rol de aplicación '{Role}' asegurado (NOBYPASSRLS, NOSUPERUSER) con grants sobre el esquema public; contraseña sincronizada desde la configuración.",
            appUser);
    }
}
