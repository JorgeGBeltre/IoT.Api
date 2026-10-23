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
    public string PublicHost { get; }
    public int Port { get; }
    public bool Tls { get; }

    public MqttConnectionInfoProvider(IConfiguration configuration)
    {
        string? host = configuration["MqttBroker:PublicHost"];
        PublicHost = string.IsNullOrWhiteSpace(host) ? "localhost" : host;

        Port = int.TryParse(configuration["MqttBroker:Port"], out var p) ? p : 1883;
        Tls = bool.TryParse(configuration["MqttBroker:Tls"], out var t) && t;
    }
}
