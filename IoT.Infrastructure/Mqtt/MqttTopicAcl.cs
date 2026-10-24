namespace IoT.Infrastructure.Mqtt;

/// <summary>
/// Control de acceso por TÓPICO para el broker embebido.
///
/// Hasta ahora el broker sólo validaba la CONEXIÓN: una vez dentro, cualquier dispositivo
/// podía suscribirse a los tópicos de otro. Con la actualización OTA eso es grave, porque el
/// firmware se publica troceado en <c>ota/{chipId}</c>: un equipo autenticado podía
/// suscribirse al <c>ota/</c> de OTRO —de otro tenant incluido— y descargarse su imagen.
/// Lo mismo con <c>event/{chipId}</c> y <c>commands/{chipId}</c>, por los que van los comandos.
///
/// Hay tres clases de cliente:
///  - <b>Servidor</b> (<see cref="ServerClientId"/>): publica donde necesite; es quien envía
///    firmware y comandos.
///  - <b>Dispositivo</b> (autenticado con la ApiKey PROPIA): sólo sus tópicos. Puede leer su
///    canal de bajada y publicar sus reportes, nunca los de otro.
///  - <b>Credencial global de bootstrap</b> (simulador y herramientas del operador): puede
///    publicar telemetría en nombre de varios equipos —es su función— pero NO puede leer
///    canales de bajada ni suscribirse a tópicos de un dispositivo concreto.
/// </summary>
public static class MqttTopicAcl
{
    /// <summary>Cliente interno con el que el servidor inyecta sus propias publicaciones.</summary>
    public const string ServerClientId = "internal-server";

    /// <summary>
    /// Prefijos de tópico que llevan el identificador del dispositivo en el segundo segmento.
    /// </summary>
    private static readonly string[] DeviceScopedPrefixes =
    {
        "ota", "event", "events", "commands", "command", "devices", "device",
        "telemetry", "telemetria", "status", "glp",
        // El firmware GPS se suscribe a "config/<chipId>" (antes al literal "config",
        // compartido por todos los equipos).
        "config"
    };

    /// <summary>
    /// Tópicos de BAJADA: los escribe el servidor y el dispositivo sólo los lee. Si un
    /// dispositivo pudiera publicar aquí, podría inyectar un firmware o un comando a otro.
    /// </summary>
    private static readonly string[] DownlinkPrefixes = { "ota", "event", "commands", "command", "config" };

