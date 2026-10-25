using System.Security.Cryptography;
using System.Text.Json;

namespace IoT.Protocols.Ota;

/// <summary>
/// Troceado y empaquetado de una imagen de firmware para la actualización OTA por MQTT.
///
/// El contrato lo manda el DISPOSITIVO, no el servidor (la plataforma se adapta, los
/// firmwares no se tocan). Transcrito de la librería real
/// <c>References/Dispositivos/MQTTOTAv5/MQTTOTAv5.cpp</c>:
///
///  - Tópico de entrada del equipo: <c>ota/{clave}</c>, suscripción ÚNICA y por coincidencia
///    EXACTA de cadena, sin comodines (MQTTOTAv5.cpp:100-104). El broker de referencia
///    publicaba en <c>event/{chipId}</c> (MqttServerService.cs:62), tópico al que la librería
///    NO está suscrita: por eso su OTA nunca llegaba al manejador del dispositivo.
///  - Sobre: <c>EventType</c> (si viene debe ser exactamente "UpdateFirmwareDevice"),
///    <c>Timestamp</c> (el equipo lo ignora) y <c>Details</c> (si falta, se usa la raíz).
///  - Claves de <c>Details</c>: se emiten las DOS grafías de cada una —snake_case
///    (<c>firmware_version</c>, <c>part_index</c>, <c>total_parts</c>) y PascalCase
///    (<c>FirmwareVersion</c>, <c>PartIndex</c>, <c>TotalParts</c>)— por compatibilidad: el
///    parser del firmware acepta cualquiera (MQTTOTAv5.cpp:304-328), y distintas versiones del
///    firmware han preferido una u otra. <c>base64</c> y <c>sha256</c> van en una sola grafía.
///  - Índices 1-BASED e inclusivos: <c>part_index == 1</c> arranca la actualización y
///    <c>part_index == total_parts</c> la cierra.
///  - Se trocea PRIMERO en bytes y se codifica en base64 DESPUÉS, chunk a chunk. Base64
///    estándar RFC 4648; base64url (- y _) produce "Base64 decode error".
///  - <c>sha256</c>: hex minúscula de 64 caracteres sobre los bytes DECODIFICADOS de la
///    imagen completa, en orden. Es el mismo valor en todos los chunks.
///  - Las User Properties de MQTT 5 se DESCARTAN en el equipo (MQTTOTAv5.cpp:115), así que
///    toda la metadata va dentro del JSON.
/// </summary>
public static class OtaChunker
{
    /// <summary>
    /// Tamaño por defecto del trozo BINARIO, en bytes.
    ///
    /// El techo real no es de red sino del parser del equipo: la librería usa
    /// <c>StaticJsonDocument&lt;4096&gt;</c> (MQTTOTAv5.h:40) y, si el documento no cabe,
    /// descarta el mensaje EN SILENCIO (sólo log por serie, sin publicar error). 2048 bytes
    /// binarios son 2732 caracteres base64, que con el sobre deja margen holgado bajo 4096;
    /// es además el valor con el que el broker de referencia estaba en producción
    /// (FirmwareServices.cs:296). No conviene subirlo: el margen se agota cerca de 2900 B.
    /// </summary>
    public const int DefaultChunkSizeBytes = 2048;

    /// <summary>Valor obligatorio de <c>EventType</c> para que el equipo no descarte el mensaje.</summary>
    public const string FirmwareEventType = "UpdateFirmwareDevice";

    /// <summary>
    /// Mínimo que el equipo acepta como imagen completa: por debajo publica
    /// "Firmware too small" (MQTTOTAv5).
    /// </summary>
    public const int MinimumFirmwareBytes = 1024;

    /// <summary>
    /// Cabecera mínima que el primer trozo debe contener para que el equipo valide la imagen
    /// (esp_image_header_t + esp_image_segment_header_t + esp_app_desc_t).
    /// </summary>
    public const int MinimumFirstChunkBytes = 288;

    /// <summary>Primer byte de una imagen ESP32 válida.</summary>
    public const byte EspImageMagic = 0xE9;

    /// <summary>Tópico al que el equipo está suscrito para recibir el firmware.</summary>
    public static string TopicFor(string otaDeviceKey) => $"ota/{otaDeviceKey}";

    /// <summary>Tópicos en los que el equipo publica sus reportes.</summary>
    public static string ProgressTopicFor(string otaDeviceKey) => $"ota/{otaDeviceKey}/progress";
    public static string ErrorTopicFor(string otaDeviceKey) => $"ota/{otaDeviceKey}/error";
    public static string SuccessTopicFor(string otaDeviceKey) => $"ota/{otaDeviceKey}/success";
    public static string StateTopicFor(string otaDeviceKey) => $"ota/{otaDeviceKey}/state";

