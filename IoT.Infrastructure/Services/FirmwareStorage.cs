using System.Text.RegularExpressions;

namespace IoT.Infrastructure.Services;

/// <summary>
/// Resolución de rutas del almacén de firmware. Existe para que NINGUNA cadena que venga
/// de fuera construya una ruta de disco sin pasar por aquí.
///
/// El riesgo es concreto: la versión de firmware la escribe quien sube el archivo y se usaba
/// tal cual en <c>Path.Combine</c>. Una versión como <c>..\..\..\algo</c> se sale del almacén,
/// y una ruta absoluta (<c>C:\...</c> o <c>/etc/...</c>) hace que <c>Path.Combine</c>
/// DESCARTE la base y escriba donde diga el atacante. Con eso un operador autenticado podía
/// crear directorios y escribir un archivo con contenido elegido fuera del almacén, o pisar
/// el firmware de OTRO tenant (<c>../{otroTenant}/1.0.0</c>). Y como la ruta guardada se lee
/// después para publicar los trozos a los dispositivos, el fallo llegaba hasta el equipo.
/// </summary>
public static class FirmwareStorage
{
    /// <summary>Carpeta raíz del almacén, relativa al directorio de la aplicación.</summary>
    public const string RootFolder = "storage";
    public const string FirmwareFolder = "firmware";

    /// <summary>
    /// Versión admisible: empieza por alfanumérico y sigue con alfanuméricos, punto, guion
    /// o guion bajo. Sin separadores de ruta, sin ".." y sin dos puntos de unidad.
    /// </summary>
    private static readonly Regex SafeVersion = new(
        @"^[0-9A-Za-z][0-9A-Za-z._-]{0,49}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Normaliza la versión y dice si es utilizable en una ruta.</summary>
    public static bool TryNormalizeVersion(string? version, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(version)) return false;

        string candidate = version.Trim().TrimStart('v', 'V').Trim();
        if (candidate.Length == 0) return false;
        if (candidate.Contains("..", StringComparison.Ordinal)) return false;
        if (!SafeVersion.IsMatch(candidate)) return false;

        normalized = candidate;
        return true;
    }

    /// <summary>Raíz absoluta del almacén de firmware de un tenant.</summary>
    public static string TenantRoot(Guid tenantId) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, RootFolder, FirmwareFolder, tenantId.ToString()));

    /// <summary>
    /// Nombre de archivo generado por el SERVIDOR a partir del hash del contenido: la cadena
    /// que mandó el cliente no participa en el nombre.
    /// </summary>
    public static string FileNameFor(string sha256Hex, string extension)
    {
        string shortHash = (sha256Hex ?? string.Empty).Length >= 16
            ? sha256Hex![..16]
            : Guid.NewGuid().ToString("N")[..16];
        return $"firmware_{shortHash}{extension}";
    }

    /// <summary>
    /// Ruta RELATIVA donde guardar la imagen, ya validada y contenida en el almacén del
    /// tenant. Devuelve false si la versión no es apta o si la ruta resultante se sale.
    /// </summary>
    public static bool TryBuildRelativeFilePath(
        Guid tenantId,
        string version,
        string sha256Hex,
        string extension,
        out string relativeFilePath,
        out string absoluteFilePath)
    {
        relativeFilePath = string.Empty;
        absoluteFilePath = string.Empty;

        if (!TryNormalizeVersion(version, out var normalizedVersion)) return false;

        string candidateRelative = Path.Combine(
            RootFolder, FirmwareFolder, tenantId.ToString(), normalizedVersion,
            FileNameFor(sha256Hex, extension));

        if (!TryResolveWithinTenant(tenantId, candidateRelative, out var resolved)) return false;

        relativeFilePath = candidateRelative;
        absoluteFilePath = resolved;
        return true;
    }

    /// <summary>
    /// Resuelve una ruta relativa GUARDADA y comprueba que sigue dentro del almacén del
    /// tenant. Se aplica también al LEER: la ruta viene de base de datos, y una fila escrita
    /// antes de esta validación (o por otra vía) no es de fiar.
    /// </summary>
    public static bool TryResolveWithinTenant(Guid tenantId, string? relativePath, out string absolutePath)
    {
        absolutePath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        // Una ruta absoluta o con raíz haría que Path.Combine descartara la base.
        if (Path.IsPathRooted(relativePath)) return false;

        string tenantRoot = TenantRoot(tenantId);
        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath));
        }
        catch (Exception)
        {
            // Caracteres inválidos para el sistema de archivos.
            return false;
        }

        string prefix = tenantRoot.EndsWith(Path.DirectorySeparatorChar)
            ? tenantRoot
            : tenantRoot + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        absolutePath = resolved;
        return true;
    }
}