    /// <summary>
    /// Tópicos FIJOS, sin identificador, en los que Fligo y Toour publicaban sus reportes.
    /// Esos firmwares YA se migraron a devices/{chipId}/{ticket-validation,config-validation,
    /// ota-error}, así que esta lista sólo cubre la TRANSICIÓN: los equipos que aún no se han
    /// re-flasheado siguen pudiendo publicar aquí y su trama se atribuye por la identidad
    /// autenticada de la conexión. Suscribirse nunca se permite, porque son compartidos.
    /// Cuando no quede ningún equipo con firmware antiguo, esta lista se puede vaciar.
    /// </summary>
    private static readonly HashSet<string> PublishOnlySharedTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        "TicketValidationRequest",
        "DeviceConfigValidationRequest",
        "ErrorUpdateFirmwareDevice"
    };

    public enum Decision
    {
        Allow,
        /// <summary>Permitido, pero es un tópico compartido sin aislamiento entre equipos.</summary>
        AllowSharedLegacy,
        Deny
    }

    /// <summary>
    /// Extrae el identificador de dispositivo de un tópico con ámbito por equipo, o null si
    /// el tópico no lo lleva.
    /// </summary>
    public static string? DeviceIdOf(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return null;

        var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        if (!DeviceScopedPrefixes.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) return null;

        // glp/<sub>/<id>: el identificador va en el tercer segmento.
        if (parts[0].Equals("glp", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
            return parts[2];

        return parts[1];
    }

    /// <summary>
    /// ¿Es un canal de BAJADA, es decir, de los que escribe el servidor y el dispositivo sólo
    /// lee? No basta mirar el primer segmento: los firmwares reales se suscriben también a
    ///   · <c>glp/config/{id}</c>                        (GLP_Presure/mqtt_client.cpp:49-50)
    ///   · <c>devices/{id}/{base}/command</c> y <c>/state</c> (Relay_Wifi_Esp32, tras pasar a
    ///     tópicos por dispositivo)
    /// Clasificarlos mal dejaba que un cliente que no es el servidor publicara órdenes a un
    /// equipo ajeno.
    ///
    /// OJO: <c>devices/{id}/{base}_state</c> (guion bajo) es BAJADA — el servidor fija ahí el
    /// estado deseado y el firmware obedece. El equipo reporta su estado real en 
    /// <c>{base}/report</c>, que sí es subida.
    /// </summary>
    private static bool IsDownlink(string topic)
    {
        var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        if (DownlinkPrefixes.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) return true;

        // glp/config/<id>: canal de configuración del tanque.
        if (parts.Length >= 2 &&
            parts[0].Equals("glp", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // devices/<id>/.../command y devices/<id>/.../state (último segmento exacto).
        if (parts.Length >= 3 &&
            (parts[0].Equals("devices", StringComparison.OrdinalIgnoreCase) ||
             parts[0].Equals("device", StringComparison.OrdinalIgnoreCase)))
        {
            string last = parts[^1];
            if (last.Equals("command", StringComparison.OrdinalIgnoreCase) ||
                last.Equals("commands", StringComparison.OrdinalIgnoreCase) ||
                last.Equals("state", StringComparison.OrdinalIgnoreCase) ||
                last.Equals("config", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // "<base>_state" (guion bajo) es el canal por el que el SERVIDOR fija el estado
            // deseado de un relé, y el firmware OBEDECE lo que llegue ahí
            // (RelayFirmwareController, rama topic == stateTopic -> applyDesiredState).
            // Estaba clasificado como SUBIDA porque el equipo también publicaba su estado
            // en ese mismo tópico; ya no lo hace (reporta en "<base>/report"), así que aquí
            // es bajada sin ambigüedad. Mientras fue subida, cualquier cliente autorizado a
            // publicar telemetría —incluida la credencial global— podía ACCIONAR FÍSICAMENTE
            // el relé de cualquier equipo de cualquier tenant.
            if (last.EndsWith("_state", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Owns(string? authenticatedChipId, string owner) =>
        !string.IsNullOrWhiteSpace(authenticatedChipId) &&
        string.Equals(owner, authenticatedChipId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ¿Puede el cliente SUSCRIBIRSE a <paramref name="topic"/>?
    /// </summary>
    /// <param name="authenticatedChipId">
    /// ChipId verificado contra el hash de la ApiKey del dispositivo, o null si la conexión
    /// no usó una credencial por dispositivo.
    /// </param>
    /// <param name="isBootstrapCredential">
    /// true si la conexión se autenticó con la ApiKey GLOBAL (simulador / herramientas).
    /// </param>
    public static Decision CanSubscribe(string topic, string? authenticatedChipId, bool isBootstrapCredential = false)
    {
        if (string.IsNullOrWhiteSpace(topic)) return Decision.Deny;

        // Cualquier comodín que no esté acotado BAJO el propio identificador se rechaza.
        // Un simple "#" abarca todo el broker, incluidos los canales de firmware de todos
        // los equipos. Los firmwares reales se suscriben a tópicos literales (MQTTOTAv5
        // exige coincidencia exacta de cadena), así que esto no les quita nada.
        if (topic.Contains('#') || topic.Contains('+'))
        {
            string? wildcardOwner = DeviceIdOf(topic);
            bool ownedWildcard =
                wildcardOwner is not null &&
                wildcardOwner is not "+" and not "#" &&
                Owns(authenticatedChipId, wildcardOwner);

            if (!ownedWildcard) return Decision.Deny;
        }

        string? owner = DeviceIdOf(topic);

        if (owner is not null)
        {
            // Una credencial global no representa a NINGÚN equipo concreto: no tiene por qué
            // leer el canal de uno.
            if (isBootstrapCredential) return Decision.Deny;
            return Owns(authenticatedChipId, owner) ? Decision.Allow : Decision.Deny;
        }

        // Sin identificador en el tópico no se puede comprobar la propiedad, así que no se
        // concede lectura: incluye los tópicos fijos de Fligo/Toour, donde se publican
        // reportes de configuración de otros equipos.
        return Decision.Deny;
    }

    /// <summary>
    /// ¿Puede el cliente PUBLICAR en <paramref name="topic"/>?
    /// </summary>
    public static Decision CanPublish(
        string topic,
        bool isServerOrigin,
        string? authenticatedChipId,
        bool isBootstrapCredential = false)
    {
        if (string.IsNullOrWhiteSpace(topic)) return Decision.Deny;

        // El servidor publica donde necesite: es quien envía firmware y comandos.
        // El privilegio NO se deduce del ClientId —lo elige quien conecta— sino de que la
        // publicación venga inyectada en proceso, sin sesión externa detrás.
        if (isServerOrigin) return Decision.Allow;

        string? owner = DeviceIdOf(topic);

        if (owner is not null && IsDownlink(topic))
        {
            // Bajada: es del servidor. La ÚNICA excepción son los reportes que el propio
            // dispositivo publica bajo su rama de OTA (ota/<yo>/progress|error|success|state),
            // que es lo que la librería del firmware emite. Cualquier otro tópico de bajada
            // se deniega a todo cliente que no sea el servidor —incluida la credencial
            // global—: antes, un `glp/config/<yo>` o un `devices/<yo>/x/command` se colaban
            // por ser "del propio equipo", lo que le permitía darse órdenes a sí mismo.
            var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
            bool isOwnOtaReport =
                parts.Length >= 3 &&
                parts[0].Equals("ota", StringComparison.OrdinalIgnoreCase) &&
                Owns(authenticatedChipId, owner);

            return isOwnOtaReport ? Decision.Allow : Decision.Deny;
        }

        if (owner is not null)
        {
            // La credencial global existe para publicar en nombre de varios equipos
            // (simulador): es su función y no es un canal de bajada.
            if (isBootstrapCredential) return Decision.Allow;
            return Owns(authenticatedChipId, owner) ? Decision.Allow : Decision.Deny;
        }

        // Tópicos fijos de firmwares que todavía los usan: se permite publicar y se marca
        // como compartido para que quede registrado que ahí no hay aislamiento.
        if (PublishOnlySharedTopics.Contains(topic.Trim('/'))) return Decision.AllowSharedLegacy;

        // Otros tópicos sin identificador: se admite la subida (la plataforma atribuye la
        // trama por la identidad autenticada de la conexión, no por el tópico) y se marca
        // como compartida.
        return Decision.AllowSharedLegacy;
    }
}
