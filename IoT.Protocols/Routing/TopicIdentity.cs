namespace IoT.Protocols.Routing;

/// <summary>
/// Identidad del dispositivo deducida del tópico MQTT.
///
/// Varios firmwares publican en tópicos FIJOS que no llevan identificador
/// (<c>telemetry/accidents</c>, <c>TicketValidationRequest</c>, <c>device_switch1_state</c>)
/// o lo ponen en un segmento que no es el segundo (<c>glp/status/GLP-001</c>,
/// <c>ota/&lt;dev&gt;/progress</c>). Tomar el segmento equivocado creaba dispositivos
/// fantasma ("accidents", "progress", "status") que el pipeline rechazaba por no
/// aprovisionados, escribiendo una fila de auditoría por cada trama.
///
/// La regla es: si no hay certeza, NO se devuelve identidad. Entonces manda la identidad
/// autenticada del transporte (ver IngestTelemetryCommand.TransportDeviceIdentifier).
/// </summary>
public static class TopicIdentity
{
    /// <summary>
    /// Segmentos que forman parte del protocolo, no de la identidad.
    /// </summary>
    private static readonly HashSet<string> ProtocolWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "telemetry", "telemetria", "status", "state", "config", "configuration",
        "events", "event", "command", "commands", "cmd", "ota", "firmware",
        "get", "set", "accidents", "accident", "progress", "error", "success",
        "ack", "stats", "will", "lwt", "data", "alarm", "alarms", "heartbeat",
        "ticket", "request", "response", "gps", "devices", "device"
    };

    /// <summary>
    /// Prefijos de tópico cuyo formato conocemos y en los que, por tanto, sí se puede
    /// localizar el identificador con seguridad.
    /// </summary>
    private static readonly HashSet<string> KnownPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "devices", "device", "event", "events", "status", "ticket",
        "telemetry", "telemetria", "glp", "ota", "esp32"
    };

    public static bool IsProtocolWord(string segment) =>
        !string.IsNullOrEmpty(segment) && ProtocolWords.Contains(segment);

    /// <summary>
    /// Devuelve el identificador del dispositivo contenido en el tópico, o null si el
    /// tópico no lo lleva o no se puede determinar con certeza.
    /// </summary>
    public static string? ExtractDeviceId(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return null;

        var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !KnownPrefixes.Contains(parts[0]))
            return null;

        // El identificador es el primer segmento que no sea palabra de protocolo: cubre
        // <prefijo>/<id>, <prefijo>/<id>/<sufijo> y <prefijo>/<sub>/<id>.
        for (int i = 1; i < parts.Length; i++)
        {
            if (!ProtocolWords.Contains(parts[i]))
                return parts[i];
        }

        return null;
    }
}
