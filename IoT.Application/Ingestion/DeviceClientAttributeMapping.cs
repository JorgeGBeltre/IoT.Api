using System.Globalization;
using IoT.Protocols.Contracts;

namespace IoT.Application.Ingestion;

/// <summary>
/// Extrae, de un <see cref="NormalizedTelemetryEvent"/> ya decodificado, el subconjunto de valores que
/// el EQUIPO reporta y que se materializan como atributos de ámbito CLIENT (ADR-014, P1b). NO inventa
/// claves: usa EXACTAMENTE las que el pipeline canónico ya coloca en el evento (ver
/// <c>CanonicalDeviceTelemetry.ToNormalizedEvent</c>).
/// <para>
/// Un atributo CLIENT es el ÚLTIMO VALOR CONOCIDO de un campo que cambia LENTO: es un metadato ESTABLE
/// de identidad/firmware, no una serie temporal. Por eso el allow-list es SÓLO de cadenas estables desde
/// <see cref="NormalizedTelemetryEvent.Attributes"/>: <c>firmware_version</c>, <c>imei</c>, <c>iccid</c>,
/// <c>imsi</c> (el equipo las pone cuando las reporta; <c>iccid</c>/<c>imsi</c> sólo aparecen si el
/// firmware las incluye en los atributos adicionales).
/// </para>
/// <para>
/// Las métricas numéricas VOLÁTILES (<c>battery</c>, <c>rssi</c>, …) quedan DELIBERADAMENTE FUERA: cambian
/// casi en cada trama, de modo que el filtro change-only (<see cref="ComputeChanges"/>) no evita la
/// escritura y cada trama acabaría reescribiendo la fila del atributo (amplificación de escritura sobre
/// <c>device_attributes</c>). Además ya se persisten como SERIE en <c>telemetry_records</c>, que es su
/// modelo correcto; el "último valor" de una métrica volátil se consulta de esa serie, no de un atributo.
/// Un valor numérico sólo pertenecería a CLIENT si fuese un metadato estable (cambia rara vez); hoy no
/// hay ninguno, por lo que <see cref="NumericMetricKeys"/> está vacío (punto de extensión documentado).
/// </para>
/// </summary>
public static class DeviceClientAttributeMapping
{
    /// <summary>Claves de cadena (en <c>ev.Attributes</c>) que el equipo reporta como atributos CLIENT.</summary>
    private static readonly string[] StringAttributeKeys =
    {
        "firmware_version",
        "hardware_version",
        "serial_number",
        "protocol_version",
        "imei",
        "iccid",
        "imsi",
        "wifi_ssid",
        "wifi_bssid",
        "local_ip",
        "startup",
        "pulse",
        "pulseWidth",
        "sledOnline",
        "account_bound",
        "channel_count"
    };

    /// <summary>
    /// Claves numéricas (en <c>ev.NumericMetrics</c>) que serían atributos CLIENT. VACÍO a propósito:
    /// sólo cabría aquí un metadato numérico ESTABLE (que cambie rara vez). Las métricas volátiles como
    /// <c>battery</c>/<c>rssi</c> NO van aquí —ya son serie en <c>telemetry_records</c>— porque
    /// reescribirían el atributo en cada trama. Añadir una clave sólo si su valor cambia LENTO.
    /// </summary>
    private static readonly string[] NumericMetricKeys = System.Array.Empty<string>();

    /// <summary>
    /// Devuelve el mapa clave-&gt;valor (como cadena) de atributos CLIENT presentes en el evento. Sólo
    /// incluye las claves del allow-list que vengan informadas y no vacías. Preserva el orden de
    /// declaración para que el diff sea estable.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Extract(NormalizedTelemetryEvent ev)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in StringAttributeKeys)
        {
            if (ev.Attributes.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                result[key] = value;
            }
        }

        foreach (var key in NumericMetricKeys)
        {
            if (ev.NumericMetrics.TryGetValue(key, out var value))
            {
                result[key] = value.ToString(CultureInfo.InvariantCulture);
            }
        }

        return result;
    }

    /// <summary>
    /// A partir de los atributos CLIENT reportados (<paramref name="reported"/>) y los YA almacenados
    /// (<paramref name="current"/>, clave-&gt;valor), devuelve SÓLO las entradas que hay que persistir
    /// porque faltan o CAMBIARON de valor. Evita reescribir en cada trama (telemetría caliente): si el
    /// valor coincide con el almacenado, no se emite.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ComputeChanges(
        IReadOnlyDictionary<string, string> reported,
        IReadOnlyDictionary<string, string> current)
    {
        var changes = new List<KeyValuePair<string, string>>();
        foreach (var (key, value) in reported)
        {
            if (!current.TryGetValue(key, out var existing) || !string.Equals(existing, value, StringComparison.Ordinal))
            {
                changes.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        return changes;
    }
}
