using System.Text.Json;
using System.Text.Json.Serialization;
using IoT.Domain.Enums;

namespace IoT.Application.Common.Serialization;

/// <summary>
/// Deserializa listas de <see cref="CapabilityType"/> aceptando indistintamente valores numéricos
/// (<c>[1, 2]</c>), nombres de enumeración (<c>["Switch", "Gps"]</c>, sin distinción de mayúsculas)
/// y alias de categoría enviados por clientes web/móviles (<c>"SmartSwitch"</c>, <c>"GlpSensor"</c>,
/// <c>"TransitValidator"</c>), ignorando etiquetas decorativas desconocidas sin provocar un HTTP 400
/// de model binding.
/// </summary>
public sealed class TolerantCapabilityListJsonConverter : JsonConverter<IReadOnlyList<CapabilityType>?>
{
    public override IReadOnlyList<CapabilityType>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Se esperaba un arreglo JSON para 'capabilities'.");
        }

        var result = new List<CapabilityType>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetInt32(out int code) && Enum.IsDefined(typeof(CapabilityType), code))
                {
                    AddUnique(result, (CapabilityType)code);
                }
                continue;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                string? raw = reader.GetString()?.Trim();
                if (TryMapCapabilityString(raw, out var mapped))
                {
                    AddUnique(result, mapped);
                }
                continue;
            }

            reader.Skip();
        }

        return result;
    }

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlyList<CapabilityType>? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item.ToString());
        }
        writer.WriteEndArray();
    }

    private static void AddUnique(List<CapabilityType> target, CapabilityType item)
    {
        if (!target.Contains(item))
        {
            target.Add(item);
        }
    }

    private static bool TryMapCapabilityString(string? raw, out CapabilityType capability)
    {
        capability = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (int.TryParse(raw, out int numeric) && Enum.IsDefined(typeof(CapabilityType), numeric))
        {
            capability = (CapabilityType)numeric;
            return true;
        }

        if (Enum.TryParse<CapabilityType>(raw, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(typeof(CapabilityType), parsed))
        {
            capability = parsed;
            return true;
        }

        string normalized = raw.Trim().Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
        switch (normalized)
        {
            case "smartswitch":
            case "relay":
            case "relaywifi":
            case "relaycutoff":
            case "interruptor":
                capability = CapabilityType.Switch;
                return true;

            case "gpstracker":
            case "tracker":
            case "location":
                capability = CapabilityType.Gps;
                return true;

            case "glpsensor":
            case "glp":
            case "telemetry":
            case "level":
                capability = CapabilityType.LevelTelemetry;
                return true;

            case "transitvalidator":
            case "validator":
            case "nfc":
                capability = CapabilityType.NfcTicket;
                return true;

            case "qr":
                capability = CapabilityType.QrReader;
                return true;

            default:
                return false;
        }
    }
}

