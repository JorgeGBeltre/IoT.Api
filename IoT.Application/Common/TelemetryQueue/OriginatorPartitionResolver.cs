using System.Text;

namespace IoT.Application.Common.TelemetryQueue;

/// <summary>
/// Resuelve la clave del originador y calcula la partición determinista usando FNV-1a 32-bit (ADR-016).
/// Garantiza que todos los mensajes de un mismo dispositivo (originator) se procesen en la misma partición
/// y por tanto de forma estrictamente secuencial (ThingsBoard Pattern 2: Sequential by Originator).
/// </summary>
public static class OriginatorPartitionResolver
{
    private const uint FnvOffsetBasis = 2166136261u;
    private const uint FnvPrime = 16777619u;

    /// <summary>
    /// Calcula el índice de partición determinista en el rango [0, partitionCount - 1].
    /// </summary>
    public static int GetPartitionIndex(string originatorKey, int partitionCount)
    {
        if (partitionCount <= 1) return 0;
        if (string.IsNullOrWhiteSpace(originatorKey)) return 0;

        uint hash = ComputeFnv1a32(originatorKey);
        return (int)(hash % (uint)partitionCount);
    }

    /// <summary>
    /// Algoritmo FNV-1a de 32 bits determinista (estable entre reinicios y procesos).
    /// </summary>
    public static uint ComputeFnv1a32(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        uint hash = FnvOffsetBasis;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= FnvPrime;
        }
        return hash;
    }

    /// <summary>
    /// Resuelve la identidad del emisor (ChipId, IMEI o tópico) que actúa como clave de particionado.
    /// </summary>
    public static string ResolveOriginatorKey(string topic, string? transportDeviceIdentifier)
    {
        if (!string.IsNullOrWhiteSpace(transportDeviceIdentifier))
        {
            return transportDeviceIdentifier.Trim();
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            return "unknown";
        }

        string trimmed = topic.Trim();
        string[] parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Patrón: devices/{chipId}/...
        if (parts.Length >= 2 && string.Equals(parts[0], "devices", StringComparison.OrdinalIgnoreCase))
        {
            return parts[1];
        }

        // Patrón: relays/devices/{chipId}/action
        if (parts.Length >= 3 &&
            string.Equals(parts[0], "relays", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parts[1], "devices", StringComparison.OrdinalIgnoreCase))
        {
            return parts[2];
        }

        // Patrón: glp/telemetria/{chipId} o relay/telemetria/{chipId}
        if (parts.Length >= 3 &&
            (string.Equals(parts[0], "glp", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(parts[0], "relay", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(parts[1], "telemetria", StringComparison.OrdinalIgnoreCase))
        {
            return parts[2];
        }

        // Patrón: telemetry/{chipId}
        if (parts.Length == 2 && string.Equals(parts[0], "telemetry", StringComparison.OrdinalIgnoreCase))
        {
            return parts[1];
        }

        // Fallback: usar el último segmento si hay varios, o el tópico completo
        return parts.Length > 0 ? parts[^1] : trimmed;
    }
}