    /// <summary>Número de trozos en que se parte una imagen de <paramref name="sizeBytes"/>.</summary>
    public static int CountChunks(long sizeBytes, int chunkSizeBytes = DefaultChunkSizeBytes)
    {
        if (sizeBytes <= 0) return 0;
        if (chunkSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes));
        return (int)((sizeBytes + chunkSizeBytes - 1) / chunkSizeBytes);
    }

    /// <summary>
    /// SHA-256 de la imagen completa en hex MINÚSCULA de 64 caracteres, tal como lo compara
    /// el equipo (la comparación ignora mayúsculas, pero se emite en minúscula).
    /// </summary>
    public static string Sha256Hex(ReadOnlySpan<byte> firmware) =>
        Convert.ToHexString(SHA256.HashData(firmware)).ToLowerInvariant();

    /// <summary>
    /// Igual que <see cref="ValidateImage(ReadOnlySpan{byte})"/> pero sin necesitar la imagen
    /// completa en memoria: basta con sus primeros bytes y el tamaño total. Lo usa la subida,
    /// que escribe a disco en streaming y nunca tiene el binario entero en RAM.
    /// </summary>
    public static string? ValidateImage(ReadOnlySpan<byte> header, long totalLength)
    {
        if (totalLength < MinimumFirmwareBytes)
            return $"La imagen tiene {totalLength} bytes; el dispositivo rechaza menos de {MinimumFirmwareBytes} ('Firmware too small').";

        if (totalLength < MinimumFirstChunkBytes)
            return $"La imagen no alcanza los {MinimumFirstChunkBytes} bytes de cabecera que valida el dispositivo.";

        if (header.Length == 0)
            return "No se pudo leer la cabecera de la imagen.";

        if (header[0] != EspImageMagic)
            return $"El primer byte es 0x{header[0]:X2} y una imagen ESP32 válida empieza por 0x{EspImageMagic:X2} ('Invalid firmware image header').";

        return null;
    }

    /// <summary>
    /// Comprueba que la imagen es apta antes de empezar a emitir trozos, para no dejar al
    /// equipo a medias: el primer trozo tiene que llevar la cabecera y el total superar el
    /// mínimo. Devuelve el motivo del rechazo, o null si es apta.
    /// </summary>
    public static string? ValidateImage(ReadOnlySpan<byte> firmware)
    {
        if (firmware.Length < MinimumFirmwareBytes)
            return $"La imagen tiene {firmware.Length} bytes; el dispositivo rechaza menos de {MinimumFirmwareBytes} ('Firmware too small').";

        if (firmware.Length < MinimumFirstChunkBytes)
            return $"La imagen no alcanza los {MinimumFirstChunkBytes} bytes de cabecera que valida el dispositivo.";

        if (firmware[0] != EspImageMagic)
            return $"El primer byte es 0x{firmware[0]:X2} y una imagen ESP32 válida empieza por 0x{EspImageMagic:X2} ('Invalid firmware image header').";

        return null;
    }

    /// <summary>
    /// Serializa el mensaje del trozo <paramref name="partIndex"/> (1-based) listo para
    /// publicar en <see cref="TopicFor"/> con QoS 1 y SIN retain.
    ///
    /// Nota sobre retain: el broker de referencia publicaba con retain=true
    /// (MqttServerService.cs:63-68) y, como todos los trozos comparten tópico, dejaba el
    /// ÚLTIMO trozo pegado en el broker; cualquier equipo que reconectase después recibía
    /// ese trozo suelto en vez del primero. No se replica.
    /// </summary>
    public static string BuildChunkPayload(
        string firmwareVersion,
        int partIndex,
        int totalParts,
        ReadOnlySpan<byte> chunk,
        string sha256Hex,
        DateTime timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(firmwareVersion))
            throw new ArgumentException("El dispositivo descarta en silencio un mensaje sin firmware_version.", nameof(firmwareVersion));
        if (partIndex < 1)
            throw new ArgumentOutOfRangeException(nameof(partIndex), "Los índices del contrato son 1-based.");
        if (totalParts < partIndex)
            throw new ArgumentOutOfRangeException(nameof(totalParts), "total_parts debe incluir a part_index.");

        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("EventType", FirmwareEventType);
            writer.WriteString("Timestamp", timestampUtc.ToString("o"));

            writer.WriteStartObject("Details");
            writer.WriteString("firmware_version", firmwareVersion);
            writer.WriteString("FirmwareVersion", firmwareVersion);
            writer.WriteNumber("part_index", partIndex);
            writer.WriteNumber("PartIndex", partIndex);
            writer.WriteNumber("total_parts", totalParts);
            writer.WriteNumber("TotalParts", totalParts);
            string base64Str = Convert.ToBase64String(chunk);
            writer.WriteString("base64", base64Str);
            if (!string.IsNullOrWhiteSpace(sha256Hex))
            {
                writer.WriteString("sha256", sha256Hex);
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
