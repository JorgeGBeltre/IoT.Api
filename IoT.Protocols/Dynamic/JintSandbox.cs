using Acornima.Ast;
using Jint;

namespace IoT.Protocols.Dynamic;

/// <summary>
/// Fábrica ÚNICA del motor Jint con el que se ejecuta cualquier script de usuario (validación y
/// runtime). Centralizar aquí los límites garantiza que TODA ejecución de JS de terceros pase por el
/// mismo sandbox, sin que un call-site se olvide de alguno:
///   - <see cref="MaxMemoryBytes"/>: memoria máxima por ejecución (bucles de asignación);
///   - <see cref="Timeout"/>: tiempo de pared máximo (bucles lentos / esperas);
///   - <see cref="MaxStatements"/>: sentencias máximas (bucles infinitos).
///
/// SEGURIDAD: NO se habilita el interop con el CLR (no se llama a <c>AllowClr</c>), de modo que el
/// script NO puede instanciar tipos .NET, hacer E/S, red ni reflexión: sólo JavaScript puro sobre los
/// argumentos que se le inyectan. No añadir <c>AllowClr</c>, módulos del sistema de ficheros ni acceso
/// a red a este motor.
/// </summary>
public static class JintSandbox
{
    public const int MaxMemoryBytes = 4 * 1024 * 1024; // 4 MB por ejecución
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    public const int MaxStatements = 5000;

    /// <summary>
    /// Profundidad máxima de recursión. Una recursión más honda lanza
    /// <see cref="Jint.Runtime.RecursionDepthOverflowException"/> (CAPTURABLE por el try/catch del
    /// convertidor y por el fail-safe de la ingesta) ANTES de agotar la pila del CLR y provocar un
    /// <see cref="StackOverflowException"/> (NO capturable, que tumbaría el proceso worker de ingesta
    /// para TODOS los tenants). 64 es holgado para un decode()/encode() legítimo y está muy por debajo
    /// del límite de pila nativa.
    /// </summary>
    public const int MaxRecursionDepth = 64;

    public static Engine CreateEngine() => new(options =>
    {
        options.LimitMemory(MaxMemoryBytes);
        options.TimeoutInterval(Timeout);
        options.MaxStatements(MaxStatements);

        // Recursión acotada: convierte un desbordamiento de pila (no capturable) en una excepción de
        // Jint capturable, de modo que el fail-safe pueda contenerla y caer al decodificador nativo.
        options.LimitRecursion(MaxRecursionDepth);

        // Presupuesto de regex acotado para la RUTA NO PREPARADA (engine.Execute(string)): el
        // TimeoutInterval se evalúa en LÍMITES de sentencia y NO interrumpe un único regex de
        // backtracking catastrófico (ReDoS). Options.Constraints.RegexTimeout fija el timeout del Regex
        // .NET en este caso. OJO: para scripts PREPARADOS (Engine.PrepareScript) Jint IGNORA este ajuste
        // y hornea el default (10 s) en tiempo de preparación; esa ruta se cubre en PrepareScript() más
        // abajo mediante las opciones de parseo. Mantener ambos al mismo presupuesto.
        options.Constraints.RegexTimeout = Timeout;

        // Deliberadamente SIN AllowClr(): sin interop .NET, sin E/S, sin red, sin reflexión.
    });

    /// <summary>
    /// Opciones de preparación/parseo compartidas por TODA ruta preparada. Clave de seguridad: para
    /// scripts preparados, Jint 4.17 IGNORA <see cref="Jint.Options.ConstraintOptions.RegexTimeout"/> y
    /// usa el default de 10 s salvo que <see cref="Jint.IParsingOptions.RegexTimeout"/> se fije en la
    /// PREPARACIÓN. Al fijarlo aquí al mismo <see cref="Timeout"/> (2 s), la Regex .NET precompilada del
    /// AST preparado honra el presupuesto de 2 s en vez de 10 s (cierre ReDoS en la ruta caliente).
    /// Instancia única y reutilizada: la caché de opciones de parser de Jint es por-instancia (clave
    /// débil), así que compartir la misma instancia evita clones redundantes al preparar repetidamente.
    /// </summary>
    private static readonly ScriptPreparationOptions PreparationOptions = new()
    {
        ParsingOptions = new ScriptParsingOptions { RegexTimeout = Timeout },
    };

    /// <summary>
    /// Prepara (análisis estático de Jint) un script reutilizable y thread-safe para la ruta caliente,
    /// con el presupuesto de regex fijado al mismo <see cref="Timeout"/> que la ruta no preparada. Punto
    /// ÚNICO de preparación: preparación y ejecución comparten el mismo presupuesto, sin que ningún
    /// call-site olvide el timeout del regex.
    /// </summary>
    public static Prepared<Script> PrepareScript(string script)
        => Engine.PrepareScript(script, options: PreparationOptions);
}
