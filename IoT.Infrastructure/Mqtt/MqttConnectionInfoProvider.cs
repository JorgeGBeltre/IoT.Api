using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using IoT.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;

namespace IoT.Infrastructure.Mqtt;

/// <summary>
/// Lee los datos de conexión públicos del broker MQTT embebido desde la configuración:
///   MqttBroker:PublicHost  (host/dominio público; por defecto "localhost")
///   MqttBroker:Port        (puerto; por defecto 1883)
///   MqttBroker:Tls         (requiere TLS; por defecto false)
/// </summary>
public sealed class MqttConnectionInfoProvider : IMqttConnectionInfoProvider
{
    private static readonly Regex SafeHostnamePattern =
        new(@"^[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*$",
            RegexOptions.Compiled);

    public string PublicHost { get; }
    public int Port { get; }
    public bool Tls { get; }

    public MqttConnectionInfoProvider(IConfiguration configuration)
    {
        string? host = configuration["MqttBroker:PublicHost"];
        PublicHost = string.IsNullOrWhiteSpace(host) ? "localhost" : host.Trim();

        Port = int.TryParse(configuration["MqttBroker:Port"], out var p) ? p : 1883;
        Tls = bool.TryParse(configuration["MqttBroker:Tls"], out var t) && t;
    }

    /// <summary>
    /// Resuelve de forma segura el host efectivo al que los dispositivos deben conectar:
    /// 1) Si <paramref name="configuredHost"/> es loopback (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>,
    ///    <c>0.0.0.0</c>) o <c>auto</c>, y la petición HTTP entró por una IP/host no-loopback válido,
    ///    usa el host de la petición (evita inyectar <c>localhost</c> a un ESP32).
    /// 2) Si tanto <paramref name="configuredHost"/> como <paramref name="requestHost"/> son IPs privadas
    ///    RFC1918 en subredes /24 distintas (p.ej. el servidor cambió de red Wi-Fi/LAN), prioriza la IP
    ///    privada por la que el cliente alcanzó realmente la API.
    /// 3) En cualquier otro caso (p.ej. un dominio público configurado en producción), respeta
    ///    estrictamente <paramref name="configuredHost"/>.
    /// </summary>
    public static string ResolveEffectiveHost(string? configuredHost, string? requestHost)
    {
        string configured = string.IsNullOrWhiteSpace(configuredHost) ? "localhost" : configuredHost.Trim();
        if (!TrySanitizeHost(requestHost, out string cleanRequestHost) || IsLoopbackOrWildcard(cleanRequestHost))
        {
            return string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase) ? "localhost" : configured;
        }

        if (IsLoopbackOrWildcard(configured) || string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return cleanRequestHost;
        }

        if (TryParsePrivateIpv4(configured, out byte[] cfgBytes) &&
            TryParsePrivateIpv4(cleanRequestHost, out byte[] reqBytes))
        {
            bool differentSlash24 = cfgBytes[0] != reqBytes[0] ||
                                    cfgBytes[1] != reqBytes[1] ||
                                    cfgBytes[2] != reqBytes[2];
            if (differentSlash24)
            {
                return cleanRequestHost;
            }
        }

        return configured;
    }

    private static bool TrySanitizeHost(string? rawHost, out string sanitized)
    {
        sanitized = string.Empty;
        if (string.IsNullOrWhiteSpace(rawHost))
        {
            return false;
        }

        string candidate = rawHost.Trim().Trim('[', ']');
        if (candidate.Length == 0 || candidate.Length > 253)
        {
            return false;
        }

        if (IPAddress.TryParse(candidate, out var ip))
        {
            sanitized = ip.ToString();
            return true;
        }

        if (SafeHostnamePattern.IsMatch(candidate))
        {
            sanitized = candidate;
            return true;
        }

        return false;
    }

    private static bool IsLoopbackOrWildcard(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
        string.Equals(host, "::1", StringComparison.Ordinal) ||
        string.Equals(host, "0.0.0.0", StringComparison.Ordinal) ||
        string.Equals(host, "::", StringComparison.Ordinal);

    private static bool TryParsePrivateIpv4(string host, out byte[] octets)
    {
        octets = Array.Empty<byte>();
        if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] b = ip.GetAddressBytes();
        bool isRfc1918 = b[0] == 10 ||
                         (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                         (b[0] == 192 && b[1] == 168);
        if (!isRfc1918)
        {
            return false;
        }

        octets = b;
        return true;
    }
}
