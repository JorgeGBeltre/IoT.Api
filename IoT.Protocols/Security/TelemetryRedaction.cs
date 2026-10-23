using System.Text.Json;

namespace IoT.Protocols.Security;

/// <summary>
/// Saneado de secretos en la telemetría ANTES de persistirla.
///
/// Varios firmwares incluyen credenciales en sus tramas: FligoV0.0.1 publica
/// <c>Details.NetworkPassword</c> en claro en su reporte de configuración. Sin este filtro,
/// el adaptador genérico copiaba el campo tal cual a los atributos y, además, la trama
/// completa se guardaba en la columna <c>jsonb RawPayload</c> y se devolvía en las consultas
/// de histórico: la contraseña WiFi del cliente quedaba almacenada y consultable por API.
///
/// Se redacta por NOMBRE de campo (no por valor) y de forma recursiva, porque los nombres
/// exactos varían entre firmwares y no se puede enumerar cada uno.
/// </summary>
public static class TelemetryRedaction
{
    public const string RedactedMarker = "[REDACTED]";

    /// <summary>Fragmentos que delatan un secreto en cualquier parte del nombre.</summary>
    private static readonly string[] SecretFragments =
    {
        "password", "passwd", "contrasena", "contraseña", "secret", "token",
        "apikey", "api_key", "privatekey", "private_key", "credential",
        "authorization", "bearer", "psk", "passphrase"
    };

    /// <summary>
    /// Nombres que sólo son secretos si coinciden por completo: evita falsos positivos
    /// como "gpio_pin" o "pin_state", que no son credenciales.
    /// </summary>
    private static readonly HashSet<string> SecretExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pin", "pincode", "pin_code", "pwd", "pass", "clave"
    };

    public static bool IsSecretName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (SecretExactNames.Contains(name)) return true;

        foreach (var fragment in SecretFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Sustituye in-place el valor de todo atributo cuyo nombre delate un secreto.
    /// </summary>
    public static void RedactAttributes(IDictionary<string, string> attributes)
    {
        if (attributes.Count == 0) return;

        List<string>? toRedact = null;
        foreach (var key in attributes.Keys)
        {
            if (IsSecretName(key))
            {
                (toRedact ??= new List<string>()).Add(key);
            }
        }

        if (toRedact is null) return;
        foreach (var key in toRedact)
        {
            attributes[key] = RedactedMarker;
        }
    }

    /// <summary>
    /// Devuelve el payload con los campos sensibles sustituidos. Si no es JSON (tramas
    /// binarias en hexadecimal, ASCII de trackers, texto plano) se devuelve sin tocar:
    /// esos protocolos no transportan credenciales en la telemetría.
    /// </summary>
    public static string? RedactPayload(string? rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload)) return rawPayload;

        // Atajo: si ningún nombre sensible aparece en el texto, no hay nada que reescribir.
        bool mayContainSecret = false;
        foreach (var fragment in SecretFragments)
        {
            if (rawPayload.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                mayContainSecret = true;
                break;
            }
        }
        if (!mayContainSecret)
        {
            foreach (var name in SecretExactNames)
            {
                if (rawPayload.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    mayContainSecret = true;
                    break;
                }
            }
        }
        if (!mayContainSecret) return rawPayload;

        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            var buffer = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteRedacted(doc.RootElement, writer);
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            // No es JSON válido: se deja como está (no se puede reescribir con garantías).
            return rawPayload;
        }
    }

    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (IsSecretName(property.Name))
                    {
                        writer.WriteString(property.Name, RedactedMarker);
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteRedacted(property.Value, writer);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedacted(item, writer);
                }
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
