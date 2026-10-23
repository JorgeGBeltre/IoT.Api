using System.Text.Json;

namespace IoT.Infrastructure.Persistence;

/// <summary>
/// Normaliza texto para columnas <c>jsonb</c> de Postgres.
///
/// La columna <c>telemetry_records.RawPayload</c> es <c>jsonb</c> (ver
/// <see cref="ApplicationDbContext"/>), así que Postgres rechaza con
/// <c>22P02: invalid input syntax for type json</c> cualquier trama que no sea un documento
/// JSON válido. Y muchos dispositivos NO envían JSON:
///   - el relé publica el estado como texto plano ("on"/"off");
///   - los trackers Coban/iStartek/Queclink envían ASCII delimitado (<c>imei:...,...;</c>);
///   - los binarios GT06/Concox llegan como volcado hexadecimal.
/// Esa telemetría se decodificaba bien y se perdía AL GUARDARLA.
///
/// En vez de cambiar el tipo de la columna (perdiendo la capacidad de consultar el JSON) o
/// descartar el original, lo que no sea JSON se envuelve como cadena JSON, que sí es un
/// documento jsonb válido y conserva el contenido íntegro.
/// </summary>
public static class JsonbText
{
    /// <summary>
    /// Devuelve <paramref name="raw"/> si ya es un documento JSON válido; si no, su
    /// representación como cadena JSON. Null o vacío devuelven null.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string trimmed = raw.TrimStart();
        if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
        {
            try
            {
                using var _ = JsonDocument.Parse(raw);
                return raw;
            }
            catch (JsonException)
            {
                // Parece JSON pero está mal formado: se guarda como cadena para no perderlo.
            }
        }

        return JsonSerializer.Serialize(raw);
    }
}
