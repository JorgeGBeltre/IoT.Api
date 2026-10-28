using IoT.Domain.Common;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace IoT.Protocols.Dynamic;

/// <summary>
/// Validación en sandbox de los scripts de convertidor ANTES de persistirlos. Compila y ejecuta el
/// cuerpo del script dentro del mismo sandbox Jint del runtime (<see cref="JintSandbox"/>), comprueba
/// que define la función esperada y la EJECUTA contra un payload de ejemplo. Rechaza JavaScript
/// inválido o que exceda los límites del sandbox (memoria/tiempo/sentencias/recursión) SIN que nada se
/// guarde. Nunca ejecuta el script fuera del sandbox.
///
/// IMPORTANTE: la prueba de ejecución se corre DIRECTAMENTE sobre el sandbox (no a través del
/// convertidor), porque el convertidor envuelve toda excepción en un <see cref="Error"/>
/// (<c>ScriptExecutionException</c>) y perdería el TIPO real de la excepción de Jint. Clasificar por
/// tipo —y no por subcadena del mensaje— es lo que permite rechazar un decode()/encode() cuyo
/// bucle/recursión/límite está EN EL CUERPO (no a nivel superior).
/// </summary>
public static class ScriptConverterValidator
{
    private const string SampleTopic = "validation/sample";
    private const string DefaultSamplePayload = "{}";
    private const string SandboxExceededMessage =
        "El script excede los límites del sandbox (memoria/tiempo/sentencias/recursión).";

    /// <summary>Valida un script de subida: debe compilar, definir <c>decode(payload, topic, metadata)</c> y no exceder el sandbox al ejecutarse.</summary>
    public static Result ValidateUplink(string? script, string? samplePayload = null)
        => CompileAndRunSample(script, "decode", (engine, fn) =>
        {
            var metadata = new JsObject(engine);
            metadata.Set("topic", SampleTopic);
            engine.Invoke(fn, samplePayload ?? DefaultSamplePayload, SampleTopic, metadata);
        });

    /// <summary>Valida un script de bajada: debe compilar, definir <c>encode(commandName, params, sequence)</c> y no exceder el sandbox al ejecutarse.</summary>
    public static Result ValidateDownlink(string? script)
        => CompileAndRunSample(script, "encode", (engine, fn) =>
        {
            var paramsObj = new JsObject(engine);
            engine.Invoke(fn, "VALIDATE", paramsObj, 1);
        });

    /// <summary>
    /// Compila el cuerpo del script en el sandbox, verifica que declara la función invocable con el
    /// nombre dado y la ejecuta una vez contra el payload de ejemplo. La clasificación es por TIPO de
    /// excepción de Jint:
    ///   - límites del sandbox (memoria/sentencias/recursión/tiempo) ⇒ rechazo (<c>SandboxExceeded</c>);
    ///   - error de sintaxis / JS en tiempo de compilación ⇒ rechazo (<c>InvalidScript</c>);
    ///   - error de EJECUCIÓN de la muestra ajeno al sandbox (p.ej. el ejemplo no tiene la forma que el
    ///     script espera) ⇒ se ACEPTA (no invalida el script), igual que antes.
    /// </summary>
    private static Result CompileAndRunSample(string? script, string functionName, Action<Engine, JsValue> runSample)
    {
        if (string.IsNullOrWhiteSpace(script))
            return Result.Failure(new Error("ConverterScript.Empty", "El script no puede estar vacío."));

        Engine engine;
        JsValue fn;
        try
        {
            engine = JintSandbox.CreateEngine();
            engine.Execute(script);

            fn = engine.GetValue(functionName);
            if (fn.IsUndefined() || !fn.IsCallable())
            {
                return Result.Failure(new Error(
                    "ConverterScript.MissingFunction",
                    $"El script debe definir una función '{functionName}'."));
            }
        }
        catch (Exception ex) when (IsSandboxLimit(ex))
        {
            // Abuso del sandbox a nivel SUPERIOR (p.ej. un bucle infinito fuera de la función).
            return Result.Failure(new Error("ConverterScript.SandboxExceeded", SandboxExceededMessage));
        }
        catch (JavaScriptException ex)
        {
            return Result.Failure(new Error("ConverterScript.InvalidScript", $"JavaScript inválido: {ex.Message}"));
        }
        catch (Exception ex)
        {
            // Incluye errores de parseo/sintaxis (Acornima.ParseErrorException) y otros fallos de compilación.
            return Result.Failure(new Error("ConverterScript.InvalidScript", $"No se pudo compilar el script: {ex.Message}"));
        }

        // Ejecución de prueba del CUERPO contra el payload de ejemplo, en el MISMO sandbox. Al correr
        // directamente sobre el motor (no vía el convertidor), el TIPO real de la excepción de Jint se
        // propaga y puede clasificarse: un bucle/recursión/límite en el cuerpo se RECHAZA aquí.
        try
        {
            runSample(engine, fn);
        }
        catch (Exception ex) when (IsSandboxLimit(ex))
        {
            return Result.Failure(new Error("ConverterScript.SandboxExceeded", SandboxExceededMessage));
        }
        catch (Exception)
        {
            // Fallo de EJECUCIÓN ajeno al sandbox: el ejemplo puede no tener la forma esperada. No
            // invalida el script (se acepta), como antes.
        }

        return Result.Success();
    }

    private static bool IsSandboxLimit(Exception ex) =>
        ex is MemoryLimitExceededException
            or StatementsCountOverflowException
            or RecursionDepthOverflowException
            or ExecutionCanceledException
            or TimeoutException;
}
