using System.Text;

namespace IoT.Protocols.Tcp;

/// <summary>Utilidades de framing para protocolos ASCII sobre TCP (delimitadores).</summary>
internal static class TcpFraming
{
    private const int MaxFrameBytes = 8192; // descarta basura si no aparece terminador

    /// <summary>
    /// Extrae una trama ASCII que termina en <paramref name="terminator"/> (incluido).
    /// Quita del buffer los bytes consumidos (incluido el terminador y cualquier CR/LF trailing).
    /// </summary>
    public static bool TryReadUntil(List<byte> buffer, byte terminator, out string frameText)
    {
        frameText = string.Empty;
        int idx = buffer.IndexOf(terminator);
        if (idx < 0)
        {
            // Sin terminador: si el buffer crece sin límite, descartar para no acumular basura.
            if (buffer.Count > MaxFrameBytes) buffer.Clear();
            return false;
        }

        int end = idx + 1;
        frameText = Encoding.ASCII.GetString(buffer.ToArray(), 0, end).Trim('\r', '\n', ' ');
        // Consumir la trama + CR/LF inmediatos que sigan.
        while (end < buffer.Count && (buffer[end] == (byte)'\r' || buffer[end] == (byte)'\n')) end++;
        buffer.RemoveRange(0, end);
        return frameText.Length > 0;
    }

    /// <summary>
    /// Extrae una trama balanceada entre '(' y ')' (formato Jointech). Descarta bytes previos
    /// al primer '('. Quita del buffer hasta el ')' de cierre (incluido).
    /// </summary>
    public static bool TryReadParenFrame(List<byte> buffer, out string frameText)
    {
        frameText = string.Empty;
        int open = buffer.IndexOf((byte)'(');
        if (open < 0)
        {
            if (buffer.Count > MaxFrameBytes) buffer.Clear();
            return false;
        }
        int close = buffer.IndexOf((byte)')', open + 1);
        if (close < 0)
        {
            // Sin ')' de cierre: descartar basura previa al '(' y, si la trama sin terminar
            // crece sin límite, vaciar para no permitir agotamiento de memoria (DoS).
            if (open > 0) buffer.RemoveRange(0, open);
            if (buffer.Count > MaxFrameBytes) buffer.Clear();
            return false;
        }

        int len = close - open + 1;
        frameText = Encoding.ASCII.GetString(buffer.ToArray(), open, len);
        int end = close + 1;
        while (end < buffer.Count && (buffer[end] == (byte)'\r' || buffer[end] == (byte)'\n')) end++;
        buffer.RemoveRange(0, end);
        return true;
    }

    private static int IndexOf(this List<byte> buffer, byte value, int start = 0)
    {
        for (int i = start; i < buffer.Count; i++)
            if (buffer[i] == value) return i;
        return -1;
    }
}
